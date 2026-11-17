using System.Globalization;
using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

/// <summary>Metadata-only status for the view and the command line.</summary>
public sealed record MonitorStatus(
    DateTimeOffset Time,
    bool Paused,
    string? Activity,
    int Queued,
    DateTimeOffset? LastScan,
    string? Problem,
    string? DeferralReason,
    DateTimeOffset? DeferredSince,
    bool DeferralWarning,
    BatchResult? LastBatch,
    DateTimeOffset? LastBatchTime,
    CategorySyncResult? LastSync,
    int NewAssessments,
    int Discovered);

public static class MonitorText
{
    /// <summary>One status line in Finnish; metadata only.</summary>
    public static string Describe(MonitorStatus s)
    {
        var parts = new List<string>
        {
            s.Paused ? "Seuranta keskeytetty" : s.Activity ?? "Seuranta odottaa",
            $"jonossa {s.Queued}",
        };
        if (s.LastScan is { } scan)
        {
            parts.Add($"haettu {scan.ToLocalTime():HH.mm}");
        }

        if (s.DeferralReason is not null)
        {
            parts.Add($"{(s.DeferralWarning ? "VAROITUS: lykätty yli tunnin" : "lykätty")} ({s.DeferralReason}, alkaen {s.DeferredSince?.ToLocalTime():HH.mm})");
        }

        if (s.LastBatch is { } b && s.LastBatchTime is { } t)
        {
            parts.Add($"viimeisin erä {t.ToLocalTime():HH.mm}: arvioitu {b.Assessed + b.Invalid}");
        }

        if (s.LastSync is { } sync && (sync.Written + sync.Blocked + sync.Failed) > 0)
        {
            parts.Add($"luokat: kirjoitettu {sync.Written}, estetty {sync.Blocked}, epäonnistui {sync.Failed}");
        }

        if (s.Problem is not null)
        {
            parts.Add($"ongelma: {s.Problem}");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Continuous monitoring: every few minutes finds new Inbox mail, starts a batch
/// when enough has queued or the oldest has waited long enough, defers politely
/// when the GPU is busy, and applies Outlook categories if enabled. All state that
/// matters lives in the database, so sleep, restarts and outages lose nothing.
/// </summary>
public sealed class MonitorService(
    AnalyserStore store,
    IMailboxScanner scanner,
    BatchRunner runner,
    CategorySync? categories,
    Func<AppSettings> settings,
    TimeProvider time)
{
    private const string RetentionKey = "retention_last_utc";

    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _tickCts;
    private int _processNow;
    private volatile bool _paused;
    private string? _activity;
    private DateTimeOffset? _lastScan;
    private string? _problem;
    private string? _deferralReason;
    private DateTimeOffset? _deferredSince;
    private DateTimeOffset? _retryAfter;
    private BatchResult? _lastBatch;
    private DateTimeOffset? _lastBatchTime;
    private CategorySyncResult? _lastSync;
    private int _newAssessments;
    private int _discovered;
    private int _queued;

    public event Action<MonitorStatus>? StatusChanged;

    public bool Paused => _paused;

    /// <summary>Stops monitoring; a running batch is stopped and its model unloaded.</summary>
    public void Pause()
    {
        _paused = true;
        _tickCts?.Cancel();
        Publish();
    }

    public void Resume()
    {
        _paused = false;
        Wake();
    }

    /// <summary>Scan and process the queue now, regardless of the batch thresholds and deferral wait.</summary>
    public void ProcessNow()
    {
        Interlocked.Exchange(ref _processNow, 1);
        Wake();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // This round serves any pending wake-up. Without draining it, a "process now"
            // given before the round would skip the pause after it, and the GPU would not
            // be released between two batches.
            _wake.Wait(0);
            var processNow = Interlocked.Exchange(ref _processNow, 0) == 1;
            using (var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                _tickCts = tick;
                try
                {
                    await TickAsync(processNow, tick.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Paused in the middle of a tick.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The loop must survive anything; messages here carry no message content.
                    _problem = ex.Message;
                    _activity = null;
                    Publish();
                }
                finally
                {
                    _tickCts = null;
                }
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, settings().MonitorIntervalMinutes)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One round: retention, scan, batch if due, categories. Public for tests and the command line.</summary>
    public async Task<MonitorStatus> TickAsync(bool processNow, CancellationToken cancellationToken)
    {
        if (_paused)
        {
            return Publish();
        }

        var s = settings();
        var now = time.GetUtcNow();
        _problem = null;
        ApplyRetentionIfDue(s, now);

        _activity = "Haetaan uusia viestejä";
        Publish();
        var outlookAvailable = await ScanAsync(s, now, cancellationToken);

        var (queued, oldest) = store.QueueInfo();
        _queued = queued;
        var due = queued > 0 && (processNow || queued >= s.BatchMinQueue || oldest <= now - TimeSpan.FromMinutes(s.BatchMaxWaitMinutes));
        var waitingAfterDeferral = !processNow && _retryAfter is { } retry && now < retry;
        if (due && outlookAvailable && !waitingAfterDeferral)
        {
            await RunBatchAsync(s, cancellationToken);
        }

        if (outlookAvailable && categories is not null && s.CategoryMode != CategoryMode.Off)
        {
            _activity = "Päivitetään Outlook-luokkia";
            Publish();
            try
            {
                _lastSync = await categories.SyncAsync(s.CategoryMode, s.CategoryTestSubject, cancellationToken);
            }
            catch (MessageSourceUnavailableException ex)
            {
                _problem ??= ex.Message;
            }
        }

        _activity = null;
        return Publish();
    }

    private async Task<bool> ScanAsync(AppSettings s, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var monitored in s.Stores)
        {
            var mark = store.GetScanMark(monitored.StoreId);
            // Start from the newest message already seen, not the clock: mail that syncs late
            // after a network outage or sleep still has a ReceivedTime after the mark.
            var since = mark is { } m ? m - TimeSpan.FromMinutes(s.ScanOverlapMinutes) : now - TimeSpan.FromDays(s.CatchUpDays);
            IReadOnlyList<DiscoveredMessage> found;
            try
            {
                found = await scanner.ListInboxSinceAsync(monitored.StoreId, since, s.ScanMaxPerStore, cancellationToken);
            }
            catch (MessageSourceUnavailableException ex)
            {
                _problem = ex.Message;
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{monitored.Name}: {ex.Message}");
                continue;
            }

            _discovered += found.Count(message => store.Enqueue(message, now));
            store.SetScanMark(monitored.StoreId, found.Count > 0 ? found.Max(f => f.Received) : mark ?? since, now);
        }

        _lastScan = now;
        if (problems.Count > 0)
        {
            _problem = "Laatikon luku epäonnistui: " + string.Join("; ", problems);
        }

        return true;
    }

    private async Task RunBatchAsync(AppSettings s, CancellationToken cancellationToken)
    {
        _activity = "Käsitellään jonoa";
        Publish();
        var options = new BatchOptions(s.Model, MaxDuration: TimeSpan.FromMinutes(s.BatchMaxMinutes));
        var result = await runner.RunAsync(options, p =>
        {
            _activity = $"Käsitellään {p.Index}/{p.Total}";
            Publish();
        }, cancellationToken);

        var now = time.GetUtcNow();
        _lastBatch = result;
        _lastBatchTime = now;
        _newAssessments += result.Assessed + result.Invalid;
        _queued = result.Remaining;
        var retryLater = result.Outcome == BatchOutcome.Deferred ||
                         result.Outcome == BatchOutcome.Stopped && result.Reason is not (BatchStopReasons.MaxDuration or BatchStopReasons.Cancelled);
        if (retryLater)
        {
            _deferralReason = result.Reason;
            _deferredSince ??= now;
            _retryAfter = now + TimeSpan.FromMinutes(s.DeferRetryMinutes);
        }
        else
        {
            _deferralReason = null;
            _deferredSince = null;
            _retryAfter = null;
        }
    }

    private void ApplyRetentionIfDue(AppSettings s, DateTimeOffset now)
    {
        if (s.RetentionDays <= 0)
        {
            return;
        }

        var last = store.GetAppState(RetentionKey) is { } value
            ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : (DateTimeOffset?)null;
        if (last is { } l && now - l < TimeSpan.FromDays(1))
        {
            return;
        }

        store.ApplyRetention(now - TimeSpan.FromDays(s.RetentionDays));
        store.SetAppState(RetentionKey, now.ToString("O", CultureInfo.InvariantCulture));
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private MonitorStatus Publish()
    {
        var now = time.GetUtcNow();
        var status = new MonitorStatus(
            now, _paused, _paused ? null : _activity, _queued, _lastScan, _problem,
            _deferralReason, _deferredSince, _deferredSince is { } since && now - since >= TimeSpan.FromHours(1),
            _lastBatch, _lastBatchTime, _lastSync, _newAssessments, _discovered);
        StatusChanged?.Invoke(status);
        return status;
    }
}
