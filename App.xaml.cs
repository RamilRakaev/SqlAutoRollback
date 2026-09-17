using System.Windows;
using SqlAutoRollback.Services;
using SqlAutoRollback.ViewModels;
using SqlAutoRollback.Views;

namespace SqlAutoRollback;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "SqlAutoRollback", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppPaths.EnsureCreated();

        var sql = new SqlExecutionService();
        var files = new ScriptFileService();
        var history = new HistoryService();
        var credentials = new CredentialStore();
        var dialogs = new DialogService(sql);
        var viewModel = new MainViewModel(sql, files, history, dialogs, credentials);

        var window = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = window;
        window.Show();
    }
}
