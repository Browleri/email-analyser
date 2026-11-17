using System.Windows;

namespace EmailAnalyser.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void ClearDeadline_Click(object sender, RoutedEventArgs e) => _viewModel.EditDeadline = null;
}
