using System.Security.Cryptography;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using Xunit;

namespace EmailAnalyser.Tests;

public class LabelNoteTests
{
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Note_is_cleaned_to_one_line_and_capped()
    {
        Assert.Null(UserLabel.CleanNote("   "));
        Assert.Equal("kaksi riviä", UserLabel.CleanNote(" kaksi" + (char)0x200B + "\nriviä "));
        Assert.Equal(UserLabel.MaxNoteChars, UserLabel.CleanNote(new string('x', 500))!.Length);
    }

    [Fact]
    public void Draft_note_change_is_dirty_and_blank_is_no_note()
    {
        var draft = new LabelDraft();
        draft.Load(new UserLabel("tiedoksi", "ei_maaraaikaa", "markkinat", null, Note: "viikkokatsaus"), null);
        Assert.Equal("viikkokatsaus", draft.Note);
        Assert.False(draft.IsDirty);

        draft.Note = "viikkokatsaus, ei toimia";
        Assert.True(draft.IsDirty);
        Assert.Equal("viikkokatsaus, ei toimia", draft.ToLabel().Note);

        draft.Load(new UserLabel("tiedoksi", "ei_maaraaikaa", "markkinat", null), null);
        draft.Note = "  ";
        Assert.False(draft.IsDirty);
        Assert.Null(draft.ToLabel().Note);
    }

    [Fact]
    public void Note_is_stored_exported_imported_and_removed_by_retention()
    {
        using var dir = new TempDir();
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<ExportedLabel> exported;
        using (var store = AnalyserStore.Open(dir.File("a.db"), Key))
        {
            store.Enqueue(new DiscoveredMessage(new MessageReference("store", "old"), "tyo@example.fi", @"\\tyo\Inbox", null, old), old);
            store.Enqueue(new DiscoveredMessage(new MessageReference("store", "new"), "tyo@example.fi", @"\\tyo\Inbox", null, now), now);
            foreach (var item in store.GetReviewItems())
            {
                store.SaveLabel(item, new UserLabel("tiedoksi", "ei_maaraaikaa", "kohdeyhtiot", null, Note: "sijoittajakirje"), false, now);
            }

            Assert.All(store.GetReviewItems(), i => Assert.Equal("sijoittajakirje", i.Label!.Note));
            exported = store.ExportLabels();
            Assert.All(exported, l => Assert.Equal("sijoittajakirje", l.Note));

            store.ApplyRetention(now.AddDays(-90));
            var after = store.GetReviewItems().ToDictionary(i => i.Reference.EntryId);
            Assert.Null(after["old"].Label!.Note);
            Assert.Equal("kohdeyhtiot", after["old"].Label!.Topic);
            Assert.Equal("sijoittajakirje", after["new"].Label!.Note);
        }

        using var other = AnalyserStore.Open(dir.File("b.db"), Key);
        other.ImportLabels(exported, now);
        Assert.All(other.GetReviewItems(), i => Assert.Equal("sijoittajakirje", i.Label!.Note));
    }
}
