using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using Xunit;

namespace EmailAnalyser.Tests;

public class LabelBackupTests
{
    private const string Passphrase = "koivu tuohi lehto 42";
    private const int FastIterations = 100_000;
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static ExportedLabel Label(string entryId, string? messageId = null, string action = "vastattava", DateTimeOffset? labeledAt = null) =>
        new("store", entryId, messageId, "tyo@example.fi", @"\\tyo\Inbox", Now.AddDays(-1),
            action, "tanaan", "tyo", new DateOnly(2026, 11, 19), false, LabelSplit.Holdout, true, labeledAt ?? Now.AddHours(-1));

    [Fact]
    public void Round_trip_keeps_every_field()
    {
        var labels = new[] { Label("e1", "<a@example>"), Label("e2") with { Deadline = null, Split = LabelSplit.Tuning, Blind = false } };
        var file = LabelBackup.Encrypt(labels, Passphrase, Now, FastIterations);
        Assert.Equal(labels, LabelBackup.Decrypt(file, Passphrase));
    }

    [Fact]
    public void File_contains_no_plain_identifiers()
    {
        var file = Encoding.UTF8.GetString(LabelBackup.Encrypt([Label("e1", "<a@example>")], Passphrase, Now, FastIterations));
        Assert.DoesNotContain("tyo@example.fi", file);
        Assert.DoesNotContain("<a@example>", file);
        Assert.DoesNotContain("vastattava", file);
    }

    [Fact]
    public void Wrong_passphrase_and_changed_file_are_rejected()
    {
        var file = LabelBackup.Encrypt([Label("e1")], Passphrase, Now, FastIterations);
        Assert.Throws<InvalidDataException>(() => LabelBackup.Decrypt(file, "väärä tunnuslause"));

        // The header is authenticated: lowering the iteration count breaks the file.
        var json = JsonNode.Parse(file)!;
        json["header"]!["iterations"] = FastIterations + 1;
        Assert.Throws<InvalidDataException>(() => LabelBackup.Decrypt(Encoding.UTF8.GetBytes(json.ToJsonString()), Passphrase));

        Assert.Throws<InvalidDataException>(() => LabelBackup.Decrypt("{}"u8.ToArray(), Passphrase));
        Assert.Throws<InvalidDataException>(() => LabelBackup.Decrypt("ei json"u8.ToArray(), Passphrase));
    }

    [Fact]
    public void Short_passphrase_is_refused() =>
        Assert.Throws<ArgumentException>(() => LabelBackup.Encrypt([Label("e1")], "lyhyt", Now, FastIterations));

    [Fact]
    public void Import_into_empty_database_queues_messages_and_keeps_split()
    {
        using var dir = new TempDir();
        using var store = AnalyserStore.Open(dir.File("a.db"), Key);
        // Split is kept from the export even when it differs from the hash of the reference.
        var label = Label("e1", "<a@example>") with { Split = LabelSplit.For(new MessageReference("store", "e1")) == LabelSplit.Tuning ? LabelSplit.Holdout : LabelSplit.Tuning };

        var result = store.ImportLabels([label], Now);

        Assert.Equal(new LabelImportResult(1, 1, 0, 0), result);
        var queued = Assert.Single(store.GetQueued(10));
        Assert.Equal(new MessageReference("store", "e1"), queued.Reference);
        var item = Assert.Single(store.GetReviewItems());
        Assert.Equal("queued", item.Status);
        Assert.Equal(label.Split, item.Label!.Split);
        Assert.Equal(label.Deadline, item.Label.Deadline);
        Assert.True(item.Label.Blind);
        Assert.Equal(new[] { label }, store.ExportLabels());
    }

    [Fact]
    public void Import_matches_by_reference_then_by_unique_message_id()
    {
        using var dir = new TempDir();
        using var store = AnalyserStore.Open(dir.File("a.db"), Key);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "e1"), "tyo@example.fi", @"\\tyo\Inbox", "<a@example>", Now), Now);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "moved"), "tyo@example.fi", @"\\tyo\Inbox", "<b@example>", Now), Now);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "copy1"), "tyo@example.fi", @"\\tyo\Inbox", "<c@example>", Now), Now);
        store.Enqueue(new DiscoveredMessage(new MessageReference("store", "copy2"), "tyo@example.fi", @"\\tyo\Inbox", "<c@example>", Now), Now);

        var result = store.ImportLabels([Label("e1"), Label("old-entry", "<b@example>"), Label("another", "<c@example>")], Now);

        // e1 by reference, <b> by Message-ID; <c> is ambiguous, so a new reference is queued instead.
        Assert.Equal(new LabelImportResult(3, 1, 0, 0), result);
        var labelled = store.GetReviewItems().Where(i => i.Label is not null).Select(i => i.Reference.EntryId).Order().ToList();
        Assert.Equal(["another", "e1", "moved"], labelled);
    }

    [Fact]
    public async Task Newer_label_in_database_is_kept_and_older_one_is_replaced()
    {
        using var env = new BatchRunnerTests.Env();
        await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        var items = env.Store.GetReviewItems();
        env.Store.SaveLabel(items.First(i => i.Reference.EntryId == "e1"), new UserLabel("tiedoksi", "ei_maaraaikaa", "tyo", null), false, Now);
        env.Store.SaveLabel(items.First(i => i.Reference.EntryId == "e2"), new UserLabel("tiedoksi", "ei_maaraaikaa", "tyo", null), false, Now.AddDays(-2));

        var result = env.Store.ImportLabels([Label("e1", action: "tehtava"), Label("e2", action: "tehtava")], Now);

        Assert.Equal(new LabelImportResult(1, 0, 1, 0), result);
        var after = env.Store.GetReviewItems();
        Assert.Equal("tiedoksi", after.First(i => i.Reference.EntryId == "e1").Label!.Action);
        Assert.Equal("tehtava", after.First(i => i.Reference.EntryId == "e2").Label!.Action);
        Assert.All(after, i => Assert.Equal("assessed", i.Status));
    }

    [Fact]
    public void Invalid_values_are_rejected_on_import()
    {
        using var dir = new TempDir();
        using var store = AnalyserStore.Open(dir.File("a.db"), Key);
        var result = store.ImportLabels([Label("e1", action: "poista"), Label("e2") with { Split = "muu" }, Label("") ], Now);
        Assert.Equal(new LabelImportResult(0, 0, 0, 3), result);
        Assert.Empty(store.GetReviewItems());
    }
}
