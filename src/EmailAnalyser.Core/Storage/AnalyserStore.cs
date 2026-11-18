using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmailAnalyser.Core.Storage;

public sealed record QueuedMessage(long Id, MessageReference Reference, string Mailbox, DateTimeOffset Received);

/// <summary>The user's own judgement of a message: ground truth for quality measurement.</summary>
public sealed record UserLabel(string Action, string Urgency, string Topic, DateOnly? Deadline, bool AgreesWithAi = false, string Split = "", bool Blind = false, DateTimeOffset LabeledAt = default);

/// <summary>A message with its latest assessment and the user's label, for the review view.</summary>
public sealed record ReviewItem(
    long MessageId,
    MessageReference Reference,
    string Mailbox,
    string? SenderName,
    string? SenderAddress,
    string? Subject,
    DateTimeOffset Received,
    string Status,
    string? LastError,
    long? AssessmentId,
    Assessment? Assessment,
    bool Truncated,
    string? Model,
    string? PromptVersion,
    UserLabel? Label,
    string? CategoryOwned = null,
    string? CategoryStatus = null,
    DateTimeOffset? DoneAt = null);

/// <summary>This application's category state for one message.</summary>
public sealed record CategoryState(string? Desired, string? Owned, string Status, int Attempts);

/// <summary>A message that may need its Outlook category changed.</summary>
public sealed record CategoryCandidate(
    long MessageId,
    MessageReference Reference,
    string? Subject,
    string? InternetMessageId,
    string? ContentHash,
    Assessment? Assessment,
    UserLabel? Label,
    CategoryState? State);

public sealed record RetentionResult(int Deleted, int Redacted);

public static class LabelSplit
{
    public const string Tuning = "saato";
    public const string Holdout = "arviointi";

