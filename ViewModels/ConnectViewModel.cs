using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlAutoRollback.Models;
using SqlAutoRollback.Services;

namespace SqlAutoRollback.ViewModels;

public sealed partial class ConnectViewModel : ObservableObject
{
    public const string WindowsAuthentication = "Windows Authentication";
    public const string SqlServerAuthentication = "SQL Server Authentication";

    private readonly ISqlExecutionService _sql;

    [ObservableProperty]
    private string serverName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlServerAuthentication))]
    [NotifyPropertyChangedFor(nameof(AreCredentialFieldsEnabled))]
    private string selectedAuthentication = WindowsAuthentication;

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(AreCredentialFieldsEnabled))]
    private bool isBusy;

    public ConnectViewModel(ISqlExecutionService sql, ConnectionCredentials? lastUsed)
    {
        _sql = sql;
        AuthenticationModes =
        [
            WindowsAuthentication,
            SqlServerAuthentication
        ];

        if (lastUsed is null)
        {
            UserName = CurrentWindowsUser;
            return;
        }

        ServerName = lastUsed.ServerName;
        SelectedAuthentication = lastUsed.UseWindowsAuthentication
            ? WindowsAuthentication
            : SqlServerAuthentication;
        UserName = lastUsed.UseWindowsAuthentication
            ? CurrentWindowsUser
            : lastUsed.UserName;
        Password = lastUsed.UseWindowsAuthentication
            ? string.Empty
            : lastUsed.Password;
    }

    public IReadOnlyList<string> AuthenticationModes { get; }

    public bool IsSqlServerAuthentication => SelectedAuthentication == SqlServerAuthentication;

    public bool AreCredentialFieldsEnabled => IsSqlServerAuthentication && !IsBusy;

    public event Action<ConnectDialogResult?>? CloseRequested;

    public ConnectDialogResult? Result { get; private set; }

    private static string CurrentWindowsUser
    {
        get
        {
            var domain = Environment.UserDomainName;
            var user = Environment.UserName;
            return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
        }
    }

    private bool CanConnect() => !IsBusy;

    partial void OnSelectedAuthenticationChanged(string value)
    {
        if (IsSqlServerAuthentication)
        {
            return;
        }

        UserName = CurrentWindowsUser;
        Password = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(ServerName))
        {
            ErrorMessage = "Server Name is required.";
            return;
        }

        if (IsSqlServerAuthentication && string.IsNullOrWhiteSpace(UserName))
        {
            ErrorMessage = "User Name is required.";
            return;
        }

        var useWindows = !IsSqlServerAuthentication;
        var credentials = new ConnectionCredentials
        {
            ServerName = ServerName.Trim(),
            UserName = useWindows ? CurrentWindowsUser : UserName.Trim(),
            Password = useWindows ? string.Empty : Password,
            UseWindowsAuthentication = useWindows
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
