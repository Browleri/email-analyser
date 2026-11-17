using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using EmailAnalyser.Core;
using EmailAnalyser.Core.Storage;
using EmailAnalyser.Outlook;

namespace EmailAnalyser.App;

public sealed class ItemViewModel(ReviewItem item, Func<bool> blindMode)
{
    public ReviewItem Item { get; } = item;

    public long Id => Item.MessageId;
    public string Mailbox => SafeText.Clean(Item.Mailbox, keepNewlines: false);
    public string Received => Item.Received.ToLocalTime().ToString("dd.MM.yyyy HH.mm");
    public DateTimeOffset ReceivedSort => Item.Received;
    public string Sender => SafeText.Clean(Item.SenderName, keepNewlines: false);
    public string SenderAddress => SafeText.Clean(Item.SenderAddress, keepNewlines: false);
    public string Subject => Item.Subject is not null
        ? SafeText.Clean(Item.Subject, keepNewlines: false)
        : Item.Assessment is not null || Item.Label is not null ? "(tunnistetiedot poistettu säilytysajan päätyttyä)" : "(ei vielä luettu)";
    public string Status => Display.Text(Item.Status);
    public bool IsLabeled => Item.Label is not null;
    public string LabeledMark => IsLabeled ? "✓" : "";

    /// <summary>In blind mode the AI's answer is hidden until the user has labelled the message.</summary>
    public bool ShowAi => Item.Assessment is not null && (!blindMode() || IsLabeled);

    public string AiAction => ShowAi ? Display.Text(Item.Assessment!.Action) : Hidden;
    public string AiUrgency => ShowAi ? Display.Text(Item.Assessment!.Urgency) : Hidden;
    public string AiTopic => ShowAi ? Display.Text(Item.Assessment!.Topic) : Hidden;
    public string AiDeadline => ShowAi ? Item.Assessment!.Deadline?.ToString("dd.MM.yyyy") ?? "–" : Hidden;
    public string Summary => ShowAi ? SafeText.ModelText(Item.Assessment!.Summary) : "";
    public string Rationale => ShowAi ? SafeText.ModelText(Item.Assessment!.Rationale) : "";
    public string Review => !ShowAi || !Item.Assessment!.NeedsReview
        ? ""
        : "Tarkistettava: " + string.Join(", ", Item.Assessment.ReviewReasons.Select(Display.Text));
    public bool InjectionSuspected => Item.Assessment?.ReviewReasons.Contains(InjectionHeuristics.ReviewReason) == true;
    public bool SenderSuspected => Item.Assessment?.ReviewReasons.Contains(SenderAuthentication.ReviewReason) == true;
    public string Category => Item.CategoryStatus is null
        ? ""
        : $"Outlook-luokka: {Item.CategoryOwned ?? "ei sovelluksen luokkaa"} ({Display.Text("luokka_" + Item.CategoryStatus)})";
    public string ModelInfo => ShowAi ? $"{Item.Model} · {Item.PromptVersion}{(Item.Truncated ? " · sisältö katkaistu" : "")}" : "";

    /// <summary>The user says it needs attention but the AI did not flag it.</summary>
    public bool Missed =>
        Item.Label is not null && Item.Assessment is not null &&
        Labels.AttentionActions.Contains(Item.Label.Action) &&
        !Labels.AttentionActions.Contains(Item.Assessment.Action) && Item.Assessment.Action != "tarkista";

    public string LabelInfo => Item.Label is null
        ? "Ei merkitty."
        : $"Merkitty {Item.Label.LabeledAt.ToLocalTime():dd.MM.yyyy HH.mm}, {Display.Text(Item.Label.Split)}" +
          $"{(Item.Label.Blind ? ", sokkona" : "")}{(Item.Label.AgreesWithAi ? ", sama kuin AI" : "")}.";

    private const string Hidden = "•••";
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    public static readonly IReadOnlyList<Option> Filters =
    [
        new("kaikki", "Kaikki"), new("huomio", "Vastattava ja tehtävä"), new("vastattava", "Vastattava"), new("tehtava", "Tehtävä"),
        new("tarkista", "Tarkista"), new("tiedoksi", "Tiedoksi"), new("merkitsematon", "Merkitsemättömät"),
        new("huomiotta", "Huomiotta jääneet"), new("ongelmat", "Ongelmat ja jonossa"),
    ];

    private const string AllMailboxes = "Kaikki laatikot";

    private readonly AnalyserStore _store;
    private readonly OutlookMailbox _outlook = new();
    private readonly MonitorHost? _monitor;
    private string _statusMonitor = "Seuranta käynnistyy…";
    private bool _monitorAttention;
    private bool _monitorPaused;
    private int _assessmentsAtReload;
    private int _assessmentsNow;
    private ItemViewModel? _selected;
    private string _filter = "kaikki";
    private string _mailbox = AllMailboxes;
    private bool _blindMode;
    private string? _editAction, _editUrgency, _editTopic;
    private DateTime? _editDeadline;
    private string _statusQueue = "", _statusBatch = "", _statusLabels = "", _statusProblems = "";

