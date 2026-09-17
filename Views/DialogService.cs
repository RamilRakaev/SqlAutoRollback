using System.Windows;
using Microsoft.Win32;
using SqlAutoRollback.Models;
using SqlAutoRollback.Services;
using SqlAutoRollback.ViewModels;

namespace SqlAutoRollback.Views;

public sealed class DialogService : IDialogService
{
    private readonly ISqlExecutionService _sql;

    public DialogService(ISqlExecutionService sql)
    {
        _sql = sql;
    }

    public ConnectDialogResult? ShowConnectDialog(ConnectionCredentials? lastUsed, IReadOnlyList<string> knownServers)
    {
        var viewModel = new ConnectViewModel(_sql, lastUsed, knownServers);
        var window = new ConnectWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = viewModel
        };

        viewModel.CloseRequested += _ =>
        {
            try
            {
                window.DialogResult = viewModel.Result is not null;
            }
            catch (InvalidOperationException)
            {
                window.Close();
            }
        };

        return window.ShowDialog() == true ? viewModel.Result : null;
    }

    public string? ShowOpenSqlFileDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open SQL Script",
            Filter = "SQL Scripts (*.sql)|*.sql|All files (*.*)|*.*",
            DefaultExt = ".sql",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog(Application.Current.MainWindow) == true
            ? dialog.FileName
            : null;
    }

    public void ShowError(string message, string title)
    {
        MessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
