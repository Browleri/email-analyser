using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using EmailAnalyser.Outlook;

namespace EmailAnalyser.App;

/// <param name="mask">When set, message text is replaced with placeholders (masked mode).</param>
public sealed class ItemViewModel(ReviewItem item, Func<bool> blindMode, DisplayMask? mask = null)
{
    public ReviewItem Item { get; } = item;

    public long Id => Item.MessageId;
    public string Mailbox => mask?.Mailbox(Item.Mailbox) ?? SafeText.Clean(Item.Mailbox, keepNewlines: false);
    // Retention keeps 90 days, so the year adds nothing in the list.
    public string Received => Item.Received.ToLocalTime().ToString("d.M. HH.mm");
    public string ReceivedLong => Item.Received.ToLocalTime().ToString("d.M.yyyy 'klo' HH.mm");
    public DateTimeOffset ReceivedSort => Item.Received;
    public string Sender => mask?.SenderName(Item) ?? SafeText.Clean(Item.SenderName, keepNewlines: false);
    public string SenderAddress => mask?.SenderAddress(Item) ?? SafeText.Clean(Item.SenderAddress, keepNewlines: false);
    public string SenderAddressText => SenderAddress.Length == 0 ? "" : $"<{SenderAddress}>";
    public string Subject => Item.Subject is not null
        ? mask is null ? SafeText.Clean(Item.Subject, keepNewlines: false) : DisplayMask.Text(Id * 3, Item.Subject.Length, 80)
        : Item.Assessment is not null || Item.Label is not null ? "(tunnistetiedot poistettu säilytysajan päätyttyä)" : "(ei vielä luettu)";
    public string Status => Display.Text(Item.Status);
    public bool IsLabeled => Item.Label is not null;
    public string LabeledMark => IsLabeled ? "✓" : "";

    /// <summary>In blind mode the AI's answer is hidden until the user has labelled the message.</summary>
    public bool ShowAi => Triage.AiVisible(Item, blindMode());
    public bool AiHidden => Item.Assessment is not null && !ShowAi;

    /// <summary>The list follows the user's label first, then the AI (as Outlook categories do).</summary>
    private EffectiveClassification Effective => Triage.Effective(Item, blindMode());

    public bool IsDone => Triage.IsDone(Item);
    public string DoneInfo => Item.DoneAt is { } at ? $"Hoidettu {at.ToLocalTime():d.M.yyyy 'klo' HH.mm}. Merkintä ja arvio säilyvät." : "";

    public string AiAction => ShowAi ? Display.Text(Item.Assessment!.Action) : Hidden;
    public string AiUrgency => ShowAi ? Display.Text(Item.Assessment!.Urgency) : Hidden;
    public string AiTopic => ShowAi ? Display.Text(Item.Assessment!.Topic) : Hidden;
    public string AiDeadline => Item.Assessment is null ? "" : ShowAi ? Item.Assessment.Deadline?.ToString("d.M.yyyy") ?? "" : Hidden;
    public string DeadlineText => AiDeadline.Length == 0 ? "ei määräaikaa" : AiDeadline;
    public string Summary => !ShowAi ? "" :
        mask is null ? SafeText.ModelText(Item.Assessment!.Summary) : DisplayMask.Text(Id * 3 + 1, Item.Assessment!.Summary.Length, 320);
    public string Rationale => !ShowAi ? "" :
        mask is null ? SafeText.ModelText(Item.Assessment!.Rationale) : DisplayMask.Text(Id * 3 + 2, Item.Assessment!.Rationale.Length, 320);
    public string Review => !ShowAi || !Item.Assessment!.NeedsReview
        ? ""
        : "Tarkistettava: " + string.Join(", ", Item.Assessment.ReviewReasons.Select(Display.Text));
    public bool InjectionSuspected => Triage.InjectionSuspected(Item);
    public bool SenderSuspected => Triage.SenderSuspected(Item);

    /// <summary>
    /// The program's own checks, not the model's opinion: shown even in blind mode, because
    /// a phishing warning matters more than an unbiased label.
    /// </summary>
    public bool HasWarning => Triage.HasWarning(Item);
    public string WarningMark => HasWarning ? "⚠" : "";
    public string? WarningTip =>
        SenderSuspected ? "Lähettäjän tunnistus ei täsmää: viesti voi olla huijaus" :
        InjectionSuspected ? "Viestissä on tekoälylle osoitettua tekstiä" : null;