    public MainViewModel()
    {
        _store = AnalyserStore.Open(AppPaths.Database, KeyStore.GetOrCreateHexKey(AppPaths.DatabaseKey));
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ItemViewModel vm && Matches(vm);
        View.SortDescriptions.Add(new SortDescription(nameof(ItemViewModel.ReceivedSort), ListSortDirection.Descending));
        RefreshCommand = new RelayCommand(Refresh);
        SaveCommand = new RelayCommand(Save, () => Selected is not null && EditAction is not null && EditUrgency is not null && EditTopic is not null);
        AcceptAiCommand = new RelayCommand(AcceptAi, () => Selected?.ShowAi == true);
        DeleteLabelCommand = new RelayCommand(DeleteLabel, () => Selected?.IsLabeled == true);
        OpenCommand = new RelayCommand(OpenInOutlook, () => Selected is not null);
        ProcessNowCommand = new RelayCommand(() => _monitor?.Monitor.ProcessNow(), () => _monitor is not null && !MonitorPaused);
        TogglePauseCommand = new RelayCommand(TogglePause, () => _monitor is not null);
        Refresh();

        var settings = AppSettings.Load(AppPaths.Settings);
        if (settings.Stores.Count == 0)
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
    public IReadOnlyList<Option> FilterOptions => Filters;
    public IReadOnlyList<Option> ActionOptions => Display.Actions;
    public IReadOnlyList<Option> UrgencyOptions => Display.Urgencies;
    public IReadOnlyList<Option> TopicOptions => Display.Topics;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand AcceptAiCommand { get; }
    public RelayCommand DeleteLabelCommand { get; }
    public RelayCommand OpenCommand { get; }
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
                LoadEditor();
            }
        }
    }

    public string? EditAction { get => _editAction; set => Set(ref _editAction, value); }
    public string? EditUrgency { get => _editUrgency; set => Set(ref _editUrgency, value); }
    public string? EditTopic { get => _editTopic; set => Set(ref _editTopic, value); }
    public DateTime? EditDeadline { get => _editDeadline; set => Set(ref _editDeadline, value); }

    public string StatusQueue { get => _statusQueue; private set => Set(ref _statusQueue, value); }
    public string StatusBatch { get => _statusBatch; private set => Set(ref _statusBatch, value); }
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

    private void OnMonitorStatus(MonitorStatus status)
    {
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
            Items.Add(new ItemViewModel(item, () => BlindMode));
        }

        foreach (var mailbox in items.Select(i => i.Mailbox).Distinct().Order().Where(m => !Mailboxes.Contains(m)))
        {
            Mailboxes.Add(mailbox);
        }

        Selected = selectId is null ? null : Items.FirstOrDefault(i => i.Id == selectId);
        UpdateStatus(items);
    }

    private bool Matches(ItemViewModel vm)
    {
        if (Mailbox != AllMailboxes && vm.Item.Mailbox != Mailbox)
        {
            return false;
        }

        var action = vm.ShowAi ? vm.Item.Assessment!.Action : null;
        return Filter switch
        {
            "huomio" => action is "vastattava" or "tehtava",
            "vastattava" or "tehtava" or "tarkista" or "tiedoksi" => action == Filter,
            "merkitsematon" => !vm.IsLabeled && vm.Item.Assessment is not null,
            "huomiotta" => vm.Missed,
            "ongelmat" => vm.Item.Status is not "assessed",
            _ => true,
        };
    }

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

    /// <summary>After labelling, move to the next unlabelled message in the current view.</summary>
    private void AdvanceAfterLabel(long labeledId)
    {
        // The view order before reloading: with the "unlabelled" filter the labelled row disappears.
        var following = View.Cast<ItemViewModel>().Select(i => i.Id).SkipWhile(id => id != labeledId).Skip(1).ToList();
        Reload(labeledId);
        var byId = Items.ToDictionary(i => i.Id);
        var nextId = following.FirstOrDefault(id => byId.TryGetValue(id, out var vm) && !vm.IsLabeled && vm.Item.Assessment is not null);
        if (nextId != 0)
        {
            Selected = byId[nextId];
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
        StatusQueue = $"Jonossa {items.Count(i => i.Status == "queued")} · arvioitu {items.Count(i => i.Assessment is not null)}/{items.Count}";
        var last = _store.RecentBatches(1).FirstOrDefault();
        StatusBatch = last is null
            ? "Ei eriä"
            : $"Viimeisin erä {last.Started.ToLocalTime():dd.MM. HH.mm}: {last.Outcome}{(last.Reason is null ? "" : $" ({last.Reason})")}";
        var labeled = items.Where(i => i.Label is not null).ToList();
        StatusLabels = $"Merkitty {labeled.Count}/{items.Count(i => i.Assessment is not null)} " +
                       $"(säätöosa {labeled.Count(l => l.Label!.Split == LabelSplit.Tuning)}, arviointiosa {labeled.Count(l => l.Label!.Split == LabelSplit.Holdout)})";
        var problems = _store.ProblemsByMailbox();
        StatusProblems = problems.Count == 0 ? "Ei ongelmia" : "Ongelmia: " + string.Join(", ", problems.Select(p => $"{p.Key} {p.Value}"));
    }
}
