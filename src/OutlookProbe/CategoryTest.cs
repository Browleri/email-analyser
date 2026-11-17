using System.Globalization;
using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace OutlookProbe;

/// <summary>
/// Adds and removes one AI category on a test message identified by its exact
/// subject, either the message selected in Outlook or, with a store number, the
/// single Inbox message with that subject. Verifies that other categories and
/// the read state are preserved.
/// </summary>
internal static class CategoryTest
{
    private const string TestCategory = "AI: Tarkista";

    public static int Run(Outlook.Application app, string expectedSubject, int? storeNumber, bool expectUnread)
    {
        var session = app.Session;
        var reference = storeNumber is { } n
            ? FindInStore(session, n, expectedSubject)
            : FindSelected(app, expectedSubject);
        if (reference is null)
        {
            return 1;
        }

        var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        var (originalCategories, originalUnread) = ReadState(session, reference);
        Console.WriteLine($"Listaerotin:   \"{separator}\"");
        Console.WriteLine($"Alussa:        luokat [{originalCategories}], lukematon {Flag(originalUnread)}");

        if (expectUnread && !originalUnread)
        {
            Console.Error.WriteLine("Viesti ei ole lukematon, vaikka niin vaadittiin. Mitään ei muutettu.");
            return 1;
        }

        if (Split(originalCategories, separator).Contains(TestCategory))
        {
            Console.Error.WriteLine($"Viestissä on jo luokka {TestCategory}. Mitään ei muutettu.");
            return 1;
        }

        // Add: the current categories are re-read just before saving.
        ChangeCategories(session, reference, separator, add: true);
        var (afterAdd, unreadAfterAdd) = ReadState(session, reference);
        Console.WriteLine($"Lisäyksen jälkeen: luokat [{afterAdd}], lukematon {Flag(unreadAfterAdd)}");

        // Remove only the AI category.
        ChangeCategories(session, reference, separator, add: false);
        var (afterRemove, unreadAfterRemove) = ReadState(session, reference);
        Console.WriteLine($"Poiston jälkeen:   luokat [{afterRemove}], lukematon {Flag(unreadAfterRemove)}");

        var original = Split(originalCategories, separator);
        var addedOk = Split(afterAdd, separator).SetEquals(original.Append(TestCategory));
        var restoredOk = Split(afterRemove, separator).SetEquals(original);
        var unreadOk = originalUnread == unreadAfterAdd && originalUnread == unreadAfterRemove;

        Console.WriteLine();
        Console.WriteLine($"Luokka lisättiin muut säilyttäen: {Verdict(addedOk)}");
        Console.WriteLine($"Alkuperäiset luokat palautuivat:  {Verdict(restoredOk)}");
        Console.WriteLine($"Lukutila säilyi:                  {Verdict(unreadOk)}");
        return addedOk && restoredOk && unreadOk ? 0 : 4;
    }

    private static MessageReference? FindSelected(Outlook.Application app, string expectedSubject)
    {
        var selection = app.ActiveExplorer()?.Selection;
        if (selection is null || selection.Count != 1 || selection[1] is not Outlook.MailItem mail)
        {
            Console.Error.WriteLine("Valitse Outlookissa täsmälleen yksi sähköpostiviesti.");
            return null;
        }

        try
        {
            if (!string.Equals(mail.Subject, expectedSubject, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Valitun viestin otsikko ei vastaa annettua otsikkoa. Mitään ei muutettu.");
                return null;
            }

            return new MessageReference(((Outlook.Folder)mail.Parent).StoreID, mail.EntryID);
        }
        finally
        {
            Marshal.ReleaseComObject(mail);
        }
    }

    private static MessageReference? FindInStore(Outlook.NameSpace session, int storeNumber, string subject)
    {
        var store = MessageLocator.GetStore(session, storeNumber, out var error);
        if (store is null)
        {
            Console.Error.WriteLine(error);
            return null;
        }

        try
        {
            var reference = MessageLocator.FindInInbox(store, subject, out error);
            if (reference is null)
            {
                Console.Error.WriteLine($"{error} Mitään ei muutettu.");
            }

            return reference;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void ChangeCategories(Outlook.NameSpace session, MessageReference reference, string separator, bool add)
    {
        var item = (Outlook.MailItem)session.GetItemFromID(reference.EntryId, reference.StoreId);
        try
        {
            var current = Split(item.Categories, separator).ToList();
            if (add)
            {
                current.Add(TestCategory);
            }
            else
            {
                current.RemoveAll(c => string.Equals(c, TestCategory, StringComparison.OrdinalIgnoreCase));
            }

            item.Categories = current.Count == 0 ? null : string.Join(separator + " ", current);
            item.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static (string Categories, bool Unread) ReadState(Outlook.NameSpace session, MessageReference reference)
    {
        var item = (Outlook.MailItem)session.GetItemFromID(reference.EntryId, reference.StoreId);
        try
        {
            return (item.Categories ?? "", item.UnRead);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static HashSet<string> Split(string? categories, string separator) =>
        new((categories ?? "")
                .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static string Flag(bool unread) => unread ? "kyllä" : "ei";

    private static string Verdict(bool ok) => ok ? "kyllä" : "EI";
}
