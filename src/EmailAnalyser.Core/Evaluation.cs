using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmailAnalyser.Core;

/// <summary>Synthetic test messages with expected labels (eval/synthetic-emails.json).</summary>
public sealed class EvalSet
{
    [JsonPropertyName("now")] public DateTimeOffset Now { get; init; }
    [JsonPropertyName("cases")] public List<EvalCase> Cases { get; init; } = [];

    public static EvalSet Load(string path) =>
        JsonSerializer.Deserialize<EvalSet>(File.ReadAllText(path, Encoding.UTF8))
        ?? throw new InvalidDataException($"Testiaineistoa ei voitu lukea: {path}");
}

public sealed class EvalCase
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("mailbox")] public string Mailbox { get; init; } = "";
    [JsonPropertyName("from")] public string From { get; init; } = "";
    [JsonPropertyName("fromAddress")] public string FromAddress { get; init; } = "";
    [JsonPropertyName("to")] public string To { get; init; } = "";
    [JsonPropertyName("cc")] public string Cc { get; init; } = "";
    [JsonPropertyName("received")] public DateTimeOffset Received { get; init; }
    [JsonPropertyName("subject")] public string Subject { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
    [JsonPropertyName("fillerChars")] public int FillerChars { get; init; }
    [JsonPropertyName("expect")] public EvalExpectation Expect { get; init; } = new();
    [JsonPropertyName("note")] public string? Note { get; init; }

    public MessageForAssessment ToMessage()
    {
        var body = Body;
        if (FillerChars > 0)
        {
            var filler = new StringBuilder();
            for (var i = 1; filler.Length < FillerChars; i++)
            {
                filler.Append($"Kohta {i}: Keskusteltiin projektin aikataulusta, resursseista ja riskeistä. Päätettiin jatkaa nykyisellä suunnitelmalla ja palata asiaan seuraavassa kokouksessa.\n");
            }

            body = body + "\n\n" + filler;
        }

        return new MessageForAssessment(Mailbox, From, FromAddress, To, Cc, Received, Subject, body);
    }
}

public sealed class EvalExpectation
{
    [JsonPropertyName("toimenpide")] public List<string> Actions { get; init; } = [];
    [JsonPropertyName("kiireellisyys")] public List<string>? Urgencies { get; init; }
    /// <summary>"*" = any, null = must be null, otherwise yyyy-MM-dd.</summary>
    [JsonPropertyName("maaraaika")] public string? Deadline { get; init; } = "*";
    [JsonPropertyName("aihe")] public List<string>? Topics { get; init; }
    [JsonPropertyName("tarkistettava")] public bool? NeedsReview { get; init; }

    public bool ExpectsAttention => Actions.Any(Labels.AttentionActions.Contains);
}

public sealed record EvalCaseResult(
    string Id,
    bool Valid,
    bool ActionOk,
    bool? UrgencyOk,
    bool? DeadlineOk,
    bool? TopicOk,
    bool? ReviewOk,
    bool ExpectsAttention,
    bool Flagged,
    Assessment? Assessment,
    IReadOnlyList<string> Errors,
    TimeSpan Duration,
    int Attempts,
    bool Truncated);

public sealed record EvalModelResult(string Model, string PromptVersion, double LoadSeconds, int? GpuLoadedMiB, IReadOnlyList<EvalCaseResult> Cases)
{
    public int Valid => Cases.Count(c => c.Valid);
    public double ActionAccuracy => Ratio(Cases.Count(c => c.ActionOk), Cases.Count);
    public double UrgencyAccuracy => Ratio(Cases.Count(c => c.UrgencyOk == true), Cases.Count(c => c.UrgencyOk is not null));
    public double DeadlineAccuracy => Ratio(Cases.Count(c => c.DeadlineOk == true), Cases.Count(c => c.DeadlineOk is not null));
    public double TopicAccuracy => Ratio(Cases.Count(c => c.TopicOk == true), Cases.Count(c => c.TopicOk is not null));
    public int AttentionExpected => Cases.Count(c => c.ExpectsAttention);
    public int AttentionFound => Cases.Count(c => c.ExpectsAttention && c.Flagged);
    public int NoAttentionExpected => Cases.Count(c => !c.ExpectsAttention);
    public int FalseAlarms => Cases.Count(c => !c.ExpectsAttention && c.Flagged && !c.ActionOk);
    public TimeSpan MedianDuration =>
        Cases.Count == 0 ? TimeSpan.Zero : Cases.Select(c => c.Duration).Order().ElementAt(Cases.Count / 2);

    private static double Ratio(int a, int b) => b == 0 ? double.NaN : (double)a / b;
}

/// <summary>
/// Runs the synthetic set through the production classifier for one model: same
/// text preparation, prompt, schema and validation as real messages.
/// </summary>
public sealed class EvalRunner(ILanguageModelServer server, IGpuActivityProbe gpuActivity, PromptTemplate prompt, ClassifierOptions options, IGpuMemoryProbe? gpuMemory = null)
{
    public async Task<EvalModelResult> RunAsync(
        string modelKey, EvalSet set, Action<EvalCaseResult>? progress, CancellationToken cancellationToken)
    {
        if (await gpuActivity.IsGeneratingAsync(cancellationToken))
        {
            throw new InvalidOperationException("Automatic1111 generoi kuvaa; vertailua ei aloiteta.");
        }

        var loaded = await server.GetLoadedInstancesAsync(cancellationToken);
        if (loaded.Count > 0)
        {
            throw new InvalidOperationException($"LM Studiossa on jo ladattuna malli ({string.Join(", ", loaded.Select(l => l.ModelKey))}).");
        }

        var load = await server.LoadAsync(modelKey, options.ContextLength, cancellationToken);
        var gpu = gpuMemory?.UsedMiB();
        var classifier = new Classifier(server, prompt, options);
        var results = new List<EvalCaseResult>();
        try
        {
            foreach (var c in set.Cases)
            {
                var outcome = await classifier.ClassifyAsync(load.InstanceId, c.ToMessage(), set.Now, cancellationToken);
                var result = Score(c, outcome);
                results.Add(result);
                progress?.Invoke(result);
            }
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await server.UnloadAsync(load.InstanceId, timeout.Token);
        }

        return new EvalModelResult(modelKey, prompt.Version, load.LoadSeconds, gpu, results);
    }

    public static EvalCaseResult Score(EvalCase c, ClassificationOutcome outcome)
    {
        var a = outcome.Assessment;
        var e = c.Expect;
        bool? deadlineOk = e.Deadline == "*"
            ? null
            : a is not null && (e.Deadline is null ? a.Deadline is null : a.Deadline?.ToString("yyyy-MM-dd") == e.Deadline);

        return new EvalCaseResult(
            c.Id,
            a is not null,
            a is not null && e.Actions.Contains(a.Action),
            e.Urgencies is null ? null : a is not null && e.Urgencies.Contains(a.Urgency),
            deadlineOk,
            e.Topics is null ? null : a is not null && e.Topics.Contains(a.Topic),
            e.NeedsReview is null ? null : a is not null && a.NeedsReview == e.NeedsReview,
            e.ExpectsAttention,
            a is not null && (Labels.AttentionActions.Contains(a.Action) || a.Action == "tarkista"),
            a,
            outcome.Errors,
            outcome.Duration,
            outcome.Attempts,
            outcome.Prepared.Truncated);
    }
}
