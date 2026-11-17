using System.Diagnostics;
using System.Runtime.InteropServices;
using EmailAnalyser.Core;
using Microsoft.Win32;
using OL = Microsoft.Office.Interop.Outlook;

namespace EmailAnalyser.Outlook;

public sealed record StoreInfo(int Number, string StoreId, string DisplayName, string Kind);

public sealed class OutlookUnavailableException(string message) : MessageSourceUnavailableException(message);

/// <summary>
/// Outlook through its object model in the user's own session. Attaches to a
/// running Outlook only, reads metadata through Folder.GetTable, never marks
/// messages read, and returns plain data (no COM objects) to callers. If Outlook
/// is closed or restarted, the next call attaches again.
/// </summary>
public sealed class OutlookMailbox : IMessageSource, IMailboxScanner, ICategoryTarget, IDisposable
{
    private const string LongTermEntryIdProperty = "http://schemas.microsoft.com/mapi/proptag/0x66700102";
    private const string MessageIdProperty = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    private const string SmtpAddressProperty = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";
    private const string TransportHeadersProperty = "http://schemas.microsoft.com/mapi/proptag/0x007D001F";
    private const int MapiNotFound = unchecked((int)0x8004010F);

    /// <summary>COM errors that mean Outlook went away (closed, restarted or hung).</summary>
    private static readonly HashSet<int> Disconnected =
    [
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x800706BE), // RPC_S_CALL_FAILED
        unchecked((int)0x800401FD), // CO_E_OBJNOTCONNECTED
        unchecked((int)0x80010001), // RPC_E_CALL_REJECTED (busy)
    ];

    private readonly StaDispatcher _sta = new();
    private OL.Application? _app;

    public Task<IReadOnlyList<StoreInfo>> ListStoresAsync() => Invoke<IReadOnlyList<StoreInfo>>(() =>
    {
        var session = App().Session;
        var stores = session.Stores;
        var result = new List<StoreInfo>();
        for (var i = 1; i <= stores.Count; i++)
        {
            var store = stores[i];
            try
            {
                result.Add(new StoreInfo(i, store.StoreID, store.DisplayName, store.ExchangeStoreType switch
                {
                    OL.OlExchangeStoreType.olPrimaryExchangeMailbox => "M365, ensisijainen",
                    OL.OlExchangeStoreType.olAdditionalExchangeMailbox => "M365, lisäpostilaatikko",
                    OL.OlExchangeStoreType.olExchangeMailbox => "M365, jaettu",
                    OL.OlExchangeStoreType.olExchangePublicFolder => "julkiset kansiot",
                    _ => "muu (esim. IMAP)",
                }));
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        return result;
    });

    /// <summary>The newest mail items in the store's Inbox, metadata only.</summary>
    public Task<IReadOnlyList<DiscoveredMessage>> ListRecentInboxAsync(string storeId, int count) =>
        Invoke(() => ListInbox(storeId, DateTimeOffset.MinValue, count));

    public Task<IReadOnlyList<DiscoveredMessage>> ListInboxSinceAsync(string storeId, DateTimeOffset since, int max, CancellationToken cancellationToken) =>
        Invoke(() => ListInbox(storeId, since, max));

    private IReadOnlyList<DiscoveredMessage> ListInbox(string storeId, DateTimeOffset since, int max)
    {
        var session = App().Session;
        var store = session.GetStoreFromID(storeId);
        var inbox = (OL.Folder)store.GetDefaultFolder(OL.OlDefaultFolders.olFolderInbox);
        var table = inbox.GetTable("", OL.OlTableContents.olUserItems);
        try
        {
            var columns = table.Columns;
            columns.RemoveAll();
            columns.Add(LongTermEntryIdProperty); // 1
            columns.Add("ReceivedTime");          // 2
            columns.Add("MessageClass");          // 3
            columns.Add(MessageIdProperty);       // 4
            table.Sort("[ReceivedTime]", Descending: true);

            var mailbox = store.DisplayName;
            var folderPath = inbox.FolderPath;
            var result = new List<DiscoveredMessage>();
            while (result.Count < max && !table.EndOfTable)
            {
                var row = table.GetNextRow();
                try
                {
                    var values = (object[])row.GetValues();
                    if (values[1] is not DateTime receivedLocal)
                    {
                        continue;
                    }

                    var received = new DateTimeOffset(DateTime.SpecifyKind(receivedLocal, DateTimeKind.Local));
                    if (received < since)
                    {
                        break; // sorted newest first
                    }

                    if (values[2] is not string messageClass ||
                        !messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new DiscoveredMessage(
                        new MessageReference(storeId, row.BinaryToString(1)), mailbox, folderPath, values[3] as string, received));
                }
                finally
                {
                    Marshal.ReleaseComObject(row);
                }
            }

            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(table);
            Marshal.ReleaseComObject(inbox);
            Marshal.ReleaseComObject(store);
        }
    }

    public Task<MessageReadResult> ReadAsync(MessageReference reference, string mailbox, CancellationToken cancellationToken) =>
        Invoke(() =>
        {
            if (GetItem(reference) is not { } item)
            {
                return MessageReadResult.Gone();
            }

            try
            {
                if (item is not OL.MailItem mail)
                {
                    return MessageReadResult.Unreadable("ei sähköpostiviesti");
                }

                var body = mail.Body;
                if (string.IsNullOrWhiteSpace(body))
                {
                    return MessageReadResult.Unreadable("tyhjä tai salattu sisältö");
                }

                var received = new DateTimeOffset(DateTime.SpecifyKind(mail.ReceivedTime, DateTimeKind.Local));
                return MessageReadResult.Found(new MessageForAssessment(
                    mailbox, mail.SenderName ?? "", SenderAddress(mail), mail.To ?? "", mail.CC ?? "",
                    received, mail.Subject ?? "", body, RoleOf(mail, mailbox), StringProperty(mail, TransportHeadersProperty)));
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        });

    /// <summary>
    /// Changes only this application's own categories on an Exchange message, after
    /// checking that it is still the message that was assessed. The current categories
    /// are re-read just before saving, and the read state is verified afterwards.
    /// </summary>
    public Task<CategoryWriteResult> ChangeAsync(MessageReference reference, CategoryChange change, CancellationToken cancellationToken) =>
        Invoke(() =>
        {
            if (GetItem(reference) is not { } item)
            {
                return CategoryWriteResult.Of(CategoryWriteStatus.Gone, "viestiä ei löytynyt");
            }

            try
            {
                if (item is not OL.MailItem mail)
                {
                    return CategoryWriteResult.Of(CategoryWriteStatus.Mismatch, "ei sähköpostiviesti");
                }

                if (!IsExchangeMailbox(mail))
                {
                    // IMAP (Gmail) categories stay local to Outlook and do not sync; shown in the app only.
                    return CategoryWriteResult.Of(CategoryWriteStatus.NotSupported, "luokat vain M365-laatikoihin");
                }

                var messageId = StringProperty(mail, MessageIdProperty);
                if (change.ExpectedMessageId is not null && !string.Equals(messageId, change.ExpectedMessageId, StringComparison.Ordinal))
                {
                    return CategoryWriteResult.Of(CategoryWriteStatus.Mismatch, "Message-ID ei täsmää");
                }

                if (!string.Equals(MessageIdentity.ContentHash(mail.Subject, mail.Body), change.ExpectedContentHash, StringComparison.Ordinal))
                {
                    return CategoryWriteResult.Of(CategoryWriteStatus.Mismatch, "sisältö on muuttunut arvioinnin jälkeen");
                }

                var separator = ListSeparator();
                var before = mail.Categories;
                var unreadBefore = mail.UnRead;
                var (after, changed, added) = AiCategories.Apply(before, separator, change.Add, change.Remove);
                if (!changed)
                {
                    return new CategoryWriteResult(CategoryWriteStatus.Unchanged, false, before, before, null);
                }

                try
                {
                    mail.Categories = after;
                    mail.Save();
                }
                catch (COMException ex) when (!Disconnected.Contains(ex.HResult))
                {
                    return CategoryWriteResult.Of(CategoryWriteStatus.Failed, $"tallennus epäonnistui (0x{ex.HResult:X8}), esim. vain luku -kansio");
                }

                string? warning = null;
                if (unreadBefore && !mail.UnRead)
                {
                    // Never observed in phase 1, but the read state must not change.
                    mail.UnRead = true;
                    mail.Save();
                    warning = "lukutila muuttui ja palautettiin";
                }

                return new CategoryWriteResult(CategoryWriteStatus.Written, added, before, mail.Categories, warning);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        });

    /// <summary>
    /// Opens the message in an Outlook window. Outlook marks it read, as when the
    /// user opens it. Returns false if the message no longer exists.
    /// </summary>
    public Task<bool> DisplayAsync(MessageReference reference) =>
        Invoke(() =>
        {
            if (GetItem(reference) is not { } item)
            {
                return false;
            }

            try
            {
                if (item is OL.MailItem mail)
                {
                    mail.Display(false);
                    return true;
                }

                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        });

    public void Dispose()
    {
        if (_app is not null)
        {
            var app = _app;
            _app = null;
            _sta.InvokeAsync(() => Marshal.ReleaseComObject(app)).Wait(TimeSpan.FromSeconds(10));
        }

        _sta.Dispose();
    }

    /// <summary>
    /// Runs on the STA thread. A lost connection releases the stale Outlook object so
    /// the next call attaches to the running Outlook again (never starts it).
    /// </summary>
    private Task<T> Invoke<T>(Func<T> action) => _sta.InvokeAsync(() =>
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is InvalidComObjectException || ex is COMException com && Disconnected.Contains(com.HResult))
        {
            Reset();
            throw new OutlookUnavailableException("Yhteys Outlookiin katkesi (Outlook suljettiin, käynnistyi uudelleen tai ei vastaa).");
        }
    });

    private void Reset()
    {
        if (_app is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_app);
            }
            catch (InvalidComObjectException)
            {
            }

            _app = null;
        }
    }

    private object? GetItem(MessageReference reference)
    {
        try
        {
            return App().Session.GetItemFromID(reference.EntryId, reference.StoreId);
        }
        catch (COMException ex) when (ex.HResult == MapiNotFound)
        {
            return null;
        }
    }

    private static bool IsExchangeMailbox(OL.MailItem mail)
    {
        var folder = (OL.Folder)mail.Parent;
        try
        {
            var store = folder.Store;
            try
            {
                return store.ExchangeStoreType is OL.OlExchangeStoreType.olPrimaryExchangeMailbox
                    or OL.OlExchangeStoreType.olAdditionalExchangeMailbox;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    /// <summary>Outlook separates categories with the Windows list separator of the user's regional settings.</summary>
    private static string ListSeparator() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International", "sList", null) is string s && s.Length > 0 ? s : ",";

    private static string StringProperty(OL.MailItem mail, string property)
    {
        try
        {
            return mail.PropertyAccessor.GetProperty(property) as string ?? "";
        }
        catch (COMException ex) when (!Disconnected.Contains(ex.HResult))
        {
            return ""; // property not present
        }
    }

    /// <summary>
    /// Whether the mailbox (its display name is the SMTP address for these stores)
    /// is in To, in CC, or not named. Only the first 200 recipients are checked.
    /// </summary>
    private static RecipientRole RoleOf(OL.MailItem mail, string mailbox)
    {
        if (!mailbox.Contains('@'))
        {
            return RecipientRole.Unknown;
        }

        var recipients = mail.Recipients;
        try
        {
            var inCopy = false;
            var count = Math.Min(recipients.Count, 200);
            for (var i = 1; i <= count; i++)
            {
                var recipient = recipients[i];
                try
                {
                    if (!string.Equals(RecipientSmtp(recipient), mailbox, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (recipient.Type == (int)OL.OlMailRecipientType.olTo)
                    {
                        return RecipientRole.Direct;
                    }

                    inCopy |= recipient.Type == (int)OL.OlMailRecipientType.olCC;
                }
                finally
                {
                    Marshal.ReleaseComObject(recipient);
                }
            }

            return inCopy ? RecipientRole.Copy : RecipientRole.NotNamed;
        }
        catch (COMException ex) when (!Disconnected.Contains(ex.HResult))
        {
            return RecipientRole.Unknown;
        }
        finally
        {
            Marshal.ReleaseComObject(recipients);
        }
    }

    private static string RecipientSmtp(OL.Recipient recipient)
    {
        try
        {
            return recipient.PropertyAccessor.GetProperty(SmtpAddressProperty) as string ?? recipient.Address ?? "";
        }
        catch (COMException ex) when (!Disconnected.Contains(ex.HResult))
        {
            return recipient.Address ?? "";
        }
    }

    private static string SenderAddress(OL.MailItem mail)
    {
        if (!string.Equals(mail.SenderEmailType, "EX", StringComparison.OrdinalIgnoreCase))
        {
            return mail.SenderEmailAddress ?? "";
        }

        // Exchange senders have an X.500 address; prefer their SMTP address.
        try
        {
            var sender = mail.Sender;
            var user = sender?.GetExchangeUser();
            return user?.PrimarySmtpAddress ?? mail.SenderEmailAddress ?? "";
        }
        catch (COMException ex) when (!Disconnected.Contains(ex.HResult))
        {
            return mail.SenderEmailAddress ?? "";
        }
    }

    private OL.Application App()
    {
        if (_app is not null)
        {
            return _app;
        }

        if (Process.GetProcessesByName("OUTLOOK").Length == 0)
        {
            throw new OutlookUnavailableException("Outlook ei ole käynnissä. Ohjelma ei käynnistä Outlookia itse.");
        }

        if (CLSIDFromProgID("Outlook.Application", out var clsid) < 0 ||
            GetActiveObject(ref clsid, IntPtr.Zero, out var instance) < 0 || instance is null)
        {
            throw new OutlookUnavailableException(
                "Käynnissä olevaan Outlookiin ei saatu yhteyttä. Outlook voi olla vielä käynnistymässä, tai ohjelma ja Outlook ajetaan eri oikeustasoilla.");
        }

        _app = (OL.Application)instance;
        return _app;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
}
