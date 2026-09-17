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
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationName = "SqlAutoRollback",
            ConnectTimeout = 15
        };

        if (credentials.UseWindowsAuthentication)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = credentials.UserName;
            builder.Password = credentials.Password;
        }

        return builder.ConnectionString;
    }
}
