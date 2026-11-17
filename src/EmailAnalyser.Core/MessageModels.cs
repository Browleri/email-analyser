namespace EmailAnalyser.Core;

/// <summary>Enough to reopen a message: StoreID and the long-term EntryID.</summary>
public sealed record MessageReference(string StoreId, string EntryId);

/// <summary>A message found in a monitored folder. Metadata only; no content.</summary>
public sealed record DiscoveredMessage(
    MessageReference Reference,
    string Mailbox,
    string FolderPath,
    string? InternetMessageId,
    DateTimeOffset Received);

/// <summary>The fields the model sees. Held in memory only; the body is never stored.</summary>
public sealed record MessageForAssessment(
    string Mailbox,
    string SenderName,
    string SenderAddress,
    string To,
    string Cc,
    DateTimeOffset Received,
    string Subject,
    string Body,
    RecipientRole Role = RecipientRole.Unknown,
    string Headers = "");

/// <summary>Identity checks before writing to a message: the content must still be what was assessed.</summary>
public static class MessageIdentity
{
    public static string ContentHash(string? subject, string? body) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes((subject ?? "") + "\n" + (body ?? ""))))[..32];
}

/// <summary>The message source (Outlook) is not reachable; the message is not at fault.</summary>
public class MessageSourceUnavailableException(string message) : Exception(message);

/// <summary>Whether the mailbox owner is a direct recipient, only in copy, or not named.</summary>
public enum RecipientRole
{
    Unknown,
    Direct,
    Copy,
    NotNamed,
}

public enum ReadStatus
{
    Found,
    Gone,
    Unreadable,
}

public sealed record MessageReadResult(ReadStatus Status, MessageForAssessment? Message, string? Reason)
{
    public static MessageReadResult Found(MessageForAssessment message) => new(ReadStatus.Found, message, null);
    public static MessageReadResult Gone() => new(ReadStatus.Gone, null, "viestiä ei löytynyt");
    public static MessageReadResult Unreadable(string reason) => new(ReadStatus.Unreadable, null, reason);
}

public interface IMessageSource
{
    Task<MessageReadResult> ReadAsync(MessageReference reference, string mailbox, CancellationToken cancellationToken);
}

/// <summary>Finds new messages in a monitored store's Inbox. Metadata only.</summary>
public interface IMailboxScanner
{
    /// <summary>Inbox mail received at or after <paramref name="since"/>, newest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<DiscoveredMessage>> ListInboxSinceAsync(string storeId, DateTimeOffset since, int max, CancellationToken cancellationToken);
}