    /// <summary>Stable assignment: about one third tuning, two thirds holdout.</summary>
    public static string For(MessageReference reference) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(reference.StoreId + "|" + reference.EntryId))[0] % 3 == 0 ? Tuning : Holdout;
}

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
    private const int SchemaVersion = 4;
    public const int MaxMessageAttempts = 3;
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
            // The view and the monitor use separate connections to the same file.
            Exec(connection, "PRAGMA busy_timeout = 10000");
            // Deleted rows (retention) are overwritten instead of left in free pages.
            Exec(connection, "PRAGMA secure_delete = ON");
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

    /// <summary>
    /// Counts a failed attempt against the message. It is failed for good when
    /// <paramref name="failNow"/> or after <see cref="MaxMessageAttempts"/> attempts;
    /// otherwise it stays queued.
    /// </summary>
    public void RecordFailure(QueuedMessage message, string reason, bool failNow, DateTimeOffset now)
    {
        using var cmd = Command("""
            UPDATE messages SET attempts = attempts + 1, last_error = $reason, updated_utc = $now,
                status = CASE WHEN $failNow = 1 OR attempts + 1 >= $max THEN 'failed' ELSE status END
            WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.Parameters.AddWithValue("$failNow", failNow ? 1 : 0);
        cmd.Parameters.AddWithValue("$max", MaxMessageAttempts);
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.ExecuteNonQuery();
    }

    // --- review and labels ---------------------------------------------------------------------

    public IReadOnlyList<ReviewItem> GetReviewItems()
    {
        using var cmd = Command("""
            SELECT m.id, m.store_id, m.entry_id, m.mailbox, m.sender_name, m.sender_address, m.subject, m.received_utc,
                   m.status, m.last_error,
                   a.id, a.valid, a.summary, a.rationale, a.topic, a.action, a.urgency, a.deadline, a.needs_review,
                   a.review_reasons, a.truncated, a.model, a.prompt_version,
                   l.action, l.urgency, l.topic, l.deadline, l.agrees_with_ai, l.split, l.blind, l.labeled_utc,
                   c.owned, c.status, d.done_utc
            FROM messages m
            LEFT JOIN (SELECT message_id, max(id) AS id FROM assessments GROUP BY message_id) la ON la.message_id = m.id
            LEFT JOIN assessments a ON a.id = la.id
            LEFT JOIN labels l ON l.message_id = m.id
            LEFT JOIN category_state c ON c.message_id = m.id
            LEFT JOIN done d ON d.message_id = m.id
            ORDER BY m.received_utc DESC
            """);
        using var r = cmd.ExecuteReader();
        var result = new List<ReviewItem>();
        while (r.Read())
        {
            Assessment? assessment = null;
            if (!r.IsDBNull(10) && r.GetInt32(11) == 1)
            {
                assessment = new Assessment(
                    r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15), r.GetString(16),
                    r.IsDBNull(17) ? null : DateOnly.ParseExact(r.GetString(17), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    r.GetInt32(18) == 1,
                    JsonSerializer.Deserialize<string[]>(r.GetString(19)) ?? []);
            }

            UserLabel? label = r.IsDBNull(23) ? null : new UserLabel(
                r.GetString(23), r.GetString(24), r.GetString(25),
                r.IsDBNull(26) ? null : DateOnly.ParseExact(r.GetString(26), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetInt32(27) == 1, r.GetString(28), r.GetInt32(29) == 1, ParseUtc(r.GetString(30)));

            result.Add(new ReviewItem(
                r.GetInt64(0), new MessageReference(r.GetString(1), r.GetString(2)), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                ParseUtc(r.GetString(7)), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetInt64(10), assessment, !r.IsDBNull(20) && r.GetInt32(20) == 1,
                r.IsDBNull(21) ? null : r.GetString(21), r.IsDBNull(22) ? null : r.GetString(22), label,
                r.IsDBNull(31) ? null : r.GetString(31), r.IsDBNull(32) ? null : r.GetString(32),
                r.IsDBNull(33) ? null : ParseUtc(r.GetString(33))));
        }

        return result;
    }

    /// <summary>
    /// Marks a message done in this application only, or undoes it. Nothing is written to
    /// Outlook, and the label and assessments are kept.
    /// </summary>
    public void SetDone(long messageId, bool done, DateTimeOffset now)
    {
        using var cmd = Command(done
            ? "INSERT INTO done (message_id, done_utc) VALUES ($id, $now) ON CONFLICT (message_id) DO NOTHING"
            : "DELETE FROM done WHERE message_id = $id");
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Saves or replaces the user's label. Returns the stored label with split and agreement.</summary>
    public UserLabel SaveLabel(ReviewItem item, UserLabel label, bool blind, DateTimeOffset now)
    {
        if (!Labels.Actions.Contains(label.Action) || !Labels.Urgencies.Contains(label.Urgency) || !Labels.Topics.Contains(label.Topic))
        {
            throw new ArgumentException("Merkinnässä on sallimaton arvo.");
        }

        var a = item.Assessment;
        var agrees = a is not null && a.Action == label.Action && a.Urgency == label.Urgency &&
                     a.Topic == label.Topic && a.Deadline == label.Deadline;
        var stored = label with { AgreesWithAi = agrees, Split = LabelSplit.For(item.Reference), Blind = blind, LabeledAt = now };
        using var cmd = Command("""
            INSERT INTO labels (message_id, action, urgency, topic, deadline, agrees_with_ai, assessment_id, split, blind, labeled_utc)
            VALUES ($id, $action, $urgency, $topic, $deadline, $agrees, $assessment, $split, $blind, $now)
            ON CONFLICT (message_id) DO UPDATE SET action = excluded.action, urgency = excluded.urgency, topic = excluded.topic,
                deadline = excluded.deadline, agrees_with_ai = excluded.agrees_with_ai, assessment_id = excluded.assessment_id,
                blind = excluded.blind, labeled_utc = excluded.labeled_utc
            """);
        cmd.Parameters.AddWithValue("$id", item.MessageId);
        cmd.Parameters.AddWithValue("$action", stored.Action);
        cmd.Parameters.AddWithValue("$urgency", stored.Urgency);
        cmd.Parameters.AddWithValue("$topic", stored.Topic);
        cmd.Parameters.AddWithValue("$deadline", (object?)stored.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agrees", agrees ? 1 : 0);
        cmd.Parameters.AddWithValue("$assessment", (object?)item.AssessmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$split", stored.Split);
        cmd.Parameters.AddWithValue("$blind", blind ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.ExecuteNonQuery();
        return stored;
    }

    public void DeleteLabel(long messageId)
    {
        using var cmd = Command("DELETE FROM labels WHERE message_id = $id");
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>All labels with the identifiers needed to find their messages again (no subjects, senders or assessments).</summary>
    public IReadOnlyList<ExportedLabel> ExportLabels()
    {
        using var cmd = Command("""
            SELECT m.store_id, m.entry_id, m.internet_message_id, m.mailbox, m.folder_path, m.received_utc,
                   l.action, l.urgency, l.topic, l.deadline, l.agrees_with_ai, l.split, l.blind, l.labeled_utc
            FROM labels l JOIN messages m ON m.id = l.message_id
            ORDER BY l.labeled_utc
            """);
        using var r = cmd.ExecuteReader();
        var result = new List<ExportedLabel>();
        while (r.Read())
        {
            result.Add(new ExportedLabel(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                ParseUtc(r.GetString(5)), r.GetString(6), r.GetString(7), r.GetString(8),
                r.IsDBNull(9) ? null : DateOnly.ParseExact(r.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetInt32(10) == 1, r.GetString(11), r.GetInt32(12) == 1, ParseUtc(r.GetString(13))));
        }

        return result;
    }

    /// <summary>
    /// Restores exported labels in one transaction. A message is found by its reference,
    /// then by a unique Message-ID in the same store; a missing one is queued as a reference
    /// so that it is read from Outlook and assessed again. A label newer than the exported
    /// one is kept, and the exported split is kept so a message never moves between parts.
    /// </summary>
    public LabelImportResult ImportLabels(IReadOnlyList<ExportedLabel> labels, DateTimeOffset now)
    {
        int imported = 0, created = 0, keptNewer = 0, rejected = 0;
        using var tx = _db.BeginTransaction();
        foreach (var label in labels)
        {
            if (!LabelBackup.IsValid(label))
            {
                rejected++;
                continue;
            }

            var messageId = FindMessage(label, tx);
            if (messageId is null)
            {
                using var insert = Command("""
                    INSERT INTO messages
                        (store_id, entry_id, mailbox, folder_path, internet_message_id, received_utc, status, discovered_utc, updated_utc)
                    VALUES ($store, $entry, $mailbox, $folder, $mid, $received, 'queued', $now, $now)
                    RETURNING id
                    """, tx);
                insert.Parameters.AddWithValue("$store", label.StoreId);
                insert.Parameters.AddWithValue("$entry", label.EntryId);
                insert.Parameters.AddWithValue("$mailbox", label.Mailbox);
                insert.Parameters.AddWithValue("$folder", label.FolderPath);
                insert.Parameters.AddWithValue("$mid", (object?)label.InternetMessageId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$received", Utc(label.Received));
                insert.Parameters.AddWithValue("$now", Utc(now));
                messageId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                created++;
            }

            using (var existing = Command("SELECT labeled_utc FROM labels WHERE message_id = $id", tx))
            {
                existing.Parameters.AddWithValue("$id", messageId);
                if (existing.ExecuteScalar() is string labeledUtc && ParseUtc(labeledUtc) >= label.LabeledAt)
                {
                    keptNewer++;
                    continue;
                }
            }

            using var upsert = Command("""
                INSERT INTO labels (message_id, action, urgency, topic, deadline, agrees_with_ai, assessment_id, split, blind, labeled_utc)
                VALUES ($id, $action, $urgency, $topic, $deadline, $agrees, NULL, $split, $blind, $labeled)
                ON CONFLICT (message_id) DO UPDATE SET action = excluded.action, urgency = excluded.urgency, topic = excluded.topic,
                    deadline = excluded.deadline, agrees_with_ai = excluded.agrees_with_ai, assessment_id = NULL,
                    blind = excluded.blind, labeled_utc = excluded.labeled_utc
                """, tx);
            upsert.Parameters.AddWithValue("$id", messageId);
            upsert.Parameters.AddWithValue("$action", label.Action);
            upsert.Parameters.AddWithValue("$urgency", label.Urgency);
            upsert.Parameters.AddWithValue("$topic", label.Topic);
            upsert.Parameters.AddWithValue("$deadline", (object?)label.Deadline?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$agrees", label.AgreesWithAi ? 1 : 0);
            upsert.Parameters.AddWithValue("$split", label.Split);
            upsert.Parameters.AddWithValue("$blind", label.Blind ? 1 : 0);
            upsert.Parameters.AddWithValue("$labeled", Utc(label.LabeledAt));
            upsert.ExecuteNonQuery();
            imported++;
        }

        tx.Commit();
        return new LabelImportResult(imported, created, keptNewer, rejected);
    }

    private long? FindMessage(ExportedLabel label, SqliteTransaction tx)
    {
        using (var byReference = Command("SELECT id FROM messages WHERE store_id = $store AND entry_id = $entry", tx))
        {
            byReference.Parameters.AddWithValue("$store", label.StoreId);
            byReference.Parameters.AddWithValue("$entry", label.EntryId);
            if (byReference.ExecuteScalar() is long id)
            {
                return id;
            }
        }

        if (label.InternetMessageId is null)
        {
            return null;
        }

        // Message-ID can repeat in copies, so it is used only when it is unique in the store.
        using var byMessageId = Command("SELECT id FROM messages WHERE store_id = $store AND internet_message_id = $mid LIMIT 2", tx);
        byMessageId.Parameters.AddWithValue("$store", label.StoreId);
        byMessageId.Parameters.AddWithValue("$mid", label.InternetMessageId);
        using var r = byMessageId.ExecuteReader();
        var ids = new List<long>();
        while (r.Read())
        {
            ids.Add(r.GetInt64(0));
        }

        return ids.Count == 1 ? ids[0] : null;
    }

    /// <summary>Queues labelled messages again, e.g. to measure a new prompt. Labels are kept.</summary>
    public int RequeueLabeled(string? split, DateTimeOffset now)
    {
        using var cmd = Command("""
            UPDATE messages SET status = 'queued', attempts = 0, last_error = NULL, updated_utc = $now
            WHERE id IN (SELECT message_id FROM labels WHERE $split IS NULL OR split = $split)
              AND status IN ('assessed', 'invalid', 'failed')
            """);
        cmd.Parameters.AddWithValue("$split", (object?)split ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Queues every processed message again, e.g. after a prompt change. Labels and old assessments are kept.</summary>
    public int RequeueAll(DateTimeOffset now)
    {
        using var cmd = Command("""
            UPDATE messages SET status = 'queued', attempts = 0, last_error = NULL, updated_utc = $now
            WHERE status IN ('assessed', 'invalid', 'failed')
            """);
        cmd.Parameters.AddWithValue("$now", Utc(now));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Per-mailbox counts of messages that could not be assessed.</summary>
    public IReadOnlyDictionary<string, int> ProblemsByMailbox() =>
        CountBy("SELECT mailbox, count(*) FROM messages WHERE status IN ('gone', 'unreadable', 'invalid', 'failed') GROUP BY mailbox");

    public static string ContentHash(MessageForAssessment message) => MessageIdentity.ContentHash(message.Subject, message.Body);

    // --- monitoring ----------------------------------------------------------------------------

    /// <summary>Queued message count and when the longest-waiting one was discovered.</summary>
    public (int Count, DateTimeOffset? OldestDiscovered) QueueInfo()
    {
        using var cmd = Command("SELECT count(*), min(discovered_utc) FROM messages WHERE status = 'queued'");
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.IsDBNull(1) ? null : ParseUtc(r.GetString(1)));
    }

    /// <summary>Newest ReceivedTime seen in the store's Inbox; the next scan starts from it.</summary>
    public DateTimeOffset? GetScanMark(string storeId)
    {
        using var cmd = Command("SELECT newest_received_utc FROM store_state WHERE store_id = $store");
        cmd.Parameters.AddWithValue("$store", storeId);
        return cmd.ExecuteScalar() is string value ? ParseUtc(value) : null;
    }

    public void SetScanMark(string storeId, DateTimeOffset newestReceived, DateTimeOffset now)
    {
        using var cmd = Command("""
            INSERT INTO store_state (store_id, newest_received_utc, last_scan_utc) VALUES ($store, $newest, $now)
            ON CONFLICT (store_id) DO UPDATE SET
                newest_received_utc = max(newest_received_utc, excluded.newest_received_utc), last_scan_utc = excluded.last_scan_utc
            """);
        cmd.Parameters.AddWithValue("$store", storeId);
        cmd.Parameters.AddWithValue("$newest", Utc(newestReceived));
        cmd.Parameters.AddWithValue("$now", Utc(now));
        cmd.ExecuteNonQuery();
    }

    public string? GetAppState(string key)
    {
        using var cmd = Command("SELECT value FROM app_state WHERE key = $key");
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetAppState(string key, string value)
    {
        using var cmd = Command("INSERT INTO app_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value");
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes messages received before <paramref name="cutoff"/> with their assessments.
    /// Labelled messages and messages carrying a category this application added are
    /// kept for measurement and rollback, but their subject, sender, summary and
    /// rationale are removed.
    /// </summary>
    public RetentionResult ApplyRetention(DateTimeOffset cutoff)
    {
        const string keep = """
            (id IN (SELECT message_id FROM labels) OR id IN (SELECT message_id FROM category_state WHERE owned IS NOT NULL))
            """;
        using var tx = _db.BeginTransaction();
        int deleted, redacted;
        using (var cmd = Command($"DELETE FROM messages WHERE received_utc < $cutoff AND NOT {keep}", tx))
        {
            cmd.Parameters.AddWithValue("$cutoff", Utc(cutoff));
            deleted = cmd.ExecuteNonQuery();
        }

        using (var cmd = Command($"""
            UPDATE messages SET subject = NULL, sender_name = NULL, sender_address = NULL
            WHERE received_utc < $cutoff AND (subject IS NOT NULL OR sender_name IS NOT NULL OR sender_address IS NOT NULL) AND {keep}
            """, tx))
        {
            cmd.Parameters.AddWithValue("$cutoff", Utc(cutoff));
            redacted = cmd.ExecuteNonQuery();
        }

        // Done marks are working state, not something to keep: kept messages lose them too.
        using (var cmd = Command("DELETE FROM done WHERE message_id IN (SELECT id FROM messages WHERE received_utc < $cutoff)", tx))
        {
            cmd.Parameters.AddWithValue("$cutoff", Utc(cutoff));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = Command("""
            UPDATE assessments SET summary = '', rationale = ''
            WHERE (summary <> '' OR rationale <> '') AND message_id IN (SELECT id FROM messages WHERE received_utc < $cutoff)
            """, tx))
        {
            cmd.Parameters.AddWithValue("$cutoff", Utc(cutoff));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new RetentionResult(deleted, redacted);
    }

    // --- categories ----------------------------------------------------------------------------

    /// <summary>Assessed messages with their latest valid assessment, label and category state.</summary>
    public IReadOnlyList<CategoryCandidate> GetCategoryCandidates() => ReadCategoryCandidates("""
        WHERE m.status = 'assessed' AND (a.id IS NOT NULL OR l.message_id IS NOT NULL)
        """);

    /// <summary>Messages carrying a category this application added.</summary>
    public IReadOnlyList<CategoryCandidate> GetOwnedCategories() => ReadCategoryCandidates("WHERE c.owned IS NOT NULL");

    private List<CategoryCandidate> ReadCategoryCandidates(string where)
    {
        using var cmd = Command($"""
            SELECT m.id, m.store_id, m.entry_id, m.subject, m.internet_message_id, m.content_hash,
                   a.action, a.review_reasons, l.action, c.desired, c.owned, c.status, c.attempts
            FROM messages m
            LEFT JOIN (SELECT message_id, max(id) AS id FROM assessments GROUP BY message_id) la ON la.message_id = m.id
            LEFT JOIN assessments a ON a.id = la.id AND a.valid = 1
            LEFT JOIN labels l ON l.message_id = m.id
            LEFT JOIN category_state c ON c.message_id = m.id
            {where}
            ORDER BY m.received_utc DESC
            """);
        using var r = cmd.ExecuteReader();
        var result = new List<CategoryCandidate>();
        while (r.Read())
        {
            var assessment = r.IsDBNull(6) ? null : new Assessment(
                "", "", "muu", r.GetString(6), "ei_maaraaikaa", null, false,
                JsonSerializer.Deserialize<string[]>(r.GetString(7)) ?? []);
            var label = r.IsDBNull(8) ? null : new UserLabel(r.GetString(8), "", "", null);
            var state = r.IsDBNull(11) ? null : new CategoryState(
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetInt32(12));
            result.Add(new CategoryCandidate(
                r.GetInt64(0), new MessageReference(r.GetString(1), r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                assessment, label, state));
        }

        return result;
    }

    /// <summary>Records the outcome of a category change and appends it to the history.</summary>
    public void SaveCategoryResult(long messageId, string? desired, string? owned, CategoryWriteResult result, string kind, DateTimeOffset now)
    {
        using var tx = _db.BeginTransaction();
        using (var cmd = Command("""
            INSERT INTO category_state (message_id, desired, owned, status, attempts, error, updated_utc)
            VALUES ($id, $desired, $owned, $status, 1, $error, $now)
            ON CONFLICT (message_id) DO UPDATE SET
                attempts = CASE WHEN category_state.desired IS excluded.desired AND category_state.status = excluded.status
                                THEN category_state.attempts + 1 ELSE 1 END,
                desired = excluded.desired, owned = excluded.owned, status = excluded.status,
                error = excluded.error, updated_utc = excluded.updated_utc
            """, tx))
        {
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$desired", (object?)desired ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$owned", (object?)owned ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", result.Status.ToString());
            cmd.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Utc(now));
            cmd.ExecuteNonQuery();
        }

        using (var cmd = Command("""
            INSERT INTO category_log (message_id, created_utc, kind, desired, status, added, categories_before, categories_after, error)
            VALUES ($id, $now, $kind, $desired, $status, $added, $before, $after, $error)
            """, tx))
        {
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.Parameters.AddWithValue("$now", Utc(now));
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$desired", (object?)desired ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", result.Status.ToString());
            cmd.Parameters.AddWithValue("$added", result.Added ? 1 : 0);
            cmd.Parameters.AddWithValue("$before", (object?)result.Before ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$after", (object?)result.After ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Category state counts and how many categories this application currently owns.</summary>
    public (IReadOnlyDictionary<string, int> ByStatus, int Owned, int LogEntries) CategorySummary()
    {
        using var owned = Command("SELECT count(*) FROM category_state WHERE owned IS NOT NULL");
        using var log = Command("SELECT count(*) FROM category_log");
        return (CountBy("SELECT status, count(*) FROM category_state GROUP BY status"),
            Convert.ToInt32(owned.ExecuteScalar(), CultureInfo.InvariantCulture),
            Convert.ToInt32(log.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    /// <summary>The latest category change of the user-chosen test message (exact subject); for development checks.</summary>
    public (string Kind, string Status, string? Before, string? After, string? Error)? LastCategoryChangeForSubject(string subject)
    {
        using var cmd = Command("""
            SELECT g.kind, g.status, g.categories_before, g.categories_after, g.error
            FROM category_log g JOIN messages m ON m.id = g.message_id
            WHERE m.subject = $subject ORDER BY g.id DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("$subject", subject);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))
            : null;
    }

    /// <summary>Whether the user-chosen test message is in the database and assessed (by exact subject).</summary>
    public (int Found, int Assessed) FindBySubject(string subject)
    {
        using var cmd = Command("SELECT count(*), coalesce(sum(status = 'assessed'), 0) FROM messages WHERE subject = $subject");
        cmd.Parameters.AddWithValue("$subject", subject);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }

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

        if (version < 2)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                CREATE TABLE labels (
                    message_id INTEGER PRIMARY KEY REFERENCES messages (id) ON DELETE CASCADE,
                    action TEXT NOT NULL,
                    urgency TEXT NOT NULL,
                    topic TEXT NOT NULL,
                    deadline TEXT,
                    agrees_with_ai INTEGER NOT NULL,
                    assessment_id INTEGER REFERENCES assessments (id),
                    split TEXT NOT NULL,
                    blind INTEGER NOT NULL,
                    labeled_utc TEXT NOT NULL
                );
                PRAGMA user_version = 2;
                """, tx);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (version < 3)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                CREATE TABLE store_state (
                    store_id TEXT PRIMARY KEY,
                    newest_received_utc TEXT NOT NULL,
                    last_scan_utc TEXT NOT NULL
                );
                CREATE TABLE app_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE category_state (
                    message_id INTEGER PRIMARY KEY REFERENCES messages (id) ON DELETE CASCADE,
                    desired TEXT,
                    owned TEXT,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL,
                    error TEXT,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE category_log (
                    id INTEGER PRIMARY KEY,
                    message_id INTEGER NOT NULL REFERENCES messages (id) ON DELETE CASCADE,
                    created_utc TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    desired TEXT,
                    status TEXT NOT NULL,
                    added INTEGER NOT NULL,
                    categories_before TEXT,
                    categories_after TEXT,
                    error TEXT
                );
                CREATE INDEX ix_category_log_message ON category_log (message_id);
                PRAGMA user_version = 3;
                """, tx);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (version < 4)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                CREATE TABLE done (
                    message_id INTEGER PRIMARY KEY REFERENCES messages (id) ON DELETE CASCADE,
                    done_utc TEXT NOT NULL
                );
                PRAGMA user_version = 4;
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
