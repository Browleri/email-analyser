using System.Security.Cryptography;
using System.Text;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using Microsoft.Data.Sqlite;

namespace EmailAnalyser.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ea-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class StorageTests
{
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Database_is_encrypted_and_needs_the_right_key()
    {
        using var dir = new TempDir();
        var path = dir.File("a.db");
        using (var store = AnalyserStore.Open(path, Key))
        {
            store.Enqueue(Discovered("e1"), DateTimeOffset.UtcNow);
        }

        var header = Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(path), 0, 15);
        Assert.NotEqual("SQLite format 3", header);
        Assert.False(AnalyserStore.OpensWithoutKey(path));
        Assert.ThrowsAny<SqliteException>(() => AnalyserStore.Open(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).Dispose());

        using var reopened = AnalyserStore.Open(path, Key);
        Assert.Single(reopened.GetQueued(10));
    }

    [Fact]
    public void Enqueue_ignores_duplicates()
    {
        using var dir = new TempDir();
        using var store = AnalyserStore.Open(dir.File("a.db"), Key);
        Assert.True(store.Enqueue(Discovered("e1"), DateTimeOffset.UtcNow));
        Assert.False(store.Enqueue(Discovered("e1"), DateTimeOffset.UtcNow));
        Assert.Equal(1, store.CountByStatus()["queued"]);
    }

    [Fact]
    public void Dpapi_key_round_trips_and_is_not_stored_in_plain_text()
    {
        using var dir = new TempDir();
        var path = dir.File("database.key");
        var key = KeyStore.GetOrCreateHexKey(path);
        Assert.Equal(64, key.Length);
        Assert.Equal(key, KeyStore.GetOrCreateHexKey(path));
        var fileBytes = System.IO.File.ReadAllBytes(path);
        Assert.Equal(-1, fileBytes.AsSpan().IndexOf(Convert.FromHexString(key)));
        Assert.DoesNotContain(key, Encoding.ASCII.GetString(fileBytes), StringComparison.OrdinalIgnoreCase);
    }

    internal static DiscoveredMessage Discovered(string entryId) =>
        new(new MessageReference("store", entryId), "tyo@example.fi", @"\\tyo\Inbox", "<id@example>", DateTimeOffset.UtcNow);
}

public class BatchRunnerTests
{
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private const string GoodAnswer = """
        {"yhteenveto":"Testi.","perustelu":"Testi.","aihe":"tyo","toimenpide":"tiedoksi","kiireellisyys":"ei_maaraaikaa",
         "maaraaika":null,"tarkistettava":false,"tarkistuksen_syyt":[]}
        """;

