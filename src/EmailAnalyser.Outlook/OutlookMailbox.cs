using System.Diagnostics;
using System.Runtime.InteropServices;
using EmailAnalyser.Core;
using OL = Microsoft.Office.Interop.Outlook;

namespace EmailAnalyser.Outlook;

public sealed record StoreInfo(int Number, string StoreId, string DisplayName, string Kind);

public sealed class OutlookUnavailableException(string message) : Exception(message);

/// <summary>
/// Outlook through its object model in the user's own session. Attaches to a
/// running Outlook only, reads metadata through Folder.GetTable, never marks
/// messages read, and returns plain data (no COM objects) to callers.
/// </summary>
public sealed class OutlookMailbox : IMessageSource, IDisposable
{
    private const string LongTermEntryIdProperty = "http://schemas.microsoft.com/mapi/proptag/0x66700102";
    private const string MessageIdProperty = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";
    private const int MapiNotFound = unchecked((int)0x8004010F);

    private readonly StaDispatcher _sta = new();
    private OL.Application? _app;

    public Task<IReadOnlyList<StoreInfo>> ListStoresAsync() => _sta.InvokeAsync<IReadOnlyList<StoreInfo>>(() =>
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
        _sta.InvokeAsync<IReadOnlyList<DiscoveredMessage>>(() =>
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
                while (result.Count < count && !table.EndOfTable)
                {
                    var row = table.GetNextRow();
                    try
                    {
                        var values = (object[])row.GetValues();
                        if (values[2] is not string messageClass ||
                            !messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var entryId = row.BinaryToString(1);
                        var received = new DateTimeOffset(DateTime.SpecifyKind((DateTime)values[1], DateTimeKind.Local));
                        result.Add(new DiscoveredMessage(
                            new MessageReference(storeId, entryId), mailbox, folderPath, values[3] as string, received));
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
        });

    public Task<MessageReadResult> ReadAsync(MessageReference reference, string mailbox, CancellationToken cancellationToken) =>
        _sta.InvokeAsync(() =>
        {
            object item;
            try
            {
                item = App().Session.GetItemFromID(reference.EntryId, reference.StoreId);
            }
            catch (COMException ex) when (ex.HResult == MapiNotFound)
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
                    received, mail.Subject ?? "", body));
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
        catch (COMException)
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
