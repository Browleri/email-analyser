using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace EmailAnalyser.App;

public partial class App : Application
{
    public static AppOptions Options { get; private set; } = new(Mask: false, Monitor: true, ScreenshotDirectory: null);

    protected override void OnStartup(StartupEventArgs e)
    {
        Options = AppOptions.Parse(e.Args);
        var finnish = CultureInfo.GetCultureInfo("fi-FI");
        CultureInfo.CurrentCulture = finnish;
        CultureInfo.CurrentUICulture = finnish;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(finnish.IetfLanguageTag)));

        DispatcherUnhandledException += (_, args) =>
        {
            // Exception messages here contain no message content.
            if (Options.ScreenshotDirectory is { } directory)
            {
                // Unattended run: a dialog would wait forever.
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "virhe.txt"), $"{args.Exception.GetType().Name}: {args.Exception.Message}");
                Shutdown(2);
            }
            else
            {
                MessageBox.Show(args.Exception.Message, "Virhe", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
