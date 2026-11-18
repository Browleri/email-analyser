using System.Security.Cryptography;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using Xunit;

namespace EmailAnalyser.Tests;

public class TriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Filters =
        [Triage.Attention, "vastattava", "tehtava", "tarkista", "tiedoksi", Triage.Unlabeled, Triage.MissedByAi, Triage.Problems, Triage.Done, Triage.All];

    private static Assessment Ai(string action, string urgency = "tanaan", DateOnly? deadline = null, params string[] reasons) =>
        new("yhteenveto", "perustelu", "tyo", action, urgency, deadline, reasons.Length > 0, reasons);

    private static UserLabel Label(string action, string urgency = "ei_maaraaikaa", DateOnly? deadline = null) =>
        new(action, urgency, "tyo", deadline, Split: LabelSplit.Holdout, LabeledAt: Now);

    private static ReviewItem Item(Assessment? ai, UserLabel? label = null, string status = "assessed", bool done = false) =>
        new(1, new MessageReference("store", "e1"), "tyo@example.fi", "Lähettäjä", "a@example.fi", "Otsikko", Now, status, null,
            ai is null ? null : 10, ai, false, "m", "p", label, DoneAt: done ? Now : null);

    private static string[] MatchingFilters(ReviewItem item, bool blind = false) =>
        Filters.Where(f => Triage.Matches(item, f, blind)).ToArray();

    [Fact]
    public void Label_wins_over_ai_in_filters_tag_and_order()
    {
        // AI flagged a newsletter for a reply; the user says it is for information only.
        var corrected = Item(Ai("vastattava"), Label("tiedoksi"));
        Assert.Equal(new EffectiveClassification(ClassificationSource.Label, "tiedoksi", "ei_maaraaikaa", "tyo", null), Triage.Effective(corrected, blind: false));
        Assert.Equal(["tiedoksi", Triage.All], MatchingFilters(corrected));

        // The other way round: the AI missed a message that needs a reply.
        var missed = Item(Ai("tiedoksi"), Label("vastattava", "tanaan", new DateOnly(2026, 11, 19)));
        Assert.Equal([Triage.Attention, "vastattava", Triage.MissedByAi, Triage.All], MatchingFilters(missed));
        Assert.True(Triage.Priority(missed, false) < Triage.Priority(corrected, false));
        Assert.Equal(new DateOnly(2026, 11, 19), Triage.DeadlineSort(missed, false));
    }

    [Fact]
    public void List_agrees_with_outlook_category()
    {
        foreach (var (ai, label) in new (Assessment?, UserLabel?)[]
                 {
                     (Ai("vastattava"), null), (Ai("vastattava"), Label("tiedoksi")), (Ai("tiedoksi"), Label("tehtava")),
                     (Ai("tarkista", reasons: SenderAuthentication.ReviewReason), null),
                 })
        {
            var effective = Triage.Effective(Item(ai, label), blind: false);
            Assert.Equal(AiCategories.Desired(ai, label), AiCategories.ByAction.GetValueOrDefault(effective.Action!));
        }
    }

    [Fact]
    public void Unassessed_message_has_no_classification()
    {
        var queued = Item(null, status: "queued");
        Assert.Equal(EffectiveClassification.None, Triage.Effective(queued, false));
        Assert.Equal(99, Triage.Priority(queued, false));
        Assert.Equal(DateOnly.MaxValue, Triage.DeadlineSort(queued, false));
        Assert.Equal([Triage.Problems, Triage.All], MatchingFilters(queued));
    }

    [Fact]
    public void Blind_mode_hides_ai_until_labelled_but_not_the_label()
    {
        var unlabeled = Item(Ai("vastattava"));
        Assert.Equal(EffectiveClassification.None, Triage.Effective(unlabeled, blind: true));
        Assert.Equal([Triage.Unlabeled, Triage.All], MatchingFilters(unlabeled, blind: true));

        var labeled = Item(Ai("tiedoksi"), Label("tehtava"));
        Assert.Equal(ClassificationSource.Label, Triage.Effective(labeled, blind: true).Source);
        Assert.True(Triage.AiVisible(labeled, blind: true));
        Assert.Contains(Triage.Attention, MatchingFilters(labeled, blind: true));
    }

    [Fact]
    public void Warnings_need_attention_until_the_user_has_judged_the_message()
    {
        var phishing = Item(Ai("tarkista", reasons: SenderAuthentication.ReviewReason));
        var steering = Item(Ai("tarkista", reasons: InjectionHeuristics.ReviewReason));
        Assert.True(Triage.HasWarning(phishing) && Triage.SenderSuspected(phishing));
        Assert.True(Triage.HasWarning(steering) && Triage.InjectionSuspected(steering));

        // Even in blind mode, where the AI's action is hidden.
        Assert.True(Triage.NeedsAttention(phishing, blind: true));
        Assert.True(Triage.NeedsAttention(steering, blind: true));

        // A label settles it, as it does for the Outlook category; the warning itself stays visible.
        var judged = phishing with { Label = Label("tiedoksi") };
        Assert.False(Triage.NeedsAttention(judged, blind: false));
        Assert.True(Triage.HasWarning(judged));
    }

    [Fact]
    public void Done_leaves_work_views_but_not_measurement_or_problem_views()
    {
        var reply = Item(Ai("vastattava"), done: true);
        Assert.Equal([Triage.Unlabeled, Triage.Done, Triage.All], MatchingFilters(reply));

        var missedAndDone = Item(Ai("tiedoksi"), Label("vastattava"), done: true);
        Assert.Equal([Triage.MissedByAi, Triage.Done, Triage.All], MatchingFilters(missedAndDone));

        var phishingDone = Item(Ai("tarkista", reasons: SenderAuthentication.ReviewReason), done: true);
        Assert.False(Triage.NeedsAttention(phishingDone, blind: false));

        var requeuedDone = Item(Ai("tehtava"), status: "queued", done: true);
        Assert.Contains(Triage.Problems, MatchingFilters(requeuedDone));
        Assert.DoesNotContain("tehtava", MatchingFilters(requeuedDone));
    }
}

