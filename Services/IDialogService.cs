using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public interface IDialogService
{
    ConnectDialogResult? ShowConnectDialog(ConnectionCredentials? lastUsed, IReadOnlyList<string> knownServers);

    string? ShowOpenSqlFileDialog();

    void ShowError(string message, string title);
}
