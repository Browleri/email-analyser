using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using static EmailAnalyser.Tests.BatchRunnerTests;

namespace EmailAnalyser.Tests;

public class SenderAuthenticationTests
{
    private const string Fail = """
        Received: from mx.example by mail.protection.outlook.com
        Authentication-Results: spf=fail (sender IP is 192.0.2.1)
         smtp.mailfrom=pankki.example; dkim=none (message not signed)
         header.d=none;dmarc=fail action=quarantine header.from=pankki.example;compauth=fail reason=000
        From: Pankki <info@pankki.example>
        """;

    [Fact]
    public void Dmarc_fail_in_the_receiving_servers_header_is_a_signal()
    {
        var check = SenderAuthentication.Evaluate("Pankki", "info@pankki.example", Fail);
        Assert.True(check.Suspicious);
        Assert.Equal(["dmarc_fail"], check.Signals);
        Assert.Equal("fail", check.Spf);
        Assert.Equal("none", check.Dkim);
    }

    [Fact]
    public void Only_the_topmost_header_counts()
    {
        // A sender can write its own Authentication-Results lower down; the receiving server's comes first.
        var forgedPass = "Authentication-Results: mx.google.com; spf=fail smtp.mailfrom=x.example; dmarc=fail header.from=x.example\n" +
                         "Authentication-Results: spf=pass; dkim=pass; dmarc=pass\n";
        Assert.True(SenderAuthentication.Evaluate("", "a@x.example", forgedPass).Suspicious);

        var forgedFail = "Authentication-Results: mx.google.com; spf=pass smtp.mailfrom=x.example; dkim=pass header.i=@x.example; dmarc=pass\n" +
                         "Authentication-Results: spf=fail; dmarc=fail\n";
        Assert.False(SenderAuthentication.Evaluate("", "a@x.example", forgedFail).Suspicious);
    }

    [Fact]
    public void Spf_fail_counts_only_without_dkim_or_dmarc_pass()
    {
        Assert.Equal(["spf_fail"], SenderAuthentication.Evaluate("", "a@x.example", "Authentication-Results: spf=fail; dkim=none").Signals);
        Assert.False(SenderAuthentication.Evaluate("", "a@x.example", "Authentication-Results: spf=fail; dkim=pass; dmarc=pass").Suspicious);
        Assert.Equal(["compauth_fail"], SenderAuthentication.Evaluate("", "a@x.example", "Authentication-Results: spf=pass; compauth=fail reason=601").Signals);
    }

    [Fact]
    public void Missing_headers_give_no_signal()
    {
        Assert.False(SenderAuthentication.Evaluate("Kollega", "kollega@firma.example", "").Suspicious);
        Assert.False(SenderAuthentication.Evaluate("Kollega", "kollega@firma.example", "Received: internal\nFrom: x").Suspicious);
    }

    [Theory]
    [InlineData("support@pankki.example", "x@huijari.example", true)]
    [InlineData("Pankki (asiakaspalvelu@pankki.example)", "noreply@lahettaja.example", true)]
    [InlineData("Matti Meikäläinen (matti@firma.example)", "matti@firma.example", false)]
    [InlineData("info@firma.example", "uutiset@posti.firma.example", false)]
    [InlineData("Matti Meikäläinen", "matti@firma.example", false)]
    public void Display_name_with_a_foreign_address_is_a_signal(string name, string address, bool expected) =>
        Assert.Equal(expected, SenderAuthentication.Evaluate(name, address, "").Signals.Contains("nimi_ristiriita"));

    [Fact]
    public void Spoofing_cases_in_the_eval_set_are_flagged_and_others_are_not()
    {
        var set = EvalSet.Load(Path.Combine(AppContext.BaseDirectory, "synthetic-emails.json"));
        foreach (var c in set.Cases)
        {
            var m = c.ToMessage();
            Assert.Equal(c.Id.StartsWith("huijaus-", StringComparison.Ordinal),
                SenderAuthentication.Evaluate(m.SenderName, m.SenderAddress, m.Headers).Suspicious);
        }
    }

    [Fact]
    public async Task Failed_sender_check_forces_review_whatever_the_model_says()
    {
        using var env = new Env();
        env.Source.Headers = Fail;
        await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        var item = env.Store.GetReviewItems()[0];
        Assert.Equal("tarkista", item.Assessment!.Action);
        Assert.Contains(SenderAuthentication.ReviewReason, item.Assessment.ReviewReasons);
        Assert.StartsWith("Ohjelma: lähettäjän tunnistus", item.Assessment.Rationale);
    }
}

