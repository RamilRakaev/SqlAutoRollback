using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public interface IDialogService
{
    ConnectDialogResult? ShowConnectDialog(ConnectionCredentials? lastUsed);

    string? ShowOpenSqlFileDialog();

    void ShowError(string message, string title);
}
