using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlAutoRollback.Models;
using SqlAutoRollback.Services;

namespace SqlAutoRollback.ViewModels;

public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly ISqlExecutionService _sql;

    [ObservableProperty]
    private string serverName = string.Empty;

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool isBusy;

    public ConnectViewModel(ISqlExecutionService sql, ConnectionCredentials? lastUsed)
    {
        _sql = sql;
        if (lastUsed is null)
        {
            return;
        }

        ServerName = lastUsed.ServerName;
        UserName = lastUsed.UserName;
        Password = lastUsed.Password;
    }

    public event Action<ConnectDialogResult?>? CloseRequested;

    public ConnectDialogResult? Result { get; private set; }

    private bool CanConnect() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(ServerName))
        {
            ErrorMessage = "Server Name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(UserName))
        {
            ErrorMessage = "User Name is required.";
            return;
        }

        var credentials = new ConnectionCredentials
        {
            ServerName = ServerName.Trim(),
            UserName = UserName.Trim(),
            Password = Password
        };

        IsBusy = true;
        try
        {
            var connectionString = SqlConnectionFactory.Build(credentials);
            var info = await _sql.TestConnectionAsync(connectionString);
            Result = new ConnectDialogResult
            {
                Credentials = credentials,
                ProductMajorVersion = info.ProductMajorVersion
            };
            CloseRequested?.Invoke(Result);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(null);
    }
}
