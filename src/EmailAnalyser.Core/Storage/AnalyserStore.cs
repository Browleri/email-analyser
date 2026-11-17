using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmailAnalyser.Core.Storage;

public sealed record QueuedMessage(long Id, MessageReference Reference, string Mailbox, DateTimeOffset Received);

public sealed record BatchSummary(
    long Id, DateTimeOffset Started, DateTimeOffset? Ended, string? Model, string Outcome, string? Reason,
    double? LoadSeconds, int? GpuBefore, int? GpuLoaded, int? GpuAfter, int Assessed, int Invalid, bool Unloaded);

public sealed record AssessmentReport(
    int Messages,
    int Valid,
    int Invalid,
    IReadOnlyDictionary<string, int> ByAction,
    IReadOnlyDictionary<string, int> ByUrgency,
    IReadOnlyDictionary<string, int> ByTopic,
    IReadOnlyDictionary<string, int> ByMailbox,
    IReadOnlyDictionary<string, int> ReviewReasons,
    int NeedsReview,
    int WithDeadline,
    int Truncated,
    int HistoryShortened,
    int RetriedOnce,
    IReadOnlyList<long> DurationsMs,
    IReadOnlyList<int> PromptTokens,
    IReadOnlyList<int> CompletionTokens);

/// <summary>
/// Encrypted SQLite store (SQLite3 Multiple Ciphers, SQLCipher 4 format). Message
/// bodies are never stored; summaries, subjects and senders are, which is why the
/// whole file is encrypted.
/// </summary>
public sealed class AnalyserStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;

    private AnalyserStore(SqliteConnection connection) => _db = connection;

    static AnalyserStore() => SQLitePCL.Batteries_V2.Init();

    public static AnalyserStore Open(string path, string hexKey)
    {
        if (hexKey.Length != 64 || !hexKey.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Avaimen pitää olla 64 heksamerkkiä.", nameof(hexKey));
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            Exec(connection, "PRAGMA cipher = 'sqlcipher'");
            Exec(connection, "PRAGMA legacy = 4");
            Exec(connection, $"PRAGMA hexkey = '{hexKey}'");
            // Fails with "file is not a database" when the key is wrong.
            Exec(connection, "SELECT count(*) FROM sqlite_master");
            Exec(connection, "PRAGMA journal_mode = WAL");
            Exec(connection, "PRAGMA foreign_keys = ON");
            var store = new AnalyserStore(connection);
            store.Migrate();
            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>True if the file opens as a plain SQLite database without a key.</summary>
    public static bool OpensWithoutKey(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Exec(connection, "SELECT count(*) FROM sqlite_master");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public void Dispose() => _db.Dispose();

    // --- messages ------------------------------------------------------------------------------

    public bool Enqueue(DiscoveredMessage message, DateTimeOffset now)
    {
        using var cmd = Command("""
            INSERT OR IGNORE INTO messages
                (store_id, entry_id, mailbox, folder_path, internet_message_id, received_utc, status, discovered_utc, updated_utc)
            VALUES ($store, $entry, $mailbox, $folder, $mid, $received, 'queued', $now, $now)
            """);
        cmd.Parameters.AddWithValue("$store", message.Reference.StoreId);
        cmd.Parameters.AddWithValue("$entry", message.Reference.EntryId);
        cmd.Parameters.AddWithValue("$mailbox", message.Mailbox);
        cmd.Parameters.AddWithValue("$folder", message.FolderPath);
        cmd.Parameters.AddWithValue("$mid", (object?)message.InternetMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$received", Utc(message.Received));
        cmd.Parameters.AddWithValue("$now", Utc(now));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Queued messages, newest first.</summary>
    public IReadOnlyList<QueuedMessage> GetQueued(int limit)
    {
        using var cmd = Command("""
            SELECT id, store_id, entry_id, mailbox, received_utc FROM messages
            WHERE status = 'queued' ORDER BY received_utc DESC LIMIT $limit
            """);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<QueuedMessage>();
        while (reader.Read())
        {
            result.Add(new QueuedMessage(
                reader.GetInt64(0),
                new MessageReference(reader.GetString(1), reader.GetString(2)),
                reader.GetString(3),
                ParseUtc(reader.GetString(4))));
        }

        return result;
    }

    public void SaveAssessment(
        QueuedMessage message, MessageForAssessment read, ClassificationOutcome outcome,
        long batchId, string model, string promptVersion, string settingsJson, DateTimeOffset now)
    {
        using var tx = _db.BeginTransaction();
        var a = outcome.Assessment;
        using (var cmd = Command("""
            INSERT INTO assessments
                (message_id, created_utc, batch_id, model, prompt_version, settings_json, valid,
                 topic, action, urgency, deadline, summary, rationale, needs_review, review_reasons,
                 truncated, history_shortened, input_chars, original_chars,
                 prompt_tokens, completion_tokens, duration_ms, attempts, error)
            VALUES ($mid, $now, $batch, $model, $prompt, $settings, $valid,
                    $topic, $action, $urgency, $deadline, $summary, $rationale, $review, $reasons,
                    $truncated, $history, $input, $original,
                    $ptok, $ctok, $ms, $attempts, $error)
            """, tx))
        {
            cmd.Parameters.AddWithValue("$mid", message.Id);
            cmd.Parameters.AddWithValue("$now", Utc(now));
            cmd.Parameters.AddWithValue("$batch", batchId);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$prompt", promptVersion);
            cmd.Parameters.AddWithValue("$settings", settingsJson);
            cmd.Parameters.AddWithValue("$valid", a is null ? 0 : 1);
            cmd.Parameters.AddWithValue("$topic", (object?)a?.Topic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$action", (object?)a?.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$urgency", (object?)a?.Urgency ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$deadline", (object?)a?.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$summary", (object?)a?.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rationale", (object?)a?.Rationale ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$review", a is null ? 1 : a.NeedsReview ? 1 : 0);
            cmd.Parameters.AddWithValue("$reasons", JsonSerializer.Serialize(a?.ReviewReasons ?? []));
            cmd.Parameters.AddWithValue("$truncated", outcome.Prepared.Truncated ? 1 : 0);
            cmd.Parameters.AddWithValue("$history", outcome.Prepared.HistoryShortened ? 1 : 0);
            cmd.Parameters.AddWithValue("$input", outcome.Prepared.Text.Length);
            cmd.Parameters.AddWithValue("$original", outcome.Prepared.OriginalLength);
            cmd.Parameters.AddWithValue("$ptok", (object?)outcome.PromptTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ctok", (object?)outcome.CompletionTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", (long)outcome.Duration.TotalMilliseconds);
            cmd.Parameters.AddWithValue("$attempts", outcome.Attempts);
            cmd.Parameters.AddWithValue("$error", outcome.IsValid ? DBNull.Value : string.Join("; ", outcome.Errors));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = Command("""
            UPDATE messages SET status = $status, attempts = attempts + 1, last_error = $error,
                sender_name = $sname, sender_address = $saddr, subject = $subject, content_hash = $hash, updated_utc = $now
            WHERE id = $id
            """, tx))
        {
            cmd.Parameters.AddWithValue("$status", outcome.IsValid ? "assessed" : "invalid");
            cmd.Parameters.AddWithValue("$error", outcome.IsValid ? DBNull.Value : string.Join("; ", outcome.Errors));
            cmd.Parameters.AddWithValue("$sname", read.SenderName);
            cmd.Parameters.AddWithValue("$saddr", read.SenderAddress);
            cmd.Parameters.AddWithValue("$subject", read.Subject);
            cmd.Parameters.AddWithValue("$hash", ContentHash(read));
            cmd.Parameters.AddWithValue("$now", Utc(now));
            cmd.Parameters.AddWithValue("$id", message.Id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void MarkUnavailable(QueuedMessage message, ReadStatus status, string reason, DateTimeOffset now)
    {
        using var cmd = Command("UPDATE messages SET status = $status, last_error = $reason, updated_utc = $now WHERE id = $id");
        cmd.Parameters.AddWithValue("$status", status == ReadStatus.Gone ? "gone" : "unreadable");
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.ExecuteNonQuery();
    }

    public static string ContentHash(MessageForAssessment message) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Subject + "\n" + message.Body)))[..32];

    // --- batches -------------------------------------------------------------------------------

    public long StartBatch(string model, int? gpuBefore, DateTimeOffset now)
    {
        using var cmd = Command("""
            INSERT INTO batch_runs (started_utc, model, outcome, gpu_mib_before) VALUES ($now, $model, 'käynnissä', $gpu);
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$gpu", (object?)gpuBefore ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void RecordDeferred(string model, string reason, DateTimeOffset now)
    {
        using var cmd = Command("""
            INSERT INTO batch_runs (started_utc, ended_utc, model, outcome, reason, unloaded)
            VALUES ($now, $now, $model, 'lykätty', $reason, 1)
            """);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.ExecuteNonQuery();
    }

    public void RecordLoad(long batchId, string instanceId, double loadSeconds, int? gpuLoaded)
    {
        using var cmd = Command("UPDATE batch_runs SET instance_id = $instance, load_seconds = $load, gpu_mib_loaded = $gpu WHERE id = $id");
        cmd.Parameters.AddWithValue("$instance", instanceId);
        cmd.Parameters.AddWithValue("$load", loadSeconds);
        cmd.Parameters.AddWithValue("$gpu", (object?)gpuLoaded ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", batchId);
        cmd.ExecuteNonQuery();
    }

    public void RecordUnload(long batchId)
    {
        using var cmd = Command("UPDATE batch_runs SET unloaded = 1 WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", batchId);
        cmd.ExecuteNonQuery();
    }

    public void FinishBatch(long batchId, string outcome, string? reason, int? gpuAfter, DateTimeOffset now)
    {
        using var cmd = Command("""
            UPDATE batch_runs SET ended_utc = $now, outcome = $outcome, reason = $reason, gpu_mib_after = $gpu,
                assessed = (SELECT count(*) FROM assessments WHERE batch_id = $id AND valid = 1),
                invalid = (SELECT count(*) FROM assessments WHERE batch_id = $id AND valid = 0)
            WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gpu", (object?)gpuAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", batchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Instances this application loaded but never confirmed unloading (e.g. after a crash).</summary>
    public IReadOnlyList<(long BatchId, string InstanceId)> GetUnreleasedInstances()
    {
        using var cmd = Command("SELECT id, instance_id FROM batch_runs WHERE instance_id IS NOT NULL AND unloaded = 0");
        using var reader = cmd.ExecuteReader();
        var result = new List<(long, string)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    // --- reporting -----------------------------------------------------------------------------

    public IReadOnlyDictionary<string, int> CountByStatus() =>
        CountBy("SELECT status, count(*) FROM messages GROUP BY status");

    public IReadOnlyList<BatchSummary> RecentBatches(int count)
    {
        using var cmd = Command("""
            SELECT id, started_utc, ended_utc, model, outcome, reason, load_seconds,
                   gpu_mib_before, gpu_mib_loaded, gpu_mib_after, assessed, invalid, unloaded
            FROM batch_runs ORDER BY id DESC LIMIT $n
            """);
        cmd.Parameters.AddWithValue("$n", count);
        using var r = cmd.ExecuteReader();
        var result = new List<BatchSummary>();
        while (r.Read())
        {
            result.Add(new BatchSummary(
                r.GetInt64(0), ParseUtc(r.GetString(1)), r.IsDBNull(2) ? null : ParseUtc(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDouble(6), NullableInt(r, 7), NullableInt(r, 8), NullableInt(r, 9),
                r.GetInt32(10), r.GetInt32(11), r.GetInt32(12) == 1));
        }

        return result;
    }

    /// <summary>Aggregates over each message's latest assessment. Contains no message content.</summary>
    public AssessmentReport BuildReport()
    {
        const string latest = """
            FROM assessments a
            JOIN (SELECT message_id, max(id) AS id FROM assessments GROUP BY message_id) l ON a.id = l.id
            JOIN messages m ON m.id = a.message_id
            """;
        var durations = new List<long>();
        var promptTokens = new List<int>();
        var completionTokens = new List<int>();
        var reasons = new Dictionary<string, int>();
        int messages = 0, valid = 0, review = 0, deadline = 0, truncated = 0, history = 0, retried = 0;

        using (var cmd = Command($"SELECT a.valid, a.needs_review, a.deadline, a.truncated, a.history_shortened, a.attempts, a.duration_ms, a.prompt_tokens, a.completion_tokens, a.review_reasons {latest}"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                messages++;
                var isValid = r.GetInt32(0) == 1;
                valid += isValid ? 1 : 0;
                review += isValid && r.GetInt32(1) == 1 ? 1 : 0;
                deadline += r.IsDBNull(2) ? 0 : 1;
                truncated += r.GetInt32(3);
                history += r.GetInt32(4);
                retried += r.GetInt32(5) > 1 ? 1 : 0;
                durations.Add(r.GetInt64(6));
                if (!r.IsDBNull(7)) promptTokens.Add(r.GetInt32(7));
                if (!r.IsDBNull(8)) completionTokens.Add(r.GetInt32(8));
                foreach (var reason in JsonSerializer.Deserialize<string[]>(r.GetString(9)) ?? [])
                {
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
            }
        }

        return new AssessmentReport(
            messages, valid, messages - valid,
            CountBy($"SELECT a.action, count(*) {latest} WHERE a.valid = 1 GROUP BY a.action"),
            CountBy($"SELECT a.urgency, count(*) {latest} WHERE a.valid = 1 GROUP BY a.urgency"),
            CountBy($"SELECT a.topic, count(*) {latest} WHERE a.valid = 1 GROUP BY a.topic"),
            CountBy($"SELECT m.mailbox, count(*) {latest} GROUP BY m.mailbox"),
            reasons, review, deadline, truncated, history, retried,
            durations, promptTokens, completionTokens);
    }

    /// <summary>Subjects of assessed messages, used only to search LM Studio logs for leaks.</summary>
    public IReadOnlyList<string> AssessedSubjects()
    {
        using var cmd = Command("SELECT subject FROM messages WHERE subject IS NOT NULL AND status IN ('assessed', 'invalid')");
        using var r = cmd.ExecuteReader();
        var result = new List<string>();
        while (r.Read())
        {
            result.Add(r.GetString(0));
        }

        return result;
    }

    // --- schema --------------------------------------------------------------------------------

    private void Migrate()
    {
        using var versionCmd = Command("PRAGMA user_version");
        var version = Convert.ToInt32(versionCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"Tietokannan versio {version} on uudempi kuin tämä ohjelma ({SchemaVersion}).");
        }

        if (version < 1)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY,
                    store_id TEXT NOT NULL,
                    entry_id TEXT NOT NULL,
                    mailbox TEXT NOT NULL,
                    folder_path TEXT NOT NULL,
                    internet_message_id TEXT,
                    received_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT,
                    content_hash TEXT,
                    sender_name TEXT,
                    sender_address TEXT,
                    subject TEXT,
                    discovered_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    UNIQUE (store_id, entry_id)
                );
                CREATE INDEX ix_messages_status ON messages (status, received_utc);
                CREATE TABLE batch_runs (
                    id INTEGER PRIMARY KEY,
                    started_utc TEXT NOT NULL,
                    ended_utc TEXT,
                    model TEXT,
                    instance_id TEXT,
                    outcome TEXT NOT NULL,
                    reason TEXT,
                    load_seconds REAL,
                    gpu_mib_before INTEGER,
                    gpu_mib_loaded INTEGER,
                    gpu_mib_after INTEGER,
                    assessed INTEGER NOT NULL DEFAULT 0,
                    invalid INTEGER NOT NULL DEFAULT 0,
                    unloaded INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE assessments (
                    id INTEGER PRIMARY KEY,
                    message_id INTEGER NOT NULL REFERENCES messages (id) ON DELETE CASCADE,
                    created_utc TEXT NOT NULL,
                    batch_id INTEGER REFERENCES batch_runs (id),
                    model TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    settings_json TEXT NOT NULL,
                    valid INTEGER NOT NULL,
                    topic TEXT,
                    action TEXT,
                    urgency TEXT,
                    deadline TEXT,
                    summary TEXT,
                    rationale TEXT,
                    needs_review INTEGER NOT NULL,
                    review_reasons TEXT NOT NULL,
                    truncated INTEGER NOT NULL,
                    history_shortened INTEGER NOT NULL,
                    input_chars INTEGER NOT NULL,
                    original_chars INTEGER NOT NULL,
                    prompt_tokens INTEGER,
                    completion_tokens INTEGER,
                    duration_ms INTEGER NOT NULL,
                    attempts INTEGER NOT NULL,
                    error TEXT
                );
                CREATE INDEX ix_assessments_message ON assessments (message_id);
                PRAGMA user_version = 1;
                """, tx);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    // --- helpers -------------------------------------------------------------------------------

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private Dictionary<string, int> CountBy(string sql)
    {
        using var cmd = Command(sql);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, int>();
        while (r.Read())
        {
            result[r.IsDBNull(0) ? "(ei)" : r.GetString(0)] = r.GetInt32(1);
        }

        return result;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int? NullableInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static string Utc(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
