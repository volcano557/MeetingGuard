using MeetingAgent.App.ViewModels;
using System.Windows;

namespace MeetingAgent.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded  += async (_, _) => { await viewModel.OnLoadedAsync(); viewModel.InitStatistics(); };
        Closing += async (_, _) => await viewModel.OnClosingAsync();
    }
}
