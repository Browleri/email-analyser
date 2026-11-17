using EmailAnalyser.Core.Storage;

namespace EmailAnalyser.Core;

/// <summary>
/// The only Outlook categories this application writes. Chosen by program code from
/// a fixed table; model text never reaches Outlook.
/// </summary>
public static class AiCategories
{
    public const string Review = "AI: Tarkista";

    public static readonly IReadOnlyDictionary<string, string> ByAction = new Dictionary<string, string>
    {
        ["vastattava"] = "AI: Vastattava",
        ["tehtava"] = "AI: Tehtävä",
        ["tiedoksi"] = "AI: Tiedoksi",
        ["tarkista"] = Review,
    };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(ByAction.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The category a message should carry. The user's label wins over the model.
    /// A message flagged for steering or a failed sender check gets only the review
    /// category, whatever the model suggested.
    /// </summary>
    public static string? Desired(Assessment? assessment, UserLabel? label)
    {
        if (label is not null)
        {
            return ByAction.GetValueOrDefault(label.Action);
        }

        if (assessment is null)
        {
            return null;
        }

        if (assessment.ReviewReasons.Contains(InjectionHeuristics.ReviewReason) ||
            assessment.ReviewReasons.Contains(SenderAuthentication.ReviewReason))
        {
            return Review;
        }

        return ByAction.GetValueOrDefault(assessment.Action);
    }

    /// <summary>
    /// Adds <paramref name="add"/> and removes <paramref name="remove"/> from a category
    /// list, keeping every other category and its order. Only names from <see cref="All"/>
    /// are ever added or removed.
    /// </summary>
    public static (string? Categories, bool Changed, bool Added) Apply(string? current, string separator, string? add, string? remove)
    {
        if (add is not null && !All.Contains(add) || remove is not null && !All.Contains(remove))
        {
            throw new ArgumentException("Vain sovelluksen omia AI-luokkia saa muuttaa.");
        }

        var list = Split(current, separator);
        var changed = false;
        if (remove is not null && !string.Equals(remove, add, StringComparison.OrdinalIgnoreCase))
        {
            changed |= list.RemoveAll(c => string.Equals(c, remove, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        var added = false;
        if (add is not null && !list.Contains(add, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(add);
            changed = added = true;
        }

        return (list.Count == 0 ? null : string.Join(separator + " ", list), changed, added);
    }

    public static List<string> Split(string? categories, string separator) =>
        (categories ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public enum CategoryWriteStatus
{
    Written,
    Unchanged,
    Gone,
    Mismatch,
    NotSupported,
    Failed,
}

/// <summary>What to change on one message, with the identity it must still have.</summary>
public sealed record CategoryChange(string? ExpectedMessageId, string ExpectedContentHash, string? Add, string? Remove);

public sealed record CategoryWriteResult(CategoryWriteStatus Status, bool Added, string? Before, string? After, string? Error)
{
    public static CategoryWriteResult Of(CategoryWriteStatus status, string? error = null) => new(status, false, null, null, error);
}

/// <summary>Writes the application's own categories to a message (Outlook adapter).</summary>
public interface ICategoryTarget
{
    /// <summary>
    /// Re-reads the message, checks its Message-ID and content hash, and changes only
    /// the given AI categories. Never marks the message read.
    /// </summary>
    Task<CategoryWriteResult> ChangeAsync(MessageReference reference, CategoryChange change, CancellationToken cancellationToken);
}

public enum CategoryMode
{
    Off,
    /// <summary>Only the message whose subject exactly matches the test subject (development).</summary>
    Test,
    On,
}

public sealed record CategorySyncResult(int Written, int Unchanged, int Skipped, int Blocked, int Failed, string? Problem = null)
{
    public static readonly CategorySyncResult None = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Brings Outlook categories in line with the latest assessments and labels. Each
/// message's state is kept so the same change is not repeated, a category the user
/// removed is not added back, and every change can be rolled back.
/// </summary>
public sealed class CategorySync(AnalyserStore store, ICategoryTarget target, TimeProvider time)
{
    public const int MaxAttempts = 3;

    public async Task<CategorySyncResult> SyncAsync(CategoryMode mode, string? testSubject, CancellationToken cancellationToken)
    {
        if (mode == CategoryMode.Off)
        {
            return CategorySyncResult.None;
        }

        int written = 0, unchanged = 0, skipped = 0, blocked = 0, failed = 0;
        foreach (var c in store.GetCategoryCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mode == CategoryMode.Test && (string.IsNullOrEmpty(testSubject) || !string.Equals(c.Subject, testSubject, StringComparison.Ordinal)))
            {
                continue;
            }

            var desired = AiCategories.Desired(c.Assessment, c.Label);
            if (desired is null || c.ContentHash is null || !NeedsWrite(c, desired))
            {
                skipped++;
                continue;
            }

            // Remove only a category this application added itself.
            var remove = c.State?.Owned is { } owned && !string.Equals(owned, desired, StringComparison.OrdinalIgnoreCase) ? owned : null;
            var result = await target.ChangeAsync(c.Reference, new CategoryChange(c.InternetMessageId, c.ContentHash, desired, remove), cancellationToken);
            // Ownership: a category counts as ours only if this application added it. One
            // that was already there (e.g. added by the user) is left out of rollback.
            var owner = result.Status switch
            {
                CategoryWriteStatus.Written or CategoryWriteStatus.Unchanged =>
                    result.Added || string.Equals(c.State?.Owned, desired, StringComparison.OrdinalIgnoreCase) ? desired : null,
                _ => c.State?.Owned,
            };
            store.SaveCategoryResult(c.MessageId, desired, owner, result, "kirjoitus", time.GetUtcNow());
            switch (result.Status)
            {
                case CategoryWriteStatus.Written: written++; break;
                case CategoryWriteStatus.Unchanged: unchanged++; break;
                case CategoryWriteStatus.Failed: failed++; break;
                default: blocked++; break;
            }
        }

        return new CategorySyncResult(written, unchanged, skipped, blocked, failed);
    }

    /// <summary>
    /// Removes every category this application added. Categories the user added or
    /// changed later are kept; a message whose identity no longer matches is left alone.
    /// </summary>
    public async Task<CategorySyncResult> RollbackAsync(CancellationToken cancellationToken)
    {
        int written = 0, unchanged = 0, blocked = 0, failed = 0;
        foreach (var c in store.GetOwnedCategories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.ContentHash is null)
            {
                blocked++;
                continue;
            }

            var result = await target.ChangeAsync(c.Reference, new CategoryChange(c.InternetMessageId, c.ContentHash, null, c.State!.Owned), cancellationToken);
            var removed = result.Status is CategoryWriteStatus.Written or CategoryWriteStatus.Unchanged or CategoryWriteStatus.Gone;
            store.SaveCategoryResult(c.MessageId, null, removed ? null : c.State.Owned, result, "palautus", time.GetUtcNow());
            switch (result.Status)
            {
                case CategoryWriteStatus.Written: written++; break;
                case CategoryWriteStatus.Unchanged or CategoryWriteStatus.Gone: unchanged++; break;
                case CategoryWriteStatus.Failed: failed++; break;
                default: blocked++; break;
            }
        }

        return new CategorySyncResult(written, unchanged, 0, blocked, failed);
    }

    private static bool NeedsWrite(CategoryCandidate c, string desired)
    {
        if (c.State is not { } s)
        {
            return true;
        }

        if (!string.Equals(s.Desired, desired, StringComparison.OrdinalIgnoreCase))
        {
            return true; // new assessment or label
        }

        // Same target as before: done, permanently blocked, or out of retries. A category
        // the user removed after it was written is not added back.
        return s.Status == nameof(CategoryWriteStatus.Failed) && s.Attempts < MaxAttempts;
    }
}
