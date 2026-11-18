using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace EmailAnalyser.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        var options = App.Options;
        Trace(options, "käynnistys");
        InitializeComponent();
        Trace(options, "ikkuna luotu");
        _viewModel = new MainViewModel(options);
        Trace(options, "näkymämalli luotu");
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();

        SizeChanged += (_, _) => _viewModel.Compact = ActualWidth < 1250;

        // Keep the selected row visible, also when labelling moves to the next message.
        List.SelectionChanged += (_, _) =>
        {
            if (List.SelectedItem is { } selected)
            {
                List.ScrollIntoView(selected);
            }
        };

        if (options.ScreenshotDirectory is { } directory)
        {
            // Loaded rather than ContentRendered: rendering to a bitmap does not need the window on screen.
            Directory.CreateDirectory(directory);
            Loaded += async (_, _) => await CaptureScreenshotsAsync(directory);
        }
    }

    /// <summary>Step names of the screenshot run, for diagnosing a run that hangs. No message data.</summary>
    private static void Trace(AppOptions options, string step)
    {
        if (options.ScreenshotDirectory is { } directory)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "vaiheet.txt"), $"{DateTime.Now:HH.mm.ss} {step}{Environment.NewLine}");
        }
    }

    private void ClearDeadline_Click(object sender, RoutedEventArgs e) => _viewModel.EditDeadline = null;

    /// <summary>WPF has no Finnish resources, so the date picker's hint would read "Select a date".</summary>
    private void DeadlinePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DatePicker picker)
        {
            return;
        }

        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is DatePickerTextBox box)
        {
            box.ApplyTemplate();
            if (box.Template.FindName("PART_Watermark", box) is ContentControl watermark)
            {
                watermark.Content = "Valitse päivä";
            }
        }
    }

    /// <summary>Captures the masked view in a few states and closes the window. Nothing is saved or changed.</summary>
    private async Task CaptureScreenshotsAsync(string directory)
    {
        try
        {
            var shots = new Screenshots(this, directory, _viewModel.SensitiveTexts());

            await SettleAsync();
            shots.Capture("01-vaatii-huomiota");

            if (_viewModel.SelectFirst(vm => vm.ActionKey is "vastattava" or "tehtava"))
            {
                await SettleAsync();
                shots.Capture("02-valittu-vastattava");
            }

            if (_viewModel.SelectFirst(vm => vm.SenderSuspected) || _viewModel.SelectFirst(vm => vm.HasWarning))
            {
                await SettleAsync();
                shots.Capture("03-varoitus");
            }

            _viewModel.Filter = "kaikki";
            _viewModel.SelectFirst(vm => vm.ActionKey == "tiedoksi");
            await SettleAsync();
            shots.Capture("04-kaikki-tiedoksi");

            _viewModel.Filter = "ongelmat";
            _viewModel.SelectFirst(_ => true);
            await SettleAsync();
            shots.Capture("05-ongelmat-ja-jono");

            _viewModel.Filter = "merkitsematon";
            _viewModel.BlindMode = true;
            _viewModel.SelectFirst(_ => true);
            await SettleAsync();
            shots.Capture("06-sokkomerkinta");

            _viewModel.BlindMode = false;
            _viewModel.Filter = "huomio";
            _viewModel.Selected = null;
            Width = MinWidth;
            Height = 640;
            await SettleAsync();
            shots.Capture("07-kapea-ei-valintaa");
        }
        catch (Exception ex)
        {
            // The exception type and message only; they carry no message content.
            File.WriteAllText(Path.Combine(directory, "virhe.txt"), $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private async Task SettleAsync()
    {
        await Task.Delay(500);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
    }
}
