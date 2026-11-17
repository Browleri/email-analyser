using System.Runtime.InteropServices;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace OutlookProbe;

/// <summary>
/// Phase 1 Outlook connection probe. Prints folder structure and metadata only;
/// message bodies are never read or printed.
/// </summary>
internal static class Program
{
    private const int MaxFolderDepth = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        if (command is not ("stores" or "selected"))
        {
            Console.WriteLine("Käyttö: OutlookProbe stores | selected");
            return command == "help" ? 0 : 1;
        }

        var app = OutlookConnection.AttachToRunning(out var error);
        if (app is null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        try
        {
            return command == "stores" ? ListStores(app) : ShowSelected(app);
        }
        catch (COMException ex)
        {
            Console.Error.WriteLine($"Outlook-virhe: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            return 3;
        }
        finally
        {
            Marshal.ReleaseComObject(app);
        }
    }

    private static int ListStores(Outlook.Application app)
    {
        var session = app.Session;
        var accountsByStore = MapAccountsToStores(session);
        var stores = session.Stores;
        Console.WriteLine($"Outlook-profiili: {session.CurrentProfileName}, tietovarastoja {stores.Count}");

        for (var i = 1; i <= stores.Count; i++)
        {
            var store = stores[i];
            try
            {
                accountsByStore.TryGetValue(store.StoreID, out var account);
                Console.WriteLine();
                Console.WriteLine($"[{i}] {store.DisplayName}");
                Console.WriteLine($"    Tyyppi: {DescribeStore(store, account)}");

                var inboxPath = TryGetInboxPath(store);
                var root = (Outlook.Folder)store.GetRootFolder();
                try
                {
                    PrintFolders(root, depth: 1, inboxPath);
                }
                finally
                {
                    Marshal.ReleaseComObject(root);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        Console.WriteLine();
        Console.WriteLine("* = tietovaraston oletus-Saapuneet. Lukumäärä: viestejä / lukemattomia.");
        return 0;
    }

    private static void PrintFolders(Outlook.Folder parent, int depth, string? inboxPath)
    {
        var folders = parent.Folders;
        try
        {
            for (var i = 1; i <= folders.Count; i++)
            {
                var folder = (Outlook.Folder)folders[i];
                try
                {
                    if (folder.DefaultItemType != Outlook.OlItemType.olMailItem)
                    {
                        continue;
                    }

                    var marker = folder.FolderPath == inboxPath ? "*" : " ";
                    var subCount = folder.Folders.Count;
                    var hidden = depth >= MaxFolderDepth && subCount > 0 ? $"  (+{subCount} alikansiota)" : "";
                    Console.WriteLine(
                        $"   {marker}{new string(' ', depth * 2)}{folder.Name}  {folder.Items.Count} / {folder.UnReadItemCount}{hidden}");

                    if (depth < MaxFolderDepth && subCount > 0)
                    {
                        PrintFolders(folder, depth + 1, inboxPath);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(folder);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folders);
        }
    }

    private static int ShowSelected(Outlook.Application app)
    {
        var explorer = app.ActiveExplorer();
        if (explorer is null)
        {
            Console.Error.WriteLine("Outlookissa ei ole avointa pääikkunaa.");
            return 1;
        }

        var selection = explorer.Selection;
        if (selection.Count != 1)
        {
            Console.Error.WriteLine($"Valitse Outlookissa täsmälleen yksi viesti (nyt valittuna {selection.Count}).");
            return 1;
        }

        if (selection[1] is not Outlook.MailItem mail)
        {
            Console.Error.WriteLine("Valittu kohde ei ole sähköpostiviesti.");
            return 1;
        }

        var folder = (Outlook.Folder)mail.Parent;
        var store = folder.Store;
        Console.WriteLine($"Tietovarasto:  {store.DisplayName} ({DescribeStore(store, null)})");
        Console.WriteLine($"Kansio:        {folder.FolderPath}");
        Console.WriteLine($"Otsikko:       {mail.Subject}");
        Console.WriteLine($"Lähettäjä:     {mail.SenderName}");
        Console.WriteLine($"Vastaanotettu: {mail.ReceivedTime:yyyy-MM-dd HH:mm}");
        Console.WriteLine($"Lukematon:     {(mail.UnRead ? "kyllä" : "ei")}");
        Console.WriteLine($"Luokat:        {(string.IsNullOrEmpty(mail.Categories) ? "(ei)" : mail.Categories)}");
        Console.WriteLine($"EntryID:       {mail.EntryID[..Math.Min(16, mail.EntryID.Length)]}… (pituus {mail.EntryID.Length})");
        return 0;
    }

    private static Dictionary<string, Outlook.Account> MapAccountsToStores(Outlook.NameSpace session)
    {
        var map = new Dictionary<string, Outlook.Account>();
        foreach (Outlook.Account account in session.Accounts)
        {
            try
            {
                map[account.DeliveryStore.StoreID] = account;
            }
            catch (COMException)
            {
                // Some account types have no delivery store.
            }
        }

        return map;
    }

    private static string DescribeStore(Outlook.Store store, Outlook.Account? account)
    {
        var kind = store.ExchangeStoreType switch
        {
            Outlook.OlExchangeStoreType.olPrimaryExchangeMailbox => "Exchange/M365, ensisijainen postilaatikko",
            Outlook.OlExchangeStoreType.olAdditionalExchangeMailbox => "Exchange/M365, lisäpostilaatikko",
            Outlook.OlExchangeStoreType.olExchangeMailbox => "Exchange/M365, delegoitu postilaatikko",
            Outlook.OlExchangeStoreType.olExchangePublicFolder => "Exchange, julkiset kansiot",
            _ => store.IsDataFileStore ? "datatiedosto (OST/PST)" : "ei-Exchange",
        };

        if (account is not null)
        {
            var accountKind = account.AccountType switch
            {
                Outlook.OlAccountType.olExchange => "Exchange",
                Outlook.OlAccountType.olImap => "IMAP",
                Outlook.OlAccountType.olPop3 => "POP3",
                Outlook.OlAccountType.olEas => "Exchange ActiveSync",
                Outlook.OlAccountType.olHttp => "HTTP",
                _ => "muu",
            };
            kind += $"; tili {accountKind}";
        }

        return kind;
    }

    private static string? TryGetInboxPath(Outlook.Store store)
    {
        try
        {
            var inbox = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
            var path = inbox.FolderPath;
            Marshal.ReleaseComObject(inbox);
            return path;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
