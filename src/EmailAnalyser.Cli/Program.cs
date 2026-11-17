using System.Globalization;
using System.Text;
using System.Text.Json;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using EmailAnalyser.Outlook;

namespace EmailAnalyser.Cli;

/// <summary>
/// Phase 2 command line. Output is metadata and aggregates only: no message
/// content, subjects or model summaries of real messages are printed.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
        var command = args.Length > 0 ? args[0] : "help";
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Keskeytetään hallitusti (malli puretaan)…");
        };

        try
        {
            return command switch
            {
                "init" => await Init(args),
                "scan" => await Scan(args),
                "run" => await Run(args, cts.Token),
                "status" => Status(),
                "report" => Report(),
                "eval" => await Eval(args, cts.Token),
                "verify-logs" => VerifyLogs(),
                "db-check" => DbCheck(),
                _ => Help(),
            };
        }
        catch (OutlookUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is ModelServerException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Käyttö: email-analyser <komento>

              init --stores 1 4 6 [--model <avain>]   valitse seurattavat tietovarastot (numerot kuten OutlookProbe stores)
              scan [--per-store 30]                   lisää jonoon kunkin laatikon uusimmat viestit
              run [--max 200]                         käsittele jono erinä (Ctrl+C keskeyttää ja purkaa mallin)
              status                                  jonon tila ja viimeiset erät
              report                                  koosteluvut arvioista (ei sisältöä)
              eval [--models a,b] [--file polku]      mallivertailu keinotekoisilla viesteillä
              verify-logs                             tarkista, ettei LM Studion lokeissa ole viestisisältöä
              db-check                                tarkista, ettei tietokanta aukea ilman avainta
            """);
        return 0;
    }

    // --- commands ------------------------------------------------------------------------------

    private static async Task<int> Init(string[] args)
    {
        var numbers = Values(args, "--stores").Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count == 0)
        {
            Console.Error.WriteLine("Anna tietovarastojen numerot: init --stores 1 4 6");
            return 1;
        }

        using var outlook = new OutlookMailbox();
        var stores = await outlook.ListStoresAsync();
        var selected = new List<MonitoredStore>();
        foreach (var n in numbers)
        {
            var store = stores.FirstOrDefault(s => s.Number == n)
                        ?? throw new ArgumentException($"Tietovarastoa {n} ei ole.");
            selected.Add(new MonitoredStore(store.StoreId, store.DisplayName));
            Console.WriteLine($"  [{n}] {store.DisplayName} ({store.Kind})");
        }

        var settings = AppSettings.Load(AppPaths.Settings) with { Stores = selected };
        if (Option(args, "--model") is { } model)
        {
            settings = settings with { Model = model };
        }

        settings.Save(AppPaths.Settings);
        Console.WriteLine($"Tallennettu {selected.Count} tietovarastoa, malli {settings.Model}.");
        Console.WriteLine($"Asetukset: {AppPaths.Settings}");
        return 0;
    }

    private static async Task<int> Scan(string[] args)
    {
        var settings = RequireSettings();
        var perStore = Option(args, "--per-store") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 30;
        using var store = OpenStore();
        using var outlook = new OutlookMailbox();
        var total = 0;
        foreach (var monitored in settings.Stores)
        {
            var found = await outlook.ListRecentInboxAsync(monitored.StoreId, perStore);
            var added = found.Count(m => store.Enqueue(m, DateTimeOffset.UtcNow));
            total += added;
            Console.WriteLine($"  {monitored.Name}: {found.Count} viestiä, uusia jonoon {added}");
        }

        Console.WriteLine($"Jonoon lisättiin {total} viestiä.");
        return 0;
    }

    private static async Task<int> Run(string[] args, CancellationToken cancellationToken)
    {
        var settings = RequireSettings();
        var max = Option(args, "--max") is { } m ? int.Parse(m, CultureInfo.InvariantCulture) : 200;
        using var store = OpenStore();
        using var outlook = new OutlookMailbox();
        using var lmStudio = new LmStudioClient(settings.LmStudioUrl, TimeSpan.FromMinutes(5));
        using var a1111 = new Automatic1111Probe(settings.Automatic1111Url);
        var classifier = new Classifier(lmStudio, PromptTemplate.LoadEmbedded(), settings.ToClassifierOptions());
        var runner = new BatchRunner(store, lmStudio, a1111, outlook, classifier, TimeProvider.System, new NvidiaSmiProbe());
        Console.WriteLine($"Malli {settings.Model}, kehote {classifier.Prompt.Version}");

        var processed = 0;
        while (processed < max && !cancellationToken.IsCancellationRequested)
        {
            var options = new BatchOptions(settings.Model, max - processed, TimeSpan.FromMinutes(settings.BatchMaxMinutes));
            var result = await runner.RunAsync(options, p =>
                Console.WriteLine($"  {p.Index,3}/{p.Total}  {p.Status,-20} {p.Action,-11} {p.Urgency,-15} {p.Duration.TotalSeconds,5:0.0} s"),
                cancellationToken);
            processed += result.Assessed + result.Invalid + result.Unavailable;
            Console.WriteLine($"Erä: {Describe(result.Outcome)}{(result.Reason is null ? "" : $" ({result.Reason})")}; " +
                              $"arvioitu {result.Assessed}, virheellisiä {result.Invalid}, ei luettavissa {result.Unavailable}, jonossa {result.Remaining}");
            if (result.Outcome is BatchOutcome.NothingQueued or BatchOutcome.Deferred ||
                result.Outcome == BatchOutcome.Stopped && result.Reason != "erän enimmäiskesto täyttyi" ||
                result.Remaining == 0)
            {
                break;
            }
        }

        PrintBatches(store, 3);
        return 0;
    }

    private static int Status()
    {
        using var store = OpenStore();
        Console.WriteLine("Viestit tiloittain:");
        foreach (var (status, count) in store.CountByStatus().OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {status,-12} {count,5}");
        }

        PrintBatches(store, 8);
        return 0;
    }

    private static int Report()
    {
        using var store = OpenStore();
        var r = store.BuildReport();
        Console.WriteLine($"Arvioituja viestejä {r.Messages}: kelvollisia {r.Valid}, virheellisiä {r.Invalid}, uudelleenyritys {r.RetriedOnce}");
        Console.WriteLine($"Tarkistettavia {r.NeedsReview}, määräaika {r.WithDeadline}, katkaistuja {r.Truncated}, ketju lyhennetty {r.HistoryShortened}");
        PrintCounts("Toimenpide", r.ByAction);
        PrintCounts("Kiireellisyys", r.ByUrgency);
        PrintCounts("Aihe", r.ByTopic);
        PrintCounts("Tarkistuksen syyt", r.ReviewReasons);
        PrintCounts("Laatikko", r.ByMailbox);
        Console.WriteLine($"Kesto / viesti: mediaani {Percentile(r.DurationsMs, 0.5) / 1000.0:0.0} s, " +
                          $"90 % {Percentile(r.DurationsMs, 0.9) / 1000.0:0.0} s, enintään {(r.DurationsMs.Count == 0 ? 0 : r.DurationsMs.Max()) / 1000.0:0.0} s");
        Console.WriteLine($"Tokenit / viesti: syöte mediaani {Percentile(r.PromptTokens.Select(x => (long)x).ToList(), 0.5)}, " +
                          $"enintään {(r.PromptTokens.Count == 0 ? 0 : r.PromptTokens.Max())}; vastaus mediaani {Percentile(r.CompletionTokens.Select(x => (long)x).ToList(), 0.5)}");
        return 0;
    }

    private static async Task<int> Eval(string[] args, CancellationToken cancellationToken)
    {
        var settings = AppSettings.Load(AppPaths.Settings);
        var models = Option(args, "--models")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     ?? [settings.Model];
        var file = Option(args, "--file") ?? FindUp(Path.Combine("eval", "synthetic-emails.json"));
        var set = EvalSet.Load(file);
        using var lmStudio = new LmStudioClient(settings.LmStudioUrl, TimeSpan.FromMinutes(5));
        using var a1111 = new Automatic1111Probe(settings.Automatic1111Url);
        var prompt = PromptTemplate.LoadEmbedded();
        var runner = new EvalRunner(lmStudio, a1111, prompt, settings.ToClassifierOptions(), new NvidiaSmiProbe());
        Console.WriteLine($"Testiviestejä {set.Cases.Count}, kehote {prompt.Version}");

        var results = new List<EvalModelResult>();
        foreach (var model in models)
        {
            Console.WriteLine();
            Console.WriteLine($"== {model}");
            var result = await runner.RunAsync(model, set, c =>
            {
                var a = c.Assessment;
                var marks = $"{Mark(c.ActionOk)}{Mark(c.UrgencyOk)}{Mark(c.DeadlineOk)}{Mark(c.TopicOk)}{Mark(c.ReviewOk)}";
                Console.WriteLine($"  {c.Id,-24} {marks}  {a?.Action,-10} {a?.Urgency,-14} {a?.Deadline?.ToString("yyyy-MM-dd") ?? "-",-10} {a?.Topic,-16} {c.Duration.TotalSeconds,5:0.0} s{(c.Valid ? "" : "  VIRHE: " + string.Join("; ", c.Errors))}");
            }, cancellationToken);
            results.Add(result);
            PrintEvalSummary(result);
        }

        var outDir = Path.Combine(FindUp("local-data", createIfMissing: true), "eval");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, $"{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
        File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine("Merkit: toimenpide, kiireellisyys, määräaika, aihe, tarkistettava (+ oikein, x väärin, . ei arvioitu)");
        Console.WriteLine($"Tulokset: {outFile}");
        return 0;
    }

    /// <summary>
    /// Searches LM Studio's data and log folders (models and extensions excluded) for
    /// the prompt delimiter, which every prompt contains, and for subjects of assessed
    /// messages. Prints counts and paths only.
    /// </summary>
    private static int VerifyLogs()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LM Studio"),
        };
        var files = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            .Where(f => !f.Contains(@"\models\", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(@"\extensions\", StringComparison.OrdinalIgnoreCase) &&
                        new FileInfo(f).Length < 100_000_000)
            .ToList();
        using var store = OpenStore();
        var subjects = store.AssessedSubjects().Where(s => s.Trim().Length >= 12).Select(s => s.Trim()).Distinct().ToList();
        int delimiterHits = 0, subjectHits = 0;
        foreach (var file in files)
        {
            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }

            var hit = text.Contains("VIESTI ALKAA", StringComparison.Ordinal);
            var subjectCount = subjects.Count(s => text.Contains(s, StringComparison.Ordinal));
            delimiterHits += hit ? 1 : 0;
            subjectHits += subjectCount;
            if (hit || subjectCount > 0)
            {
                Console.WriteLine($"  osuma: {file}");
            }
        }

        Console.WriteLine($"Tarkistettuja LM Studion tiedostoja {files.Count}, verrattuja otsikoita {subjects.Count}.");
        Console.WriteLine($"Kehotteen erotin löytyi {delimiterHits} tiedostosta, otsikko-osumia {subjectHits}.");
        Console.WriteLine(delimiterHits == 0 && subjectHits == 0 ? "Tulos: lokeissa ei ole viestisisältöä." : "Tulos: LOKEISSA ON VIESTISISÄLTÖÄ.");
        return delimiterHits == 0 && subjectHits == 0 ? 0 : 4;
    }

    private static int DbCheck()
    {
        var path = AppPaths.Database;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Tietokantaa ei vielä ole.");
            return 1;
        }

        var header = new byte[16];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.ReadExactly(header);
        }

        var plainHeader = Encoding.ASCII.GetString(header).StartsWith("SQLite format 3", StringComparison.Ordinal);
        var opens = AnalyserStore.OpensWithoutKey(path);
        Console.WriteLine($"Tiedosto: {path}");
        Console.WriteLine($"Selväkielinen SQLite-otsake: {(plainHeader ? "KYLLÄ" : "ei")}");
        Console.WriteLine($"Aukeaa ilman avainta: {(opens ? "KYLLÄ" : "ei")}");
        return plainHeader || opens ? 4 : 0;
    }

    // --- helpers -------------------------------------------------------------------------------

    private static AnalyserStore OpenStore() =>
        AnalyserStore.Open(AppPaths.Database, KeyStore.GetOrCreateHexKey(AppPaths.DatabaseKey));

    private static AppSettings RequireSettings()
    {
        var settings = AppSettings.Load(AppPaths.Settings);
        if (settings.Stores.Count == 0)
        {
            throw new InvalidOperationException("Seurattavia tietovarastoja ei ole valittu. Aja ensin: email-analyser init --stores …");
        }

        return settings;
    }

    private static void PrintBatches(AnalyserStore store, int count)
    {
        Console.WriteLine("Viimeiset erät:");
        foreach (var b in store.RecentBatches(count))
        {
            var duration = b.Ended is { } end ? $"{(end - b.Started).TotalMinutes:0.0} min" : "-";
            Console.WriteLine($"  #{b.Id,-4} {b.Started.ToLocalTime():dd.MM. HH:mm}  {b.Outcome,-10} {duration,9}  arvioitu {b.Assessed,3}, virheitä {b.Invalid}" +
                              $"  lataus {(b.LoadSeconds is { } l ? $"{l:0} s" : "-")}  GPU {b.GpuBefore?.ToString() ?? "-"}/{b.GpuLoaded?.ToString() ?? "-"}/{b.GpuAfter?.ToString() ?? "-"} MiB" +
                              $"  purettu {(b.Unloaded ? "kyllä" : "EI")}{(b.Reason is null ? "" : $"  ({b.Reason})")}");
        }
    }

    private static void PrintEvalSummary(EvalModelResult r)
    {
        Console.WriteLine($"  Kelvollisia {r.Valid}/{r.Cases.Count}. Lataus {r.LoadSeconds:0} s, GPU ladattuna {r.GpuLoadedMiB?.ToString() ?? "-"} MiB, mediaani {r.MedianDuration.TotalSeconds:0.0} s/viesti");
        Console.WriteLine($"  Toimenpide {Pct(r.ActionAccuracy)}, kiireellisyys {Pct(r.UrgencyAccuracy)}, määräaika {Pct(r.DeadlineAccuracy)}, aihe {Pct(r.TopicAccuracy)}");
        Console.WriteLine($"  Huomiota vaativat löytyivät {r.AttentionFound}/{r.AttentionExpected}, turhia hälytyksiä {r.FalseAlarms}/{r.NoAttentionExpected}");
    }

    private static void PrintCounts(string title, IReadOnlyDictionary<string, int> counts) =>
        Console.WriteLine($"{title}: {(counts.Count == 0 ? "-" : string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")))}");

    private static long Percentile(IReadOnlyList<long> values, double p)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * sorted.Count))];
    }

    private static string Mark(bool? ok) => ok switch { true => "+", false => "x", null => "." };

    private static string Pct(double value) => double.IsNaN(value) ? "-" : $"{value * 100:0} %";

    private static string Describe(BatchOutcome outcome) => outcome switch
    {
        BatchOutcome.NothingQueued => "jono tyhjä",
        BatchOutcome.Deferred => "lykätty",
        BatchOutcome.Completed => "valmis",
        _ => "keskeytyi",
    };

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> Values(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i < 0 ? [] : args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Finds a path relative to the repository root by walking up from the working directory.</summary>
    private static string FindUp(string relative, bool createIfMissing = false)
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate) ||
                createIfMissing && File.Exists(Path.Combine(dir.FullName, "EmailAnalyser.slnx")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Polkua ei löytynyt: {relative}");
    }
}
