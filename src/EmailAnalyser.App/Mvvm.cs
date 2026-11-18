using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace EmailAnalyser.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute)
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed record Option(string Key, string Text);

/// <summary>A list filter whose text shows how many rows it matches.</summary>
public sealed class FilterOption(string key, string label) : ObservableObject
{
    private int? _count;

    public string Key { get; } = key;

    public string Text => _count is { } count ? $"{label} ({count})" : label;

    public int? Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
            {
                OnPropertyChanged(nameof(Text));
            }
        }
    }
}

/// <summary>Gives DataGrid columns, which are outside the visual tree, access to the view model.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>Finnish display texts for stored keys.</summary>
public static class Display
{
    public static readonly IReadOnlyList<Option> Actions =
    [
        new("vastattava", "Vastattava"), new("tehtava", "Tehtävä"), new("tiedoksi", "Tiedoksi"), new("tarkista", "Tarkista"),
    ];

    public static readonly IReadOnlyList<Option> Urgencies =
    [
        new("tanaan", "Tänään"), new("talla_viikolla", "Tällä viikolla"), new("myohemmin", "Myöhemmin"), new("ei_maaraaikaa", "Ei kiirettä"),
    ];

    public static readonly IReadOnlyList<Option> Topics =
    [
        new("tyo", "Työ"), new("sijoitukset", "Omat sijoitukset"), new("perheyhtio", "Perheyhtiö"), new("kohdeyhtiot", "Kohdeyhtiöt"),
        new("markkinat", "Markkinat"), new("sijoitusmahdollisuudet", "Sijoitusmahdollisuudet"), new("tapahtumat", "Tapahtumat"),
        new("hallinto", "Hallinto"), new("ilmoitukset", "Ilmoitukset"),
        new("henkilokohtainen", "Henkilökohtainen"), new("uutiskirje", "Uutiskirje"), new("muu", "Muu"),
    ];

    private static readonly Dictionary<string, string> Other = new()
    {
        ["queued"] = "Jonossa", ["assessed"] = "Arvioitu", ["invalid"] = "Virheellinen vastaus",
        ["unreadable"] = "Ei luettavissa", ["gone"] = "Poistunut", ["failed"] = "Epäonnistui",
        ["puuttuva_konteksti"] = "puuttuva konteksti", ["epaselva_pyynto"] = "epäselvä pyyntö",
        ["katkaistu_sisalto"] = "katkaistu sisältö", ["ristiriita"] = "ristiriita", ["ohjailuyritys"] = "mahdollinen ohjailuyritys",
        ["huijausmerkki"] = "lähettäjän tunnistus ei täsmää",
        ["saato"] = "säätöosa", ["arviointi"] = "arviointiosa",
        ["luokka_Written"] = "kirjoitettu", ["luokka_Unchanged"] = "oli jo kunnossa", ["luokka_Gone"] = "viesti poistunut",
        ["luokka_Mismatch"] = "viesti muuttunut, ei kirjoitettu", ["luokka_NotSupported"] = "vain M365-laatikoihin",
        ["luokka_Failed"] = "kirjoitus epäonnistui",
    };

    public static string Text(string? key) =>
        key is null ? "" :
        Actions.Concat(Urgencies).Concat(Topics).FirstOrDefault(o => o.Key == key)?.Text ?? Other.GetValueOrDefault(key, key);
}
