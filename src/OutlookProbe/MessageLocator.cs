using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace OutlookProbe;

/// <summary>A reference that is enough to reopen a message: StoreID + EntryID.</summary>
internal sealed record MessageReference(string StoreId, string EntryId);

internal static class MessageLocator
{
    private const string SubjectProperty = "urn:schemas:httpmail:subject";

    /// <summary>
    /// Finds the single mail item in the store's default Inbox whose subject equals
    /// <paramref name="subject"/> exactly. Returns null and an error when there is
    /// no match or more than one.
    /// </summary>
    public static MessageReference? FindInInbox(Outlook.Store store, string subject, out string? error)
    {
        error = null;
        var inbox = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
        var filter = $"@SQL=\"{SubjectProperty}\" = '{subject.Replace("'", "''")}'";
        var table = inbox.GetTable(filter, Outlook.OlTableContents.olUserItems);
        try
        {
            var columns = table.Columns;
            columns.RemoveAll();
            columns.Add("EntryID");
            columns.Add("MessageClass");

            var matches = new List<string>();
            while (!table.EndOfTable)
            {
                var row = table.GetNextRow();
                var values = (object[])row.GetValues();
                Marshal.ReleaseComObject(row);
                if (values[1] is string messageClass &&
                    messageClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((string)values[0]);
                }
            }

            if (matches.Count != 1)
            {
                error = $"Otsikolla löytyi {matches.Count} viestiä, kun pitäisi löytyä täsmälleen yksi.";
                return null;
            }

            return new MessageReference(store.StoreID, matches[0]);
        }
        finally
        {
            Marshal.ReleaseComObject(table);
            Marshal.ReleaseComObject(inbox);
        }
    }

    public static Outlook.Store? GetStore(Outlook.NameSpace session, int number, out string? error)
    {
        error = null;
        var stores = session.Stores;
        if (number < 1 || number > stores.Count)
        {
            error = $"Tietovarastoa {number} ei ole.";
            return null;
        }

        return stores[number];
    }
}
