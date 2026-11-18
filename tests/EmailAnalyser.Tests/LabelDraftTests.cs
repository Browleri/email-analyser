using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using Xunit;

namespace EmailAnalyser.Tests;

public class LabelDraftTests
{
    private static readonly Assessment Ai = new("y", "p", "tyo", "vastattava", "tanaan", new DateOnly(2026, 11, 19), false, []);

    [Fact]
    public void Prefilled_ai_values_are_not_a_change()
    {
        var draft = new LabelDraft();
        draft.Load(null, Ai);
        Assert.Equal(("vastattava", "tanaan", "tyo", new DateOnly(2026, 11, 19)), (draft.Action, draft.Urgency, draft.Topic, draft.Deadline));
        Assert.False(draft.IsDirty);
        Assert.True(draft.CanSave);
    }

    [Fact]
    public void Label_wins_over_ai_and_a_cleared_deadline_stays_cleared()
    {
        var draft = new LabelDraft();
        draft.Load(new UserLabel("tiedoksi", "ei_maaraaikaa", "uutiskirje", null), Ai);
        Assert.Equal("tiedoksi", draft.Action);
        Assert.Null(draft.Deadline);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Any_change_is_dirty_until_saved_or_reverted()
    {
        var draft = new LabelDraft();
        draft.Load(null, Ai);

        draft.Deadline = null;
        Assert.True(draft.IsDirty);
        draft.Deadline = new DateOnly(2026, 11, 19);
        Assert.False(draft.IsDirty);

        draft.Action = "tehtava";
        Assert.True(draft.IsDirty);
        Assert.Equal(new UserLabel("tehtava", "tanaan", "tyo", new DateOnly(2026, 11, 19)), draft.ToLabel());
        draft.MarkClean();
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Blind_mode_starts_empty_and_an_incomplete_label_cannot_be_saved()
    {
        var draft = new LabelDraft();
        draft.Load(null, visibleAi: null);
        Assert.False(draft.IsDirty);
        Assert.False(draft.CanSave);

        draft.Action = "vastattava";
        Assert.True(draft.IsDirty);
        Assert.False(draft.CanSave);
        Assert.Throws<InvalidOperationException>(() => draft.ToLabel());
    }
}