    /// <summary>Row highlight: a failed sender check colours the row; a done message is dimmed.</summary>
    public string Emphasis => SenderSuspected ? "danger" : IsDone ? "done" : "";

    /// <summary>The action for the coloured tag in the list: the user's label, otherwise the AI's.</summary>
    public string ActionKey => Effective.Action ?? "";
    public string ActionText => Display.Text(ActionKey);
    public string ActionSource => Effective.Source switch
    {
        ClassificationSource.Label => "Oma merkintä",
        ClassificationSource.Ai => "AI-arvio",
        _ => "",
    };

    /// <summary>The urgency next to the tag; a stale AI answer (message queued again) shows the state instead.</summary>
    public string UrgencyKey => Effective.Source == ClassificationSource.Label || Item.Status == "assessed" ? Effective.Urgency ?? "" : "";
    public string UrgencyText => Display.Text(UrgencyKey).ToLowerInvariant();

    /// <summary>Next to or instead of the tag: the processing state, the blind-mode mask or "hoidettu".</summary>
    public string StateText =>
        Effective.Source == ClassificationSource.None ? (AiHidden ? Hidden : Status) :
        UrgencyKey.Length == 0 ? Status :
        IsDone ? "· hoidettu" : "";

    /// <summary>The AI's own answer, for the AI card: compared with the label, never replaced by it.</summary>
    public string AiActionKey => ShowAi ? Item.Assessment!.Action : "";
    public string AiActionText => Display.Text(AiActionKey);

    /// <summary>The deadline shown in the list: the label's, otherwise the AI's.</summary>
    public string EffectiveDeadline => Effective.Deadline?.ToString("d.M.yyyy") ?? "";

    public int PrioritySort => Triage.Priority(Item, blindMode());
    public DateOnly DeadlineSort => Triage.DeadlineSort(Item, blindMode());
    public string Category => Item.CategoryStatus is null
        ? ""
        : $"Outlook-luokka: {Item.CategoryOwned ?? "ei sovelluksen luokkaa"} ({Display.Text("luokka_" + Item.CategoryStatus)})";
    public string ModelInfo => ShowAi ? $"{Item.Model} · {Item.PromptVersion}{(Item.Truncated ? " · sisältö katkaistu" : "")}" : "";

    public string LabelInfo => Item.Label is null
        ? "Ei merkitty."
        : $"Merkitty {Item.Label.LabeledAt.ToLocalTime():dd.MM.yyyy HH.mm}, {Display.Text(Item.Label.Split)}" +
          $"{(Item.Label.Blind ? ", sokkona" : "")}{(Item.Label.AgreesWithAi ? ", sama kuin AI" : "")}.";

    private const string Hidden = "•••";
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string AllMailboxes = "Kaikki laatikot";

    private readonly AnalyserStore _store;
    private readonly OutlookMailbox _outlook = new();
    private readonly MonitorHost? _monitor;
    private readonly DisplayMask? _mask;
    private string _statusMonitor = "Seuranta käynnistyy…";
    private bool _monitorAttention;
    private bool _monitorPaused;
    private int _assessmentsAtReload;
    private int _assessmentsNow;
    private ItemViewModel? _selected;
    private string _filter = "huomio";
    private string _mailbox = AllMailboxes;
    private bool _blindMode;
    private bool _compact;
    private string? _editAction, _editUrgency, _editTopic;
    private DateTime? _editDeadline;
    private string _statusList = "", _statusLabels = "", _statusProblems = "";

    public MainViewModel(AppOptions options)
    {
        _mask = options.Mask ? new DisplayMask() : null;
        _store = AnalyserStore.Open(AppPaths.Database, KeyStore.GetOrCreateHexKey(AppPaths.DatabaseKey));
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ItemViewModel vm && Matches(vm);
        View.SortDescriptions.Add(new SortDescription(nameof(ItemViewModel.ReceivedSort), ListSortDirection.Descending));
        RefreshCommand = new RelayCommand(Refresh);
        SaveCommand = new RelayCommand(Save, () => Selected is not null && EditAction is not null && EditUrgency is not null && EditTopic is not null);
        AcceptAiCommand = new RelayCommand(AcceptAi, () => Selected?.ShowAi == true);
        DeleteLabelCommand = new RelayCommand(DeleteLabel, () => Selected?.IsLabeled == true);
        OpenCommand = new RelayCommand(OpenInOutlook, () => Selected is not null);
        ToggleDoneCommand = new RelayCommand(ToggleDone, () => Selected is not null);
        ProcessNowCommand = new RelayCommand(() => _monitor?.Monitor.ProcessNow(), () => _monitor is not null && !MonitorPaused);
        TogglePauseCommand = new RelayCommand(TogglePause, () => _monitor is not null);
        Refresh();

        var settings = AppSettings.Load(AppPaths.Settings);
        if (!options.Monitor)
        {
            StatusMonitor = "Seuranta ei ole käynnissä (näkymä avattiin ilman seurantaa)";
        }
        else if (settings.Stores.Count == 0)
        {
            StatusMonitor = "Seuranta ei ole käytössä: valitse laatikot komennolla email-analyser init --stores …";
        }
        else
        {
            _monitor = new MonitorHost(_outlook, settings);
            _monitor.Monitor.StatusChanged += status => Application.Current?.Dispatcher.BeginInvoke(() => OnMonitorStatus(status));
            _monitor.Start();
        }
    }

