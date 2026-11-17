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
    string Body);

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