public class ServerIdentityTests
{
    [Fact]
    public async Task Batch_is_deferred_when_the_port_belongs_to_another_program()
    {
        using var env = new Env();
        var result = await env.Runner(new FixedIdentity("Porttia 1234 kuuntelee python, ei odotettu LM Studio.")).RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Deferred, result.Outcome);
        Assert.Contains("python", result.Reason);
        Assert.Equal(0, env.Server.Loads);
    }

    [Fact]
    public async Task Answer_from_another_model_stops_the_batch_and_keeps_the_queue()
    {
        using var env = new Env();
        env.Server.AnsweringModel = "joku-muu-malli";
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Stopped, result.Outcome);
        Assert.Equal(0, result.Assessed);
        Assert.Equal(2, env.Store.GetQueued(10).Count);
        Assert.Equal(1, env.Server.Unloads);
    }

    [Fact]
    public void Port_owner_check_reports_a_missing_listener()
    {
        // No program listens on port 1 on a normal machine.
        var problem = new LoopbackPortOwnerCheck(1, @"C:\ei\ole.exe").Problem();
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task Outlook_going_away_mid_batch_keeps_messages_queued_without_counting_attempts()
    {
        using var env = new Env();
        env.Source.Unavailable = true;
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Stopped, result.Outcome);
        Assert.Equal(1, env.Server.Unloads);
        Assert.Equal(2, env.Store.GetQueued(10).Count);
        Assert.All(env.Store.GetReviewItems(), i => Assert.Null(i.LastError));
    }

    internal sealed class FixedIdentity(string? problem) : IServerIdentityCheck
    {
        public string? Problem() => problem;
    }
}

public class CategoryTests
{
    private static Assessment A(string action, params string[] reasons) =>
        new("s", "r", "tyo", action, "ei_maaraaikaa", null, reasons.Length > 0, reasons);

    [Fact]
    public void Desired_category_comes_from_a_fixed_table_and_the_label_wins()
    {
        Assert.Equal("AI: Tehtävä", AiCategories.Desired(A("tehtava"), null));
        Assert.Equal("AI: Vastattava", AiCategories.Desired(A("tiedoksi"), new UserLabel("vastattava", "tanaan", "tyo", null)));
        Assert.Equal("AI: Tarkista", AiCategories.Desired(A("tehtava", InjectionHeuristics.ReviewReason), null));
        Assert.Equal("AI: Tarkista", AiCategories.Desired(A("vastattava", SenderAuthentication.ReviewReason), null));
        Assert.Null(AiCategories.Desired(null, null));
    }

    [Fact]
    public void Apply_keeps_other_categories_and_changes_only_own_ones()
    {
        var (after, changed, added) = AiCategories.Apply("Blue, Asiakas", ",", "AI: Tiedoksi", null);
        Assert.Equal("Blue, Asiakas, AI: Tiedoksi", after);
        Assert.True(changed && added);

        (after, changed, added) = AiCategories.Apply(after, ",", "AI: Tehtävä", "AI: Tiedoksi");
        Assert.Equal("Blue, Asiakas, AI: Tehtävä", after);

        (after, changed, added) = AiCategories.Apply("Blue, ai: tehtävä", ",", "AI: Tehtävä", null);
        Assert.False(changed || added);

        Assert.Throws<ArgumentException>(() => AiCategories.Apply("Blue", ",", "Huijaus: maksa heti", null));
        Assert.Throws<ArgumentException>(() => AiCategories.Apply("Blue", ",", null, "Blue"));
        Assert.Equal((null, true, false), AiCategories.Apply("AI: Tarkista", ",", null, "AI: Tarkista"));
    }

    [Fact]
    public async Task Off_mode_writes_nothing()
    {
        using var c = await CategoryEnv.CreateAsync();
        var result = await c.Sync.SyncAsync(CategoryMode.Off, null, CancellationToken.None);
        Assert.Equal(CategorySyncResult.None, result);
        Assert.Equal(0, c.Target.Calls);
    }

