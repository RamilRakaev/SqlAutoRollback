using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public interface ICredentialStore
{
    ConnectionCredentials? Load();

    void Save(ConnectionCredentials credentials);
}
