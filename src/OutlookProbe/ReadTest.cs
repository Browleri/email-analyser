using System.Diagnostics;
using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace OutlookProbe;

/// <summary>
/// Reads the newest mail items of selected stores through Folder.GetTable and
/// checks that reading guarded properties (Body, SenderEmailAddress) works
/// without prompts and does not change the read state. Prints metadata only:
/// no subjects, senders or body text.
/// </summary>
internal static class ReadTest
{
    private const string MessageIdProperty = "http://schemas.microsoft.com/mapi/proptag/0x1035001F";

    public static int Run(Outlook.Application app, IReadOnlyList<int> storeNumbers, int count, string? match)
    {
        var session = app.Session;
        var stores = session.Stores;
        var failures = 0;

        foreach (var number in storeNumbers)
        {
            if (number < 1 || number > stores.Count)
            {
                Console.Error.WriteLine($"Tietovarastoa {number} ei ole.");
                failures++;
                continue;
            }

            var store = stores[number];
            try
            {
                failures += TestStore(session, store, number, count, match);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "Tulos: kaikki luettiin, eikä lukutila muuttunut."
            : $"Tulos: {failures} poikkeamaa, ks. yllä.");
        return failures == 0 ? 0 : 4;
    }

    private static int TestStore(Outlook.NameSpace session, Outlook.Store store, int number, int count, string? match)
    {
        Console.WriteLine();
        Console.WriteLine($"[{number}] {store.DisplayName}");
        var failures = 0;
        var inbox = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
        var table = inbox.GetTable("", Outlook.OlTableContents.olUserItems);
        try
        {
            var columns = table.Columns;
            columns.RemoveAll();
            foreach (var name in new[] { "EntryID", "ReceivedTime", "UnRead", "MessageClass", "Subject", MessageIdProperty })
            {
                columns.Add(name);
            }

            table.Sort("[ReceivedTime]", Descending: true);
            Console.WriteLine("    Vastaanotettu     Lukematon ennen/jälkeen  Teksti (merkkiä)  Lähettäjäosoite  Aika      Message-ID");

            var read = 0;
            while (read < count && !table.EndOfTable)
            {
                var row = table.GetNextRow();
                var values = (object[])row.GetValues();
                Marshal.ReleaseComObject(row);

                var messageClass = values[3] as string ?? "";
                if (!messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                read++;
                var entryId = (string)values[0];
                var received = (DateTime)values[1];
                var unreadBefore = (bool)values[2];
                var isMatch = match is not null && values[4] is string subject &&
                              subject.Contains(match, StringComparison.OrdinalIgnoreCase);
                var hasMessageId = values[5] is string id && id.Length > 0;

                var line = TestItem(session, store.StoreID, entryId, unreadBefore, out var ok);
                if (!ok)
                {
                    failures++;
                }

                Console.WriteLine($"    {received:yyyy-MM-dd HH:mm}  {line}  {(hasMessageId ? "on" : "puuttuu"),-10}{(isMatch ? "  <- haettu viesti" : "")}");
            }

            if (read == 0)
            {
                Console.WriteLine("    (ei sähköposteja)");
            }
        }
        catch (COMException ex)
        {
            Console.WriteLine($"    Virhe: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            failures++;
        }
        finally
        {
            Marshal.ReleaseComObject(table);
            Marshal.ReleaseComObject(inbox);
        }

        return failures;
    }

    private static string TestItem(Outlook.NameSpace session, string storeId, string entryId, bool unreadBefore, out bool ok)
    {
        var watch = Stopwatch.StartNew();
        Outlook.MailItem? item = null;
        try
        {
            item = (Outlook.MailItem)session.GetItemFromID(entryId, storeId);
            var bodyLength = item.Body?.Length ?? 0;
            var senderAddressRead = !string.IsNullOrEmpty(item.SenderEmailAddress);
            Marshal.ReleaseComObject(item);
            item = null;

            // Fetch again so the read state comes from Outlook, not from the earlier object.
            item = (Outlook.MailItem)session.GetItemFromID(entryId, storeId);
            var unreadAfter = item.UnRead;
            ok = unreadBefore == unreadAfter;

            var state = $"{Flag(unreadBefore)}/{Flag(unreadAfter)}{(ok ? "" : " MUUTTUI")}";
            return $"{state,-24} {bodyLength,7}  {(senderAddressRead ? "luettu" : "tyhjä"),-15}  {watch.ElapsedMilliseconds,5} ms";
        }
        catch (COMException ex)
        {
            ok = false;
            return $"VIRHE: {ex.Message} (HRESULT 0x{ex.HResult:X8})";
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private static string Flag(bool unread) => unread ? "kyllä" : "ei";
}
