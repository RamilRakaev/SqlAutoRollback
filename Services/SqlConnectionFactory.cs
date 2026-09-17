using Microsoft.Data.SqlClient;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public static class SqlConnectionFactory
{
    public static string Build(ConnectionCredentials credentials)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = credentials.ServerName,
            UserID = credentials.UserName,
            Password = credentials.Password,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationName = "SqlAutoRollback",
            ConnectTimeout = 15
        };

        return builder.ConnectionString;
    }
}
