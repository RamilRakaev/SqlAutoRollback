using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlAutoRollback.ViewModels;

public partial class ConnectionItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string serverName = string.Empty;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string userName = string.Empty;

    [ObservableProperty]
    private string connectionString = string.Empty;
}