    [Fact]
    public async Task Test_mode_writes_only_to_the_message_with_the_exact_test_subject()
    {
        using var c = await CategoryEnv.CreateAsync();
        await c.Sync.SyncAsync(CategoryMode.Test, "AI: testiviesti", CancellationToken.None);
        Assert.Equal("Blue, AI: Tiedoksi", c.Target.Categories["e1"]);
        Assert.Equal("Blue", c.Target.Categories["e2"]);

        await c.Sync.SyncAsync(CategoryMode.Test, "", CancellationToken.None);
        Assert.Equal(1, c.Target.Calls);
    }

    [Fact]
    public async Task Same_change_is_not_repeated_and_a_category_the_user_removed_is_not_added_back()
    {
        using var c = await CategoryEnv.CreateAsync();
        var first = await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        Assert.Equal(2, first.Written);
        Assert.Equal(2, c.Target.Calls);

        c.Target.Categories["e1"] = "Blue"; // the user removes the AI category in Outlook
        var second = await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        Assert.Equal(0, second.Written);
        Assert.Equal(2, c.Target.Calls);
        Assert.Equal("Blue", c.Target.Categories["e1"]);
    }

    [Fact]
    public async Task Label_change_replaces_only_the_category_the_app_added()
    {
        using var c = await CategoryEnv.CreateAsync();
        await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        var item = c.Env.Store.GetReviewItems().First(i => i.Reference.EntryId == "e1");
        c.Env.Store.SaveLabel(item, new UserLabel("tehtava", "tanaan", "tyo", null), blind: false, DateTimeOffset.UtcNow);

        await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        Assert.Equal("Blue, AI: Tehtävä", c.Target.Categories["e1"]);
    }

    [Fact]
    public async Task Rollback_removes_only_own_categories_and_keeps_later_user_changes()
    {
        using var c = await CategoryEnv.CreateAsync();
        c.Target.Categories["e2"] = "Blue, AI: Tiedoksi"; // the user had set it already: not ours
        await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        c.Target.Categories["e1"] += ", Green"; // the user adds a category later

        var result = await c.Sync.RollbackAsync(CancellationToken.None);
        Assert.Equal(1, result.Written);
        Assert.Equal("Blue, Green", c.Target.Categories["e1"]);
        Assert.Equal("Blue, AI: Tiedoksi", c.Target.Categories["e2"]);
        Assert.Equal(0, c.Env.Store.CategorySummary().Owned);
    }

    [Fact]
    public async Task Changed_message_is_not_written_and_not_retried()
    {
        using var c = await CategoryEnv.CreateAsync();
        c.Target.ChangedContent.Add("e1");
        var result = await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        Assert.Equal(1, result.Blocked);
        Assert.Equal("Blue", c.Target.Categories["e1"]);

        var calls = c.Target.Calls;
        await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        Assert.Equal(calls, c.Target.Calls);
    }

    [Fact]
    public async Task Failed_write_is_retried_a_limited_number_of_times()
    {
        using var c = await CategoryEnv.CreateAsync();
        c.Target.ReadOnly.Add("e1");
        for (var i = 0; i < 5; i++)
        {
            await c.Sync.SyncAsync(CategoryMode.On, null, CancellationToken.None);
        }

        Assert.Equal(CategorySync.MaxAttempts, c.Target.CallsFor["e1"]);
        Assert.Equal(1, c.Target.CallsFor["e2"]);
    }

    internal sealed class CategoryEnv : IDisposable
    {
        public Env Env { get; } = new();
        public FakeTarget Target { get; } = new();
        public CategorySync Sync => new(Env.Store, Target, TimeProvider.System);

        public static async Task<CategoryEnv> CreateAsync()
        {
            var c = new CategoryEnv();
            c.Env.Source.SubjectFor = id => id == "e1" ? "AI: testiviesti" : "Muu";
            await c.Env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
            return c;
        }

        public void Dispose() => Env.Dispose();
    }

    internal sealed class FakeTarget : ICategoryTarget
    {
        public Dictionary<string, string> Categories { get; } = new() { ["e1"] = "Blue", ["e2"] = "Blue" };
        public HashSet<string> ChangedContent { get; } = [];
        public HashSet<string> ReadOnly { get; } = [];
        public Dictionary<string, int> CallsFor { get; } = [];
        public int Calls { get; private set; }

