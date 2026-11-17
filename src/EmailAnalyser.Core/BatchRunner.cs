using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

public enum BatchOutcome
{
    NothingQueued,
    Deferred,
    Completed,
    Stopped,
}

public sealed record BatchResult(BatchOutcome Outcome, string? Reason, int Assessed, int Invalid, int Unavailable, int Remaining, int Failed = 0);

public sealed record BatchOptions(string ModelKey, int MaxMessages = 200, TimeSpan? MaxDuration = null)
{
    public TimeSpan Duration => MaxDuration ?? TimeSpan.FromMinutes(15);
}

/// <summary>Progress callback with metadata only (never content).</summary>
public sealed record MessageProgress(int Index, int Total, string Status, string? Action, string? Urgency, TimeSpan Duration);

/// <summary>
/// One batch: checks that the GPU is free, loads the model, classifies queued
/// messages and always unloads the instance it loaded. Messages stay queued when
/// the model server fails or the batch is deferred or stopped.
/// </summary>
public sealed class BatchRunner(
    AnalyserStore store,
    ILanguageModelServer server,
    IGpuActivityProbe gpuActivity,
    IMessageSource source,
    Classifier classifier,
    TimeProvider time,
    IGpuMemoryProbe? gpuMemory = null,
    IPrivacyCheck? privacy = null)
{
    public async Task<BatchResult> RunAsync(BatchOptions options, Action<MessageProgress>? progress, CancellationToken cancellationToken)
    {
        var queued = store.GetQueued(options.MaxMessages);
        if (queued.Count == 0)
        {
            return new BatchResult(BatchOutcome.NothingQueued, null, 0, 0, 0, 0);
        }

        var deferral = await CheckPreconditionsAsync(cancellationToken);
        if (deferral is not null)
        {
            store.RecordDeferred(options.ModelKey, deferral, time.GetUtcNow());
            return new BatchResult(BatchOutcome.Deferred, deferral, 0, 0, 0, store.GetQueued(int.MaxValue).Count);
        }

        var batchId = store.StartBatch(options.ModelKey, gpuMemory?.UsedMiB(), time.GetUtcNow());
        string? instance = null;
        int assessed = 0, invalid = 0, unavailable = 0, failed = 0;
        var outcome = BatchOutcome.Completed;
        string? reason = null;
        try
        {
            var load = await server.LoadAsync(options.ModelKey, classifier.Options.ContextLength, cancellationToken);
            instance = load.InstanceId;
            store.RecordLoad(batchId, instance, load.LoadSeconds, gpuMemory?.UsedMiB());

            var deadline = time.GetUtcNow() + options.Duration;
            var settingsJson = classifier.Options.ToSettingsJson(options.ModelKey);
            for (var i = 0; i < queued.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (time.GetUtcNow() >= deadline)
                {
                    (outcome, reason) = (BatchOutcome.Stopped, "erän enimmäiskesto täyttyi");
                    break;
                }

                if (await gpuActivity.IsGeneratingAsync(cancellationToken))
                {
                    (outcome, reason) = (BatchOutcome.Stopped, "Automatic1111 aloitti generoinnin");
                    break;
                }

                var message = queued[i];
                var read = await source.ReadAsync(message.Reference, message.Mailbox, cancellationToken);
                if (read.Status != ReadStatus.Found)
                {
                    store.MarkUnavailable(message, read.Status, read.Reason ?? "", time.GetUtcNow());
                    unavailable++;
                    progress?.Invoke(new MessageProgress(i + 1, queued.Count, read.Status == ReadStatus.Gone ? "poistunut" : "ei luettavissa", null, null, TimeSpan.Zero));
                    continue;
                }

                ClassificationOutcome result;
                try
                {
                    result = await classifier.ClassifyAsync(instance, read.Message!, HelsinkiTime.Now(time), cancellationToken);
                }
                catch (ModelServerException ex) when (ex.Kind == ModelFailureKind.Rejected && assessed + invalid > 0)
                {
                    // The server has worked in this batch, so the refusal is about this message
                    // (e.g. too many tokens). Fail it instead of blocking the queue.
                    store.RecordFailure(message, ex.Message, failNow: true, time.GetUtcNow());
                    failed++;
                    progress?.Invoke(new MessageProgress(i + 1, queued.Count, "mallipalvelin hylkäsi", null, null, TimeSpan.Zero));
                    continue;
                }
                catch (ModelServerException ex) when (ex.Kind != ModelFailureKind.Unavailable)
                {
                    // Count the attempt against the message so one message cannot stop every batch.
                    store.RecordFailure(message, ex.Message, failNow: false, time.GetUtcNow());
                    throw;
                }

                store.SaveAssessment(message, read.Message!, result, batchId, options.ModelKey, classifier.Prompt.Version, settingsJson, time.GetUtcNow());
                if (result.IsValid)
                {
                    assessed++;
                }
                else
                {
                    invalid++;
                }

                progress?.Invoke(new MessageProgress(
                    i + 1, queued.Count, result.IsValid ? "arvioitu" : "virheellinen vastaus",
                    result.Assessment?.Action, result.Assessment?.Urgency, result.Duration));
            }
        }
        catch (ModelServerException ex)
        {
            (outcome, reason) = (BatchOutcome.Stopped, ex.Message);
        }
        catch (OperationCanceledException)
        {
            (outcome, reason) = (BatchOutcome.Stopped, "keskeytetty");
        }
        finally
        {
            if (instance is not null && await TryUnloadAsync(instance))
            {
                store.RecordUnload(batchId);
            }

            store.FinishBatch(batchId, OutcomeName(outcome), reason, gpuMemory?.UsedMiB(), time.GetUtcNow());
        }

        var remaining = store.GetQueued(int.MaxValue).Count;
        return new BatchResult(outcome, reason, assessed, invalid, unavailable, remaining, failed);
    }

    /// <summary>Returns a deferral reason, or null when the batch may start.</summary>
    private async Task<string?> CheckPreconditionsAsync(CancellationToken cancellationToken)
    {
        if (privacy?.Problem() is { } privacyProblem)
        {
            return privacyProblem;
        }

        if (await gpuActivity.IsGeneratingAsync(cancellationToken))
        {
            return "Automatic1111 generoi kuvaa";
        }

        IReadOnlyList<LoadedInstance> loaded;
        try
        {
            await ReleaseOwnOrphansAsync(cancellationToken);
            loaded = await server.GetLoadedInstancesAsync(cancellationToken);
        }
        catch (ModelServerException ex)
        {
            return ex.Message;
        }

        return loaded.Count > 0
            ? $"LM Studiossa on jo ladattuna malli ({string.Join(", ", loaded.Select(l => l.ModelKey))})"
            : null;
    }

    /// <summary>Unloads instances an earlier run loaded but did not confirm unloading.</summary>
    private async Task ReleaseOwnOrphansAsync(CancellationToken cancellationToken)
    {
        var orphans = store.GetUnreleasedInstances();
        if (orphans.Count == 0)
        {
            return;
        }

        var loaded = await server.GetLoadedInstancesAsync(cancellationToken);
        foreach (var (batchId, instanceId) in orphans)
        {
            if (loaded.Any(l => l.InstanceId == instanceId))
            {
                await server.UnloadAsync(instanceId, cancellationToken);
            }

            store.RecordUnload(batchId);
        }
    }

    private async Task<bool> TryUnloadAsync(string instance)
    {
        try
        {
            // Not tied to the caller's token: unloading must happen even after cancellation.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await server.UnloadAsync(instance, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is ModelServerException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string OutcomeName(BatchOutcome outcome) => outcome switch
    {
        BatchOutcome.Completed => "valmis",
        BatchOutcome.Stopped => "keskeytyi",
        BatchOutcome.Deferred => "lykätty",
        _ => "tyhjä",
    };
}
