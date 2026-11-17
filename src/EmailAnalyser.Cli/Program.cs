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
                "watch" => await Watch(args, cts.Token),
                "categories" => await Categories(args, cts.Token),
                "retention" => Retention(),
                "status" => Status(),
                "report" => Report(),
                "eval" => await Eval(args, cts.Token),
                "labels" => EvaluateLabels(args),
                "injection-scan" => await InjectionScan(),
                "requeue" => Requeue(args),
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
              watch [--minutes 30] [--now]            jatkuva seuranta: haku, eräehdot, lykkäys ja luokat (Ctrl+C lopettaa)
              categories status                       Outlook-luokkien tila ja testiviestin löytyminen
              categories mode off|test|on [--subject "AI: testiviesti"]
                                                      luokkien kirjoitus: pois, vain testiviestiin tai kaikkiin M365-viesteihin
              categories sync                         kirjoita luokat nyt valitulla tavalla
              categories rollback                     kytke kirjoitus pois ja poista sovelluksen lisäämät luokat
              retention                               poista säilytysaikaa vanhemmat tiedot nyt
              status                                  jonon tila ja viimeiset erät
              report                                  koosteluvut arvioista (ei sisältöä)
              eval [--models a,b] [--file polku]      mallivertailu keinotekoisilla viesteillä
              labels [--split arviointi|saato|kaikki] arvioiden osuvuus käyttäjän merkintöihin (oletus arviointi)
              requeue --labeled saato|arviointi|kaikki merkityt viestit uudelleen jonoon (esim. uuden kehotteen mittaus)
              requeue --all                           kaikki käsitellyt viestit uudelleen jonoon (kehotteen vaihtuessa)
              injection-scan                          ohjailuheuristiikan osumat oikeissa viesteissä (vain määrät)
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
        var runner = new BatchRunner(store, lmStudio, a1111, outlook, classifier, TimeProvider.System, new NvidiaSmiProbe(), new LmStudioPrivacyCheck(), settings.ToIdentityCheck());
        Console.WriteLine($"Malli {settings.Model}, kehote {classifier.Prompt.Version}");

        var processed = 0;
        while (processed < max && !cancellationToken.IsCancellationRequested)
        {
            var options = new BatchOptions(settings.Model, max - processed, TimeSpan.FromMinutes(settings.BatchMaxMinutes));
            var result = await runner.RunAsync(options, p =>
                Console.WriteLine($"  {p.Index,3}/{p.Total}  {p.Status,-20} {p.Action,-11} {p.Urgency,-15} {p.Duration.TotalSeconds,5:0.0} s"),
                cancellationToken);
            processed += result.Assessed + result.Invalid + result.Unavailable + result.Failed;
            Console.WriteLine($"Erä: {Describe(result.Outcome)}{(result.Reason is null ? "" : $" ({result.Reason})")}; " +
                              $"arvioitu {result.Assessed}, virheellisiä {result.Invalid}, ei luettavissa {result.Unavailable}, " +
                              $"epäonnistui {result.Failed}, jonossa {result.Remaining}");
            if (result.Outcome is BatchOutcome.NothingQueued or BatchOutcome.Deferred ||
                result.Outcome == BatchOutcome.Stopped && result.Reason != BatchStopReasons.MaxDuration ||
                result.Remaining == 0)
            {
                break;
            }
        }

        PrintBatches(store, 3);
        return 0;
    }

    private static async Task<int> Watch(string[] args, CancellationToken cancellationToken)
    {
        var settings = RequireSettings();
        using var store = OpenStore();
        using var outlook = new OutlookMailbox();
        using var lmStudio = new LmStudioClient(settings.LmStudioUrl, TimeSpan.FromMinutes(5));
        using var a1111 = new Automatic1111Probe(settings.Automatic1111Url);
        var classifier = new Classifier(lmStudio, PromptTemplate.LoadEmbedded(), settings.ToClassifierOptions());
        var runner = new BatchRunner(store, lmStudio, a1111, outlook, classifier, TimeProvider.System, new NvidiaSmiProbe(), new LmStudioPrivacyCheck(), settings.ToIdentityCheck());
        // Settings are re-read every round, so "categories mode" takes effect without a restart.
        var monitor = new MonitorService(store, outlook, runner, new CategorySync(store, outlook, TimeProvider.System),
            () => AppSettings.Load(AppPaths.Settings), TimeProvider.System);
        Console.WriteLine($"Malli {settings.Model}, kehote {classifier.Prompt.Version}, luokat {settings.CategoryMode}. Ctrl+C lopettaa.");

        string? last = null;
        monitor.StatusChanged += s =>
        {
            var line = MonitorText.Describe(s);
            var perMessageProgress = s.Activity?.StartsWith("Käsitellään ", StringComparison.Ordinal) == true && s.Activity != "Käsitellään jonoa";
            if (line != last && !perMessageProgress)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            }

            last = line;
        };

        using var limit = Option(args, "--minutes") is { } m
            ? new CancellationTokenSource(TimeSpan.FromMinutes(double.Parse(m, CultureInfo.InvariantCulture)))
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        if (args.Contains("--now"))
        {
            monitor.ProcessNow();
        }

        await monitor.RunAsync(linked.Token);
        PrintBatches(store, 3);
        return 0;
    }

    private static async Task<int> Categories(string[] args, CancellationToken cancellationToken)
    {
        var sub = args.Length > 1 ? args[1] : "status";
        var settings = AppSettings.Load(AppPaths.Settings);
        switch (sub)
        {
            case "mode":
            {
                var mode = (args.Length > 2 ? args[2] : "") switch
                {
                    "off" => CategoryMode.Off,
                    "test" => CategoryMode.Test,
                    "on" => CategoryMode.On,
                    _ => throw new ArgumentException("Anna tila: categories mode off|test|on"),
                };
                settings = settings with { CategoryMode = mode, CategoryTestSubject = Option(args, "--subject") ?? settings.CategoryTestSubject };
                settings.Save(AppPaths.Settings);
                Console.WriteLine(mode switch
                {
                    CategoryMode.Off => "Luokkien kirjoitus pois. Jo kirjoitetut luokat jäävät; poista ne komennolla categories rollback.",
                    CategoryMode.Test => $"Luokat kirjoitetaan vain viestiin, jonka otsikko on täsmälleen \"{settings.CategoryTestSubject}\".",
                    _ => "Luokat kirjoitetaan kaikkiin seurattujen M365-laatikoiden arvioituihin viesteihin.",
                });
                return 0;
            }

            case "sync":
            {
                if (settings.CategoryMode == CategoryMode.Off)
                {
                    Console.Error.WriteLine("Luokkien kirjoitus on pois päältä (categories mode test|on).");
                    return 1;
                }

                using var store = OpenStore();
                using var outlook = new OutlookMailbox();
                var r = await new CategorySync(store, outlook, TimeProvider.System).SyncAsync(settings.CategoryMode, settings.CategoryTestSubject, cancellationToken);
                Console.WriteLine($"Kirjoitettu {r.Written}, jo kunnossa {r.Unchanged}, ohitettu {r.Skipped}, estetty {r.Blocked}, epäonnistui {r.Failed}.");
                PrintCategorySummary(store, settings);
                return 0;
            }

            case "rollback":
            {
                settings = settings with { CategoryMode = CategoryMode.Off };
                settings.Save(AppPaths.Settings);
                using var store = OpenStore();
                using var outlook = new OutlookMailbox();
                var r = await new CategorySync(store, outlook, TimeProvider.System).RollbackAsync(cancellationToken);
                Console.WriteLine($"Kirjoitus kytketty pois. Poistettu {r.Written}, valmiiksi poissa {r.Unchanged}, estetty {r.Blocked}, epäonnistui {r.Failed}.");
                PrintCategorySummary(store, settings);
                return r.Blocked + r.Failed == 0 ? 0 : 4;
            }

            default:
            {
                using var store = OpenStore();
                PrintCategorySummary(store, settings);
                return 0;
            }
        }
    }

    private static void PrintCategorySummary(AnalyserStore store, AppSettings settings)
    {
        var (byStatus, owned, log) = store.CategorySummary();
        Console.WriteLine($"Tila: {settings.CategoryMode}" +
                          (settings.CategoryMode == CategoryMode.Test ? $", testiviesti \"{settings.CategoryTestSubject}\"" : ""));
        var (found, assessed) = store.FindBySubject(settings.CategoryTestSubject);
        Console.WriteLine($"Testiviesti tietokannassa: {found} kpl, joista arvioituja {assessed}.");
        if (store.LastCategoryChangeForSubject(settings.CategoryTestSubject) is { } last)
        {
            // Only the user-chosen test message: its category names are not message content.
            Console.WriteLine($"Testiviestin viimeisin muutos ({last.Kind}): {last.Status}, luokat [{last.Before}] → [{last.After}]" +
                              (last.Error is null ? "" : $", huomio: {last.Error}"));
        }
        PrintCounts("Luokkatilat", byStatus);
        Console.WriteLine($"Sovelluksen lisäämiä luokkia nyt {owned}, historiarivejä {log}.");
    }

    private static int Retention()
    {
        var settings = AppSettings.Load(AppPaths.Settings);
        using var store = OpenStore();
        var r = store.ApplyRetention(DateTimeOffset.UtcNow - TimeSpan.FromDays(settings.RetentionDays));
        Console.WriteLine($"Säilytysaika {settings.RetentionDays} pv: poistettu {r.Deleted} viestiä, " +
                          $"merkityistä tai luokitelluista poistettu tunnistetiedot {r.Redacted}.");
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
        var settings = AppSettings.Load(AppPaths.Settings);
        Console.WriteLine($"Mallipalvelimen tunnistus: {settings.ToIdentityCheck().Problem() ?? "kunnossa (portin omistaa odotettu LM Studio)"}");
        Console.WriteLine($"LM Studion lokiasetukset: {new LmStudioPrivacyCheck().Problem() ?? "kunnossa"}");
        Console.WriteLine($"Outlook-luokat: {settings.CategoryMode}");
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

    private static int EvaluateLabels(string[] args)
    {
        var split = Option(args, "--split") ?? LabelSplit.Holdout;
        using var store = OpenStore();
        var items = store.GetReviewItems();
        var r = LabelEvaluation.Compute(items, split == "kaikki" ? null : split);
        Console.WriteLine($"Osa: {r.Split}. Merkittyjä {r.Labeled}, joista arvioituja {r.Assessed}. " +
                          $"Kaikkiaan merkitty {items.Count(i => i.Label is not null)}/{items.Count}.");
        if (r.Assessed == 0)
        {
            return 0;
        }

        Console.WriteLine($"Huomiota vaativat löytyivät {r.AttentionFound}/{r.AttentionExpected} ({Pct(r.Recall)}), " +
                          $"joista tarkista-tilan kautta {r.AttentionFoundAsReview}. Huomiotta jääneitä {r.MissedMessageIds.Count}.");
        Console.WriteLine($"Turhia hälytyksiä {r.FalseAlarms}/{r.NoAttentionExpected}.");
        Console.WriteLine($"Oikein: toimenpide {r.ActionCorrect}/{r.Assessed}, kiireellisyys {r.UrgencyCorrect}/{r.Assessed}, " +
                          $"aihe {r.TopicCorrect}/{r.Assessed}, määräaika {r.DeadlineCorrect}/{r.Assessed}.");
        Console.WriteLine($"AI täysin samaa mieltä: sokkona merkityistä {r.AgreeBlind}/{r.LabeledBlind}, arvio näkyvillä {r.AgreeSeen}/{r.LabeledSeen}.");
        Console.WriteLine($"Kehoteversiot: {string.Join(", ", r.PromptVersions.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (r.MissedMessageIds.Count > 0)
        {
            Console.WriteLine($"Huomiotta jääneiden viestien tunnisteet (näkymän suodatin \"Huomiotta jääneet\"): {string.Join(", ", r.MissedMessageIds)}");
        }

        return 0;
    }

    /// <summary>
    /// Measures how often the injection heuristic fires on real messages (false
    /// positives force review). Reads bodies into memory only; prints counts.
    /// </summary>
    private static async Task<int> InjectionScan()
    {
        using var store = OpenStore();
        using var outlook = new OutlookMailbox();
        var hits = new Dictionary<string, int>();
        var senderHits = new Dictionary<string, int>();
        var withHeader = new Dictionary<string, int>();
        var totals = new Dictionary<string, int>();
        var signals = new Dictionary<string, int>();
        foreach (var item in store.GetReviewItems())
        {
            var read = await outlook.ReadAsync(item.Reference, item.Mailbox, CancellationToken.None);
            if (read.Message is not { } m)
            {
                continue;
            }

            totals[item.Mailbox] = totals.GetValueOrDefault(item.Mailbox) + 1;
            if (InjectionHeuristics.Detect(m.Subject + "\n" + m.Body))
            {
                hits[item.Mailbox] = hits.GetValueOrDefault(item.Mailbox) + 1;
            }

            var sender = SenderAuthentication.Evaluate(m.SenderName, m.SenderAddress, m.Headers);
            if (sender.Dmarc is not null || sender.Spf is not null)
            {
                withHeader[item.Mailbox] = withHeader.GetValueOrDefault(item.Mailbox) + 1;
            }

            if (sender.Suspicious)
            {
                senderHits[item.Mailbox] = senderHits.GetValueOrDefault(item.Mailbox) + 1;
                foreach (var signal in sender.Signals)
                {
                    signals[signal] = signals.GetValueOrDefault(signal) + 1;
                }
            }
        }

        Console.WriteLine("  laatikko: ohjailuheuristiikka / lähettäjäsignaali / tunnistusotsake löytyi / viestejä");
        foreach (var (mailbox, total) in totals.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {mailbox}: {hits.GetValueOrDefault(mailbox)} / {senderHits.GetValueOrDefault(mailbox)} / {withHeader.GetValueOrDefault(mailbox)} / {total}");
        }

        Console.WriteLine($"Ohjailuheuristiikka osui {hits.Values.Sum()} viestiin, lähettäjäsignaali {senderHits.Values.Sum()} viestiin, " +
                          $"yhteensä {totals.Values.Sum()} viestiä.");
        PrintCounts("Lähettäjäsignaalit", signals);
        return 0;
    }

    private static int Requeue(string[] args)
    {
        if (args.Contains("--all"))
        {
            using var all = OpenStore();
            Console.WriteLine($"Jonoon palautettiin {all.RequeueAll(DateTimeOffset.UtcNow)} viestiä. Merkinnät ja aiemmat arviot säilyvät.");
            return 0;
        }

        var split = Option(args, "--labeled");
        if (split is not (LabelSplit.Tuning or LabelSplit.Holdout or "kaikki"))
        {
            Console.Error.WriteLine("Anna osa: requeue --labeled saato|arviointi|kaikki");
            return 1;
        }

        using var store = OpenStore();
        var count = store.RequeueLabeled(split == "kaikki" ? null : split, DateTimeOffset.UtcNow);
        Console.WriteLine($"Jonoon palautettiin {count} merkittyä viestiä. Merkinnät säilyvät; aja seuraavaksi run.");
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
