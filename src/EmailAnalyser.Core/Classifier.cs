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
            duration += response.Elapsed;
            promptTokens = response.PromptTokens;
            completionTokens = (completionTokens ?? 0) + (response.CompletionTokens ?? 0);

            var validation = response.FinishReason == "length"
                ? new ValidationResult(null, ["vastaus katkesi tokenirajaan"])
                : AssessmentValidator.Validate(response.Content, prepared.Truncated);
            if (validation.IsValid)
            {
                var assessment = validation.Assessment!;
                if (InjectionHeuristics.Detect(message.Subject + "\n" + message.Body))
                {
                    // A message that addresses the classifier does not get the classifier's
                    // judgement: the action is forced to review regardless of the model's answer.
                    var note = $"Ohjelma: viestissä on tekoälylle osoitettua tekstiä, joten toimenpide on tarkista (mallin ehdotus: {assessment.Action}). ";
                    var rationale = note + assessment.Rationale;
                    assessment = assessment with
                    {
                        Action = "tarkista",
                        Rationale = rationale.Length > AssessmentValidator.MaxRationaleChars ? rationale[..AssessmentValidator.MaxRationaleChars] : rationale,
                        NeedsReview = true,
                        ReviewReasons = [.. assessment.ReviewReasons, InjectionHeuristics.ReviewReason],
                    };
                }

                return new ClassificationOutcome(assessment, [], prepared, attempt, promptTokens, completionTokens, duration);
            }

            errors = validation.Errors;
        }

        return new ClassificationOutcome(null, errors, prepared, MaxAttempts, promptTokens, completionTokens, duration);
    }
}