public class DoneStateTests
{
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task Done_is_local_reversible_and_keeps_label_and_assessment()
    {
        using var env = new BatchRunnerTests.Env();
        await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        var item = env.Store.GetReviewItems().First(i => i.Reference.EntryId == "e1");
        env.Store.SaveLabel(item, new UserLabel("vastattava", "tanaan", "tyo", null), false, DateTimeOffset.UtcNow);
        var now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

        env.Store.SetDone(item.MessageId, true, now);
        env.Store.SetDone(item.MessageId, true, now.AddHours(1)); // repeating keeps the first time
        var done = env.Store.GetReviewItems().First(i => i.MessageId == item.MessageId);
        Assert.Equal(now, done.DoneAt);
        Assert.NotNull(done.Label);
        Assert.NotNull(done.Assessment);
        Assert.Null(env.Store.GetReviewItems().First(i => i.Reference.EntryId == "e2").DoneAt);

        env.Store.SetDone(item.MessageId, false, now);
        Assert.Null(env.Store.GetReviewItems().First(i => i.MessageId == item.MessageId).DoneAt);
    }

    [Fact]
    public void Retention_removes_done_marks_of_old_messages_even_when_the_message_is_kept()
    {
        using var dir = new TempDir();
        using var store = AnalyserStore.Open(dir.File("a.db"), Key);
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "old-labelled"), "tyo@example.fi", @"\\tyo\Inbox", null, old), old);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "old"), "tyo@example.fi", @"\\tyo\Inbox", null, old), old);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "new"), "tyo@example.fi", @"\\tyo\Inbox", null, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        var items = store.GetReviewItems();
        store.SaveLabel(items.Single(i => i.Reference.EntryId == "old-labelled"), new UserLabel("tiedoksi", "ei_maaraaikaa", "tyo", null), false, DateTimeOffset.UtcNow);
        foreach (var i in items)
        {
            store.SetDone(i.MessageId, true, DateTimeOffset.UtcNow);
        }

        store.ApplyRetention(DateTimeOffset.UtcNow.AddDays(-90));

        var after = store.GetReviewItems().ToDictionary(i => i.Reference.EntryId);
        Assert.Equal(["new", "old-labelled"], after.Keys.Order());
        Assert.Null(after["old-labelled"].DoneAt);
        Assert.NotNull(after["new"].DoneAt);
    }
}