        public Task<CategoryWriteResult> ChangeAsync(MessageReference reference, CategoryChange change, CancellationToken ct)
        {
            Calls++;
            var id = reference.EntryId;
            CallsFor[id] = CallsFor.GetValueOrDefault(id) + 1;
            var subject = id == "e1" ? "AI: testiviesti" : "Muu";
            if (ChangedContent.Contains(id) || change.ExpectedContentHash != MessageIdentity.ContentHash(subject, "Runko") ||
                change.ExpectedMessageId != "<id@example>")
            {
                return Task.FromResult(CategoryWriteResult.Of(CategoryWriteStatus.Mismatch, "sisältö muuttunut"));
            }

            if (ReadOnly.Contains(id))
            {
                return Task.FromResult(CategoryWriteResult.Of(CategoryWriteStatus.Failed, "vain luku"));
            }

            var before = Categories[id];
            var (after, changed, added) = AiCategories.Apply(before, ",", change.Add, change.Remove);
            Categories[id] = after ?? "";
            return Task.FromResult(new CategoryWriteResult(changed ? CategoryWriteStatus.Written : CategoryWriteStatus.Unchanged, added, before, after, null));
        }
    }
}

public class MonitorTests
{
    private static AppSettings Settings(CategoryMode mode = CategoryMode.Off) => new()
    {
        Stores = [new MonitoredStore("store", "tyo@example.fi")],
        Model = "m",
        BatchMinQueue = 10,
        BatchMaxWaitMinutes = 20,
        DeferRetryMinutes = 5,
        CatchUpDays = 3,
        ScanOverlapMinutes = 60,
        RetentionDays = 90,
        CategoryMode = mode,
    };

    [Fact]
    public async Task First_scan_catches_up_and_later_scans_start_from_the_newest_message_seen()
    {
        using var m = new MonitorEnv();
        var newest = m.Time.Now.AddMinutes(-30);
        m.Scanner.Messages.Add(Message("n1", newest));
        await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(m.Time.Now.AddDays(-3), m.Scanner.LastSince);
        Assert.Equal(newest, m.Env.Store.GetScanMark("store"));

        m.Time.Now = m.Time.Now.AddHours(5); // e.g. sleep or a network outage
        await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(newest.AddMinutes(-60), m.Scanner.LastSince);
    }

    [Fact]
    public async Task Small_young_queue_waits_and_old_queue_is_processed()
    {
        using var m = new MonitorEnv();
        await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(0, m.Env.Server.Loads);

        m.Time.Now = m.Time.Now.AddMinutes(21);
        var status = await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(1, m.Env.Server.Loads);
        Assert.Equal(2, status.NewAssessments);
        Assert.Equal(0, status.Queued);
    }

    [Fact]
    public async Task Ten_queued_messages_start_a_batch_and_process_now_starts_one_at_once()
    {
        using var m = new MonitorEnv();
        for (var i = 0; i < 8; i++)
        {
            m.Scanner.Messages.Add(Message("q" + i, m.Time.Now.AddMinutes(-i)));
        }

        await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(1, m.Env.Server.Loads);

        using var other = new MonitorEnv();
        await other.Monitor.TickAsync(processNow: true, CancellationToken.None);
        Assert.Equal(1, other.Env.Server.Loads);
    }

    [Fact]
    public async Task Deferred_batch_waits_before_retrying_and_warns_after_an_hour()
    {
        using var m = new MonitorEnv();
        m.Env.Gpu.Generating = true;
        m.Time.Now = m.Time.Now.AddMinutes(21);
        var status = await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal("Automatic1111 generoi kuvaa", status.DeferralReason);
        var deferrals = m.Env.Store.RecentBatches(10).Count;

        m.Time.Now = m.Time.Now.AddMinutes(2);
        await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Equal(deferrals, m.Env.Store.RecentBatches(10).Count); // no new attempt within 5 minutes

        m.Time.Now = m.Time.Now.AddMinutes(61);
        status = await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.True(status.DeferralWarning);
        Assert.Equal(0, m.Env.Server.Loads);

        m.Env.Gpu.Generating = false;
        m.Time.Now = m.Time.Now.AddMinutes(6);
        status = await m.Monitor.TickAsync(false, CancellationToken.None);
        Assert.Null(status.DeferralReason);
        Assert.False(status.DeferralWarning);
        Assert.Equal(1, m.Env.Server.Loads);
    }

