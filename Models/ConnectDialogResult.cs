namespace SqlAutoRollback.Models;

public sealed class ConnectDialogResult
{
    public required ConnectionCredentials Credentials { get; init; }
    public string? ProductMajorVersion { get; init; }
}
