using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlAutoRollback.Models;

namespace SqlAutoRollback.Services;

public sealed class CredentialStore : ICredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public ConnectionCredentials? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.Credentials))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(AppPaths.Credentials);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<ConnectionCredentials>(json, JsonOptions);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(ConnectionCredentials credentials)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.Credentials, encrypted);
    }
}