    public ObservableCollection<ItemViewModel> Items { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<string> Mailboxes { get; } = [AllMailboxes];
    public IReadOnlyList<FilterOption> FilterOptions { get; } =
    [
        new("huomio", "Vaatii huomiota"), new("kaikki", "Kaikki"), new("vastattava", "Vastattava"), new("tehtava", "Tehtävä"),
        new("tarkista", "Tarkista"), new("tiedoksi", "Tiedoksi"), new("merkitsematon", "Merkitsemättömät"),
        new("huomiotta", "Huomiotta jääneet"), new("ongelmat", "Ongelmat ja jonossa"), new(Triage.Done, "Hoidetut"),
    ];

    /// <summary>The mailbox column is redundant when one mailbox is chosen, and dropped in a narrow window.</summary>
    public bool ShowMailboxColumn => Mailbox == AllMailboxes && !Compact;

    /// <summary>The deadline is also in the detail pane, so a narrow window drops the column.</summary>
    public bool ShowDeadlineColumn => !Compact;

    /// <summary>Set by the window when it is too narrow for every column.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (Set(ref _compact, value))
            {
                OnPropertyChanged(nameof(ShowMailboxColumn));
                OnPropertyChanged(nameof(ShowDeadlineColumn));
            }
        }
    }

    public bool Masked => _mask is not null;
    public string WindowTitle => Masked ? "Sähköpostiavustaja – peitetty näkymä" : "Sähköpostiavustaja";
    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public IReadOnlyList<Option> ActionOptions => Display.Actions;
    public IReadOnlyList<Option> UrgencyOptions => Display.Urgencies;
    public IReadOnlyList<Option> TopicOptions => Display.Topics;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand AcceptAiCommand { get; }
    public RelayCommand DeleteLabelCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand ToggleDoneCommand { get; }
    public string DoneButtonText => Selected?.IsDone == true ? "Palauta hoitamattomaksi (Ctrl+E)" : "Merkitse hoidetuksi (Ctrl+E)";
    public RelayCommand ProcessNowCommand { get; }
    public RelayCommand TogglePauseCommand { get; }

    public string StatusMonitor { get => _statusMonitor; private set => Set(ref _statusMonitor, value); }

    /// <summary>A problem or a deferral longer than an hour: shown highlighted.</summary>
    public bool MonitorAttention { get => _monitorAttention; private set => Set(ref _monitorAttention, value); }

    public bool MonitorPaused
    {
        get => _monitorPaused;
        private set
        {
            if (Set(ref _monitorPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
            }
        }
    }

    public string PauseButtonText => MonitorPaused ? "Jatka seurantaa" : "Keskeytä seuranta";

    public string NewResultsHint => _assessmentsNow > _assessmentsAtReload
        ? $"Uusia arvioita {_assessmentsNow - _assessmentsAtReload}: päivitä (F5)"
        : "";

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                View.Refresh();
            }
        }
    }

    public string Mailbox
    {
        get => _mailbox;
        set
        {
            if (Set(ref _mailbox, value))
            {
                View.Refresh();
                UpdateFilterCounts();
                OnPropertyChanged(nameof(ShowMailboxColumn));
            }
        }
    }

    public bool BlindMode
    {
        get => _blindMode;
        set
        {
            if (Set(ref _blindMode, value))
            {
                Reload(Selected?.Id);
            }
        }
    }

    public ItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(NoSelection));
                OnPropertyChanged(nameof(DoneButtonText));
                LoadEditor();
            }
        }
    }

    public string? EditAction { get => _editAction; set => Set(ref _editAction, value); }
    public string? EditUrgency { get => _editUrgency; set => Set(ref _editUrgency, value); }
    public string? EditTopic { get => _editTopic; set => Set(ref _editTopic, value); }
    public DateTime? EditDeadline { get => _editDeadline; set => Set(ref _editDeadline, value); }

    public string StatusList { get => _statusList; private set => Set(ref _statusList, value); }
    public string StatusLabels { get => _statusLabels; private set => Set(ref _statusLabels, value); }
    public string StatusProblems { get => _statusProblems; private set => Set(ref _statusProblems, value); }

    public void Dispose()
    {
        // Stops a running batch and waits for the model to be unloaded.
        _monitor?.Dispose();
        _outlook.Dispose();
        _store.Dispose();
    }

    private void TogglePause()
    {
        if (_monitor is null)
        {
            return;
        }

        if (_monitor.Monitor.Paused)
        {
            _monitor.Monitor.Resume();
        }
        else
        {
            _monitor.Monitor.Pause();
        }

        MonitorPaused = _monitor.Monitor.Paused;
    }

    /// <summary>Selects the first row in the current view order that matches; for the screenshot run.</summary>
    public bool SelectFirst(Func<ItemViewModel, bool> predicate)
    {
        var match = View.Cast<ItemViewModel>().FirstOrDefault(predicate);
        if (match is not null)
        {
            Selected = match;
        }

        return match is not null;
    }

    /// <summary>The real texts that masked mode must keep off the screen.</summary>
    public IReadOnlyCollection<SensitiveText> SensitiveTexts() =>
        Screenshots.Prefixes(Items.SelectMany(vm => new (string, string?)[]
        {
            ("otsikko", vm.Item.Subject), ("lähettäjän nimi", vm.Item.SenderName), ("lähettäjän osoite", vm.Item.SenderAddress),
            ("laatikko", vm.Item.Mailbox), ("yhteenveto", vm.Item.Assessment?.Summary), ("perustelu", vm.Item.Assessment?.Rationale),
        }));

    private void OnMonitorStatus(MonitorStatus status)
    {
        if (Masked && status.Problem is not null)
        {
            // Problems can name mailboxes.
            status = status with { Problem = DisplayMask.HiddenProblem };
        }

        StatusMonitor = MonitorText.Describe(status);
        MonitorAttention = status.Problem is not null || status.DeferralWarning;
        MonitorPaused = status.Paused;
        _assessmentsNow = status.NewAssessments;
        OnPropertyChanged(nameof(NewResultsHint));
    }

    private void Refresh() => Reload(Selected?.Id);

    private void Reload(long? selectId)
    {
        // The list is refreshed only on request, so new results never disturb labelling.
        _assessmentsAtReload = _assessmentsNow;
        OnPropertyChanged(nameof(NewResultsHint));
        var items = _store.GetReviewItems();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(new ItemViewModel(item, () => BlindMode, _mask));
        }

        // Display names, so that masked mode lists placeholders.
        foreach (var mailbox in Items.Select(i => i.Mailbox).Distinct().Order().Where(m => !Mailboxes.Contains(m)))
        {
            Mailboxes.Add(mailbox);
        }

        Selected = selectId is null ? null : Items.FirstOrDefault(i => i.Id == selectId);
        UpdateFilterCounts();
        UpdateStatus(items);
    }

    private bool Matches(ItemViewModel vm) => InMailbox(vm) && MatchesFilter(vm, Filter);

    private bool InMailbox(ItemViewModel vm) => Mailbox == AllMailboxes || vm.Mailbox == Mailbox;

    private void UpdateFilterCounts()
    {
        var inMailbox = Items.Where(InMailbox).ToList();
        foreach (var option in FilterOptions)
        {
            option.Count = inMailbox.Count(vm => MatchesFilter(vm, option.Key));
        }
    }

    private bool MatchesFilter(ItemViewModel vm, string filter) => Triage.Matches(vm.Item, filter, BlindMode);

    private void LoadEditor()
    {
        var item = Selected?.Item;
        var label = item?.Label;
        var ai = Selected?.ShowAi == true ? item!.Assessment : null;
        EditAction = label?.Action ?? ai?.Action;
        EditUrgency = label?.Urgency ?? ai?.Urgency;
        EditTopic = label?.Topic ?? ai?.Topic;
        var deadline = label is not null ? label.Deadline : ai?.Deadline;
        EditDeadline = deadline?.ToDateTime(TimeOnly.MinValue);
    }

    private void Save()
    {
        if (Selected is null || EditAction is null || EditUrgency is null || EditTopic is null)
        {
            return;
        }

        var deadline = EditDeadline is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        var blind = BlindMode && Selected.Item.Label is null;
        _store.SaveLabel(Selected.Item, new UserLabel(EditAction, EditUrgency, EditTopic, deadline), blind, DateTimeOffset.UtcNow);
        AdvanceAfterLabel(Selected.Id);
    }

    private void AcceptAi()
    {
        if (Selected?.Item.Assessment is not { } ai)
        {
            return;
        }

        _store.SaveLabel(Selected.Item, new UserLabel(ai.Action, ai.Urgency, ai.Topic, ai.Deadline), blind: false, DateTimeOffset.UtcNow);
        AdvanceAfterLabel(Selected.Id);
    }

    private void DeleteLabel()
    {
        if (Selected is null)
        {
            return;
        }

        _store.DeleteLabel(Selected.Id);
        Reload(Selected.Id);
    }

    /// <summary>
    /// Marks the selected message done (or undoes it). Local only: nothing is written to
    /// Outlook. When the row leaves the current view, the next row is selected.
    /// </summary>
    private void ToggleDone()
    {
        if (Selected is not { } vm)
        {
            return;
        }

        _store.SetDone(vm.Id, !vm.IsDone, DateTimeOffset.UtcNow);
        // A row that stays in the view (e.g. "Kaikki") stays selected, so the change can be undone at once.
        Advance(vm.Id, stayIfVisible: true, _ => true);
    }

    /// <summary>After labelling, move to the next unlabelled message in the current view.</summary>
    private void AdvanceAfterLabel(long labeledId) =>
        Advance(labeledId, stayIfVisible: false, vm => !vm.IsLabeled && vm.Item.Assessment is not null);

    /// <summary>
    /// Reloads after a change to <paramref name="changedId"/> and selects the next row after it
    /// that satisfies <paramref name="next"/>. Without one, the changed row stays selected if
    /// it is still in the view.
    /// </summary>
    private void Advance(long changedId, bool stayIfVisible, Func<ItemViewModel, bool> next)
    {
        // The view order before reloading: the changed row may leave the current filter.
        var following = View.Cast<ItemViewModel>().Select(i => i.Id).SkipWhile(id => id != changedId).Skip(1).ToList();
        Reload(changedId);
        var visible = View.Cast<ItemViewModel>().ToDictionary(i => i.Id);
        if (stayIfVisible && visible.ContainsKey(changedId))
        {
            return;
        }

        var nextId = following.FirstOrDefault(id => visible.TryGetValue(id, out var vm) && next(vm));
        if (nextId != 0)
        {
            Selected = visible[nextId];
        }
        else if (!visible.ContainsKey(changedId))
        {
            Selected = null;
        }
    }

    private async Task OpenInOutlook()
    {
        if (Selected is null)
        {
            return;
        }

        try
        {
            if (!await _outlook.DisplayAsync(Selected.Item.Reference))
            {
                MessageBox.Show("Viestiä ei enää löytynyt Outlookista.", "Avaa Outlookissa");
            }
        }
        catch (OutlookUnavailableException ex)
        {
            MessageBox.Show(ex.Message, "Avaa Outlookissa");
        }
    }

    private void UpdateStatus(IReadOnlyList<ReviewItem> items)
    {
        // A snapshot of the list as of the last refresh; the live queue and batches are on the monitor line.
        StatusList = $"Listassa {items.Count}, arvioitu {items.Count(i => i.Assessment is not null)} (päivitetty {DateTime.Now:HH.mm})";
        var labeled = items.Where(i => i.Label is not null).ToList();
        StatusLabels = $"Merkitty {labeled.Count}/{items.Count(i => i.Assessment is not null)} " +
                       $"(säätöosa {labeled.Count(l => l.Label!.Split == LabelSplit.Tuning)}, arviointiosa {labeled.Count(l => l.Label!.Split == LabelSplit.Holdout)})";
        var problems = _store.ProblemsByMailbox();
        StatusProblems = problems.Count == 0 ? "Ei ongelmia" : "Ongelmia: " + string.Join(", ", problems.Select(p =>
            $"{_mask?.Mailbox(p.Key) ?? SafeText.Clean(p.Key, keepNewlines: false)} {p.Value}"));
    }
}
