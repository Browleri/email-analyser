using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

/// <summary>
/// Compares each labelled message's latest assessment with the user's label.
/// Contains only counts and message ids, never content.
/// </summary>
public sealed record LabelEvaluationResult(
    string Split,
    int Labeled,
    int Assessed,
    int AttentionExpected,
    int AttentionFound,
    int AttentionFoundAsReview,
    IReadOnlyList<long> MissedMessageIds,
    int NoAttentionExpected,
    int FalseAlarms,
    int ActionCorrect,
    int UrgencyCorrect,
    int TopicCorrect,
    int DeadlineCorrect,
    int AgreeBlind,
    int LabeledBlind,
    int AgreeSeen,
    int LabeledSeen,
    IReadOnlyDictionary<string, int> PromptVersions,
    IReadOnlyDictionary<string, int> LabelTopics,
    IReadOnlyDictionary<string, int> TopicMismatches)
{
    public double Recall => AttentionExpected == 0 ? double.NaN : (double)AttentionFound / AttentionExpected;
}

public static class LabelEvaluation
{
    /// <param name="split">Tuning or holdout split; null for all labelled messages.</param>
    public static LabelEvaluationResult Compute(IEnumerable<ReviewItem> items, string? split)
    {
        var labeled = items.Where(i => i.Label is not null && (split is null || i.Label.Split == split)).ToList();
        var assessed = labeled.Where(i => i.Assessment is not null).ToList();
        var missed = new List<long>();
        int expected = 0, found = 0, foundAsReview = 0, noAttention = 0, falseAlarms = 0;
        int action = 0, urgency = 0, topic = 0, deadline = 0;

        foreach (var item in assessed)
        {
            var label = item.Label!;
            var ai = item.Assessment!;
            var needsAttention = Labels.AttentionActions.Contains(label.Action);
            var aiAttention = Labels.AttentionActions.Contains(ai.Action);
            if (needsAttention)
            {
                expected++;
                if (aiAttention)
                {
                    found++;
                }
                else if (ai.Action == "tarkista")
                {
                    // Not missed: the message is in the review list.
                    found++;
                    foundAsReview++;
                }
                else
                {
                    missed.Add(item.MessageId);
                }
            }
            else
            {
                noAttention++;
                falseAlarms += aiAttention ? 1 : 0;
            }

            action += ai.Action == label.Action ? 1 : 0;
            urgency += ai.Urgency == label.Urgency ? 1 : 0;
            topic += ai.Topic == label.Topic ? 1 : 0;
            deadline += ai.Deadline == label.Deadline ? 1 : 0;
        }

        return new LabelEvaluationResult(
            split ?? "kaikki",
            labeled.Count,
            assessed.Count,
            expected,
            found,
            foundAsReview,
            missed,
            noAttention,
            falseAlarms,
            action,
            urgency,
            topic,
            deadline,
            assessed.Count(i => i.Label!.Blind && i.Label.AgreesWithAi),
            assessed.Count(i => i.Label!.Blind),
            assessed.Count(i => !i.Label!.Blind && i.Label.AgreesWithAi),
            assessed.Count(i => !i.Label!.Blind),
            assessed.GroupBy(i => i.PromptVersion ?? "?").ToDictionary(g => g.Key, g => g.Count()),
            labeled.GroupBy(i => i.Label!.Topic).ToDictionary(g => g.Key, g => g.Count()),
            assessed.Where(i => i.Assessment!.Topic != i.Label!.Topic)
                .GroupBy(i => $"{i.Label!.Topic} → {i.Assessment!.Topic}")
                .ToDictionary(g => g.Key, g => g.Count()));
    }
}