    [Fact]
    public async Task Outlook_unavailable_skips_the_batch_and_keeps_the_queue()
    {
        using var m = new MonitorEnv();
        m.Scanner.Unavailable = true;
        m.Time.Now = m.Time.Now.AddMinutes(30);
        var status = await m.Monitor.TickAsync(true, CancellationToken.None);
        Assert.Contains("Outlook", status.Problem);
        Assert.Equal(0, m.Env.Server.Loads);
        Assert.Equal(2, status.Queued);
    }

    [Fact]
    public async Task Process_now_runs_one_round_and_then_waits_for_the_interval()
    {
        using var m = new MonitorEnv();
        m.Monitor.ProcessNow();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await m.Monitor.RunAsync(cts.Token);
        Assert.Equal(1, m.Scanner.Calls); // the interval (2 min) did not pass in 3 seconds
        Assert.Equal(1, m.Env.Server.Loads);
    }

    [Fact]
    public async Task Paused_monitor_does_nothing()
    {
        using var m = new MonitorEnv();
        m.Monitor.Pause();
        var status = await m.Monitor.TickAsync(true, CancellationToken.None);
        Assert.True(status.Paused);
        Assert.Equal(0, m.Scanner.Calls);
        Assert.Equal(0, m.Env.Server.Loads);
    }

    [Fact]
    public async Task Categories_are_synced_after_the_batch_when_enabled()
    {
        using var m = new MonitorEnv(CategoryMode.On);
        var status = await m.Monitor.TickAsync(true, CancellationToken.None);
        Assert.Equal(2, status.LastSync!.Written);
        Assert.Equal("Blue, AI: Tiedoksi", m.Target.Categories["e1"]);
    }

    [Fact]
    public void Retention_deletes_old_messages_and_redacts_labelled_ones()
    {
        using var env = new Env();
        var old = DateTimeOffset.UtcNow.AddDays(-100);
        env.Store.Enqueue(new DiscoveredMessage(new MessageReference("store", "old1"), "tyo@example.fi", "Inbox", null, old), DateTimeOffset.UtcNow);
        env.Store.Enqueue(new DiscoveredMessage(new MessageReference("store", "old2"), "tyo@example.fi", "Inbox", null, old), DateTimeOffset.UtcNow);
        var labelled = env.Store.GetReviewItems().First(i => i.Reference.EntryId == "old2");
        env.Store.SaveLabel(labelled, new UserLabel("tiedoksi", "ei_maaraaikaa", "muu", null), blind: true, DateTimeOffset.UtcNow);

        var result = env.Store.ApplyRetention(DateTimeOffset.UtcNow.AddDays(-90));
        Assert.Equal(1, result.Deleted);
        var ids = env.Store.GetReviewItems().Select(i => i.Reference.EntryId).ToList();
        Assert.DoesNotContain("old1", ids);
        Assert.Contains("old2", ids);
        Assert.Contains("e1", ids);
    }

    private static DiscoveredMessage Message(string id, DateTimeOffset received) =>
        new(new MessageReference("store", id), "tyo@example.fi", "Inbox", "<id@example>", received);

    internal sealed class MonitorEnv : IDisposable
    {
        public MonitorEnv(CategoryMode mode = CategoryMode.Off)
        {
            var settings = Settings(mode);
            Monitor = new MonitorService(Env.Store, Scanner, Env.Runner(time: Time),
                new CategorySync(Env.Store, Target, Time), () => settings, Time);
            Env.Source.SubjectFor = id => id == "e1" ? "AI: testiviesti" : "Muu";
        }

        public Env Env { get; } = new();
        public ManualTime Time { get; } = new(DateTimeOffset.UtcNow);
        public FakeScanner Scanner { get; } = new();
        public CategoryTests.FakeTarget Target { get; } = new();
        public MonitorService Monitor { get; }

        public void Dispose() => Env.Dispose();
    }

    internal sealed class FakeScanner : IMailboxScanner
    {
        public List<DiscoveredMessage> Messages { get; } = [];
        public bool Unavailable { get; set; }
        public DateTimeOffset? LastSince { get; private set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DiscoveredMessage>> ListInboxSinceAsync(string storeId, DateTimeOffset since, int max, CancellationToken ct)
        {
            Calls++;
            LastSince = since;
            return Unavailable
                ? throw new MessageSourceUnavailableException("Outlook ei ole käynnissä.")
                : Task.FromResult<IReadOnlyList<DiscoveredMessage>>(Messages.Where(m => m.Received >= since).Take(max).ToList());
        }
    }

    internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
