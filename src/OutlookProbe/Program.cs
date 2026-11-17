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
        var storeOption = OptionValue(args, "--store") is { } s && int.TryParse(s, out var sn) ? sn : (int?)null;
        var valid = command switch
        {
            "stores" or "selected" => true,
            "read-test" => ParseStoreNumbers(args) is not null,
            "category-test" => args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) &&
                               (OptionValue(args, "--store") is null || storeOption is not null),
            "open" => args.Length == 3 && int.TryParse(args[1], out _),
            _ => false,
        };
        if (!valid)
        {
            Console.WriteLine("Käyttö:");
            Console.WriteLine("  OutlookProbe stores");
            Console.WriteLine("  OutlookProbe selected");
            Console.WriteLine("  OutlookProbe read-test <tietovarastojen numerot> [--count N] [--match \"otsikon osa\"]");
            Console.WriteLine("  OutlookProbe category-test \"tarkka otsikko\" [--store N] [--expect-unread]");
            Console.WriteLine("      ilman --store: Outlookissa valittu viesti; --store N: tietovaraston N Saapuneet");
            Console.WriteLine("  OutlookProbe open <tietovaraston numero> \"tarkka otsikko\"");
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
            return command switch
            {
                "stores" => ListStores(app),
                "selected" => ShowSelected(app),
                "read-test" => ReadTest.Run(app, ParseStoreNumbers(args)!, OptionValue(args, "--count") is { } c ? int.Parse(c) : 10, OptionValue(args, "--match")),
                "category-test" => CategoryTest.Run(app, args[1], storeOption, args.Contains("--expect-unread")),
                _ => OpenTest.Run(app, int.Parse(args[1]), args[2]),
            };
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

    private static List<int>? ParseStoreNumbers(string[] args)
    {
        var numbers = new List<int>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++; // skip the option's value
                continue;
            }

            if (!int.TryParse(args[i], out var n))
            {
                return null;
            }

            numbers.Add(n);
        }

        return numbers.Count > 0 ? numbers : null;
    }

    private static string? OptionValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
        var map = new Dictionary<string, Outlook.Account>(StringComparer.OrdinalIgnoreCase);
        var accounts = session.Accounts;
        for (var i = 1; i <= accounts.Count; i++)
        {
            var account = accounts[i];
            try
            {
                map[account.DeliveryStore.StoreID] = account;
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine($"Tilin {i} toimitusvarastoa ei saatu: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
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
