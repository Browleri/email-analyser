using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

public enum ClassificationSource
{
    None,
    Label,
    Ai,
}

/// <summary>What the list shows for a message: the user's label, otherwise the AI's assessment.</summary>
public sealed record EffectiveClassification(ClassificationSource Source, string? Action, string? Urgency, string? Topic, DateOnly? Deadline)
{
    public static readonly EffectiveClassification None = new(ClassificationSource.None, null, null, null, null);
}

/// <summary>
/// Which messages the view shows, highlights and puts first. The user's label wins over
/// the model, as it does for Outlook categories (<see cref="AiCategories.Desired"/>), so
/// the application and Outlook never disagree about a corrected message.
/// </summary>
public static class Triage
{
    public const string Attention = "huomio";
    public const string All = "kaikki";
    public const string Unlabeled = "merkitsematon";
    public const string MissedByAi = "huomiotta";
    public const string Problems = "ongelmat";
    public const string Done = "hoidetut";

    /// <summary>Actions that put a message in the attention view.</summary>
    public static readonly IReadOnlySet<string> AttentionOrReview = new HashSet<string> { "vastattava", "tehtava", "tarkista" };

    private static readonly string[] ActionPriority = ["vastattava", "tehtava", "tarkista", "tiedoksi"];
    private static readonly string[] UrgencyPriority = ["tanaan", "talla_viikolla", "myohemmin", "ei_maaraaikaa"];

    /// <summary>In blind mode the AI's answer is hidden until the user has labelled the message.</summary>
    public static bool AiVisible(ReviewItem item, bool blind) => item.Assessment is not null && (!blind || item.Label is not null);

    public static EffectiveClassification Effective(ReviewItem item, bool blind)
    {
        if (item.Label is { } label)
        {
            return new(ClassificationSource.Label, label.Action, label.Urgency, label.Topic, label.Deadline);
        }

        return AiVisible(item, blind) && item.Assessment is { } ai
            ? new(ClassificationSource.Ai, ai.Action, ai.Urgency, ai.Topic, ai.Deadline)
            : EffectiveClassification.None;
    }

    public static bool SenderSuspected(ReviewItem item) => item.Assessment?.ReviewReasons.Contains(SenderAuthentication.ReviewReason) == true;

    public static bool InjectionSuspected(ReviewItem item) => item.Assessment?.ReviewReasons.Contains(InjectionHeuristics.ReviewReason) == true;

    /// <summary>The program's own checks, not the model's opinion: they count even in blind mode.</summary>
    public static bool HasWarning(ReviewItem item) => SenderSuspected(item) || InjectionSuspected(item);

    public static bool IsDone(ReviewItem item) => item.DoneAt is not null;

    /// <summary>
    /// Needs the user: an action that calls for a response, a task or a check, or a warning
    /// the user has not yet judged. A label settles a warning, as it does for the Outlook
    /// category. A message marked done never needs attention.
    /// </summary>
    public static bool NeedsAttention(ReviewItem item, bool blind)
    {
        if (IsDone(item))
        {
            return false;
        }

        var action = Effective(item, blind).Action;
        return action is not null && AttentionOrReview.Contains(action) || item.Label is null && HasWarning(item);
    }

    /// <summary>
    /// The user says it needs attention but the AI did not flag it. Compares the raw AI
    /// answer, because this is a measurement view.
    /// </summary>
    public static bool Missed(ReviewItem item) =>
        item.Label is not null && item.Assessment is not null &&
        Labels.AttentionActions.Contains(item.Label.Action) &&
        !Labels.AttentionActions.Contains(item.Assessment.Action) && item.Assessment.Action != "tarkista";

    /// <summary>
    /// Done messages leave the attention and action views, but stay in the measurement
    /// views (unlabelled, missed) and the problem view, which are about the data, not the work.
    /// </summary>
    public static bool Matches(ReviewItem item, string filter, bool blind) => filter switch
    {
        Attention => NeedsAttention(item, blind),
        "vastattava" or "tehtava" or "tarkista" or "tiedoksi" => !IsDone(item) && Effective(item, blind).Action == filter,
        Unlabeled => item.Label is null && item.Assessment is not null,
        MissedByAi => Missed(item),
        Problems => item.Status is not "assessed",
        Done => IsDone(item),
        _ => true,
    };

    /// <summary>Most important first when sorted ascending: action, then urgency; nothing to show last.</summary>
    public static int Priority(ReviewItem item, bool blind)
    {
        var effective = Effective(item, blind);
        return effective.Action is null ? 99 : Rank(ActionPriority, effective.Action) * 10 + Rank(UrgencyPriority, effective.Urgency);
    }

    /// <summary>Nearest deadline first when sorted ascending; no deadline last.</summary>
    public static DateOnly DeadlineSort(ReviewItem item, bool blind) => Effective(item, blind).Deadline ?? DateOnly.MaxValue;

    private static int Rank(string[] order, string? key)
    {
        var index = key is null ? -1 : Array.IndexOf(order, key);
        return index < 0 ? 9 : index;
    }
}
