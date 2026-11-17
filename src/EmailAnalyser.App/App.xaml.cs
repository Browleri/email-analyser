using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace EmailAnalyser.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var finnish = CultureInfo.GetCultureInfo("fi-FI");
        CultureInfo.CurrentCulture = finnish;
        CultureInfo.CurrentUICulture = finnish;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(finnish.IetfLanguageTag)));

        DispatcherUnhandledException += (_, args) =>
        {
            // Exception messages here contain no message content.
            MessageBox.Show(args.Exception.Message, "Virhe", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
