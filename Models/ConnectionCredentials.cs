namespace SqlAutoRollback.Models;

public sealed class ConnectionCredentials
{
    public string ServerName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