    [Fact]
    public async Task Defers_when_Automatic1111_is_generating()
    {
        using var env = new Env();
        env.Gpu.Generating = true;
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Deferred, result.Outcome);
        Assert.Equal(0, env.Server.Loads);
        Assert.Equal(2, env.Store.GetQueued(10).Count);
    }

    [Fact]
    public async Task Defers_when_another_model_is_loaded()
    {
        using var env = new Env();
        env.Server.Loaded.Add(new LoadedInstance("prompter-model", "prompter-model"));
        var result = await env.Runner().RunAsync(new BatchOptions("m", MaxMessages: 1), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Deferred, result.Outcome);
        Assert.Contains("prompter-model", result.Reason);
        Assert.Equal(2, result.Remaining);
        Assert.Equal(0, env.Server.Loads);
    }

    [Fact]
    public async Task Missing_model_server_leaves_messages_queued()
    {
        using var env = new Env();
        env.Server.Down = true;
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Deferred, result.Outcome);
        Assert.Equal(2, env.Store.GetQueued(10).Count);
    }

    [Fact]
    public async Task Classifies_then_unloads()
    {
        using var env = new Env();
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Assessed);
        Assert.Equal(1, env.Server.Unloads);
        Assert.Empty(env.Store.GetQueued(10));
        Assert.True(env.Store.RecentBatches(1)[0].Unloaded);
    }

    [Fact]
    public async Task Unloads_and_keeps_queue_when_the_server_fails_mid_batch()
    {
        using var env = new Env();
        env.Server.FailChat = true;
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Stopped, result.Outcome);
        Assert.Equal(1, env.Server.Unloads);
        Assert.Equal(2, env.Store.GetQueued(10).Count);
    }

    [Fact]
    public async Task Invalid_answer_is_retried_once_and_stored_as_invalid()
    {
        using var env = new Env();
        env.Server.Answer = "{\"rikki\":true}";
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(2, result.Invalid);
        Assert.Equal(4, env.Server.Chats);
        Assert.Equal(2, env.Store.CountByStatus()["invalid"]);
    }

    [Fact]
    public async Task Stops_when_Automatic1111_starts_mid_batch()
    {
        using var env = new Env();
        env.Gpu.StartGeneratingAfterCalls = 2; // precondition + first message pass
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Stopped, result.Outcome);
        Assert.Equal(1, result.Assessed);
        Assert.Equal(1, env.Server.Unloads);
        Assert.Single(env.Store.GetQueued(10));
    }

    [Fact]
    public async Task Releases_an_orphaned_own_instance_before_starting()
    {
        using var env = new Env();
        var orphanBatch = env.Store.StartBatch("m", null, DateTimeOffset.UtcNow);
        env.Store.RecordLoad(orphanBatch, "m", 1, null);
        env.Server.Loaded.Add(new LoadedInstance("m", "m"));
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(BatchOutcome.Completed, result.Outcome);
        Assert.Equal(2, env.Server.Unloads);
        Assert.Empty(env.Store.GetUnreleasedInstances());
    }

    [Fact]
    public async Task Gone_messages_are_marked_and_skipped()
    {
        using var env = new Env();
        env.Source.Gone.Add("e1");
        var result = await env.Runner().RunAsync(new BatchOptions("m"), null, CancellationToken.None);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal(1, result.Assessed);
        Assert.Equal(1, env.Store.CountByStatus()["gone"]);
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir _dir = new();

        public Env()
        {
            Store = AnalyserStore.Open(_dir.File("a.db"), Key);
            Store.Enqueue(StorageTests.Discovered("e1"), DateTimeOffset.UtcNow);
            Store.Enqueue(StorageTests.Discovered("e2"), DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        public AnalyserStore Store { get; }
        public FakeServer Server { get; } = new();
        public FakeGpu Gpu { get; } = new();
        public FakeSource Source { get; } = new();

        public BatchRunner Runner() =>
            new(Store, Server, Gpu, Source, new Classifier(Server, PromptTemplate.LoadEmbedded(), new ClassifierOptions()), TimeProvider.System);

        public void Dispose()
        {
            Store.Dispose();
            _dir.Dispose();
        }
    }

    private sealed class FakeServer : ILanguageModelServer
    {
        public List<LoadedInstance> Loaded { get; } = [];
        public bool Down { get; set; }
        public bool FailChat { get; set; }
        public string Answer { get; set; } = GoodAnswer;
        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        public int Chats { get; private set; }

        public Task<IReadOnlyList<LoadedInstance>> GetLoadedInstancesAsync(CancellationToken ct) =>
            Down ? throw new ModelServerException("LM Studio ei vastaa.") : Task.FromResult<IReadOnlyList<LoadedInstance>>(Loaded.ToList());

        public Task<LoadResult> LoadAsync(string modelKey, int contextLength, CancellationToken ct)
        {
            Loads++;
            Loaded.Add(new LoadedInstance(modelKey, modelKey));
            return Task.FromResult(new LoadResult(modelKey, 1));
        }

        public Task UnloadAsync(string instanceId, CancellationToken ct)
        {
            Unloads++;
            Loaded.RemoveAll(l => l.InstanceId == instanceId);
            return Task.CompletedTask;
        }

        public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct)
        {
            Chats++;
            return FailChat
                ? throw new ModelServerException("LM Studio ei vastannut ajoissa.")
                : Task.FromResult(new ChatResponse(Answer, "stop", 100, 50, TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class FakeGpu : IGpuActivityProbe
    {
        private int _calls;
        public bool Generating { get; set; }
        public int? StartGeneratingAfterCalls { get; set; }

        public Task<bool> IsGeneratingAsync(CancellationToken ct)
        {
            _calls++;
            return Task.FromResult(Generating || _calls > StartGeneratingAfterCalls);
        }
    }

    private sealed class FakeSource : IMessageSource
    {
        public HashSet<string> Gone { get; } = [];

        public Task<MessageReadResult> ReadAsync(MessageReference reference, string mailbox, CancellationToken ct) =>
            Task.FromResult(Gone.Contains(reference.EntryId)
                ? MessageReadResult.Gone()
                : MessageReadResult.Found(new MessageForAssessment(mailbox, "Lähettäjä", "a@example.fi", "Minä", "", DateTimeOffset.UtcNow, "Otsikko", "Runko")));
    }
}
