using System.Text.Json;

namespace EmailAnalyser.Core;

public sealed record ClassifierOptions(int MaxBodyChars = TextPreparation.DefaultMaxChars, int MaxTokens = 600, int ContextLength = 8192)
{
    /// <summary>Stored with every assessment so results can be traced to their settings.</summary>
    public string ToSettingsJson(string modelKey) => JsonSerializer.Serialize(new
    {
        model = modelKey,
        contextLength = ContextLength,
        maxBodyChars = MaxBodyChars,
        maxTokens = MaxTokens,
        temperature = 0,
        reasoning = "none",
    });
}

public sealed record ClassificationOutcome(
    Assessment? Assessment,
    IReadOnlyList<string> Errors,
    PreparedText Prepared,
    int Attempts,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Duration)
{
    public bool IsValid => Assessment is not null;
}

public sealed class Classifier(ILanguageModelServer server, PromptTemplate prompt, ClassifierOptions options)
{
    public const int MaxAttempts = 2;

    public PromptTemplate Prompt => prompt;
    public ClassifierOptions Options => options;

    /// <summary>
    /// Classifies one message. An invalid answer is retried once; a model server
    /// failure throws <see cref="ModelServerException"/> so the caller can leave the
    /// message queued.
    /// </summary>
    public async Task<ClassificationOutcome> ClassifyAsync(
        string instance, MessageForAssessment message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prepared = TextPreparation.Prepare(message.Body, options.MaxBodyChars);
        var user = prompt.BuildUser(message, prepared, now);
        var request = new ChatRequest(instance, prompt.System, user, prompt.SchemaJson, options.MaxTokens);

        IReadOnlyList<string> errors = [];
        var duration = TimeSpan.Zero;
        int? promptTokens = null, completionTokens = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var response = await server.ChatAsync(request, cancellationToken);
            if (!string.Equals(response.Model, instance, StringComparison.OrdinalIgnoreCase))
            {
                // Something other than the instance this application loaded answered (R16).
                throw new ModelServerException("Mallipalvelin vastasi eri mallilla kuin sovellus latasi.", ModelFailureKind.ServerError);
            }

            duration += response.Elapsed;
            promptTokens = response.PromptTokens;
            completionTokens = (completionTokens ?? 0) + (response.CompletionTokens ?? 0);

            var validation = response.FinishReason == "length"
                ? new ValidationResult(null, ["vastaus katkesi tokenirajaan"])
                : AssessmentValidator.Validate(response.Content, prepared.Truncated);
            if (validation.IsValid)
            {
                var assessment = ApplyProgramChecks(validation.Assessment!, message);
                return new ClassificationOutcome(assessment, [], prepared, attempt, promptTokens, completionTokens, duration);
            }

            errors = validation.Errors;
        }

        return new ClassificationOutcome(null, errors, prepared, MaxAttempts, promptTokens, completionTokens, duration);
    }

    /// <summary>
    /// Program-level checks that the model cannot override: a message that addresses
    /// the classifier, or whose sender fails authentication, does not get the model's
    /// judgement. The action is forced to review and the reason is recorded.
    /// </summary>
    public static Assessment ApplyProgramChecks(Assessment assessment, MessageForAssessment message)
    {
        if (InjectionHeuristics.Detect(message.Subject + "\n" + message.Body))
        {
            assessment = ForceReview(assessment, InjectionHeuristics.ReviewReason,
                $"Ohjelma: viestissä on tekoälylle osoitettua tekstiä, joten toimenpide on tarkista (mallin ehdotus: {assessment.Action}). ");
        }

        var sender = SenderAuthentication.Evaluate(message.SenderName, message.SenderAddress, message.Headers);
        if (sender.Suspicious)
        {
            assessment = ForceReview(assessment, SenderAuthentication.ReviewReason,
                $"Ohjelma: lähettäjän tunnistus ei täsmää ({string.Join(", ", sender.Signals)}), joten toimenpide on tarkista. ");
        }

        return assessment;
    }

    private static Assessment ForceReview(Assessment assessment, string reason, string note)
    {
        var rationale = note + assessment.Rationale;
        return assessment with
        {
            Action = "tarkista",
            Rationale = rationale.Length > AssessmentValidator.MaxRationaleChars ? rationale[..AssessmentValidator.MaxRationaleChars] : rationale,
            NeedsReview = true,
            ReviewReasons = assessment.ReviewReasons.Contains(reason) ? assessment.ReviewReasons : [.. assessment.ReviewReasons, reason],
        };
    }
}
