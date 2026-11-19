using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backend.Services.EngineManagement;

public interface IManagementSecretStore
{
    Task<string> WriteAsync(string key, string value, CancellationToken cancellationToken);
    Task<string> ReadAsync(string reference, CancellationToken cancellationToken);
}

public static class EngineManagementRegistration
{
    public static IServiceCollection AddEngineManagement(this IServiceCollection services)
    {
        services.AddScoped<EngineManagementStore>();
        services.AddScoped<IEnginePolicyReader>(provider => provider.GetRequiredService<EngineManagementStore>());
        services.AddScoped<IEngineReplicaReporter>(provider => provider.GetRequiredService<EngineManagementStore>());
        services.AddSingleton<ManagementSettingsCatalog>();
        services.AddSingleton<IManagementSecretStore>(provider =>
        {
            var config = provider.GetRequiredService<IConfiguration>();
            var vault = config["EngineManagement:VaultUri"];
            if (!string.IsNullOrEmpty(vault))
            {
                var uri = new Uri(vault, UriKind.Absolute);
                if (uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Management vault requires HTTPS.");
                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = config["EngineManagement:ManagedIdentityClientId"]
                });
                return new KeyVaultManagementSecretStore(new SecretClient(uri, credential));
            }
            if (provider.GetRequiredService<IHostEnvironment>().IsDevelopment())
                return new LocalManagementSecretStore();
            throw new InvalidOperationException("The management vault is unavailable.");
        });
        return services;
    }
}

internal sealed class KeyVaultManagementSecretStore(SecretClient client) : IManagementSecretStore
{
    public async Task<string> WriteAsync(string key, string value, CancellationToken cancellationToken)
    {
        var name = "setting-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var secret = await client.SetSecretAsync(name, value, cancellationToken);
        return $"{name}/{secret.Value.Properties.Version}";
    }

    public async Task<string> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        var parts = reference.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid managed secret reference.");
        // Always resolve against the configured vault and a specific version.
        var secret = await client.GetSecretAsync(parts[0], parts[1], cancellationToken);
        return secret.Value.Value;
    }
}

internal sealed class LocalManagementSecretStore : IManagementSecretStore
{
    private static readonly UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string _root = Path.Combine(UserDirectory(Environment.SpecialFolder.LocalApplicationData),
        "WorkflowBuilder", "EngineManagement");

    private static string UserDirectory(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("The local credential store requires a user profile.");
        return path;
    }

    public async Task<string> WriteAsync(string key, string value, CancellationToken cancellationToken)
    {
        EnsureDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var ciphertext = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser)
                : Encrypt(plaintext);
            await File.WriteAllBytesAsync(Path.Combine(_root, id), ciphertext, cancellationToken);
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<string> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(reference, "N", out var id)) throw new InvalidDataException("Invalid local secret reference.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, id.ToString("N")), cancellationToken);
        var plaintext = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)
            : Decrypt(bytes);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, PrivateDirectory);
            File.SetUnixFileMode(path, PrivateDirectory);
        }
    }

    private byte[] GetUnixKey()
    {
        var keyDirectory = Path.Combine(UserDirectory(Environment.SpecialFolder.ApplicationData),
            "WorkflowBuilder", "EngineManagementKeys");
        EnsureDirectory(keyDirectory);
        var path = Path.Combine(keyDirectory, "key");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(keyDirectory, Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllBytes(temporary, RandomNumberGenerator.GetBytes(32));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { } // Another local process installed the key first.
            }
            finally { File.Delete(temporary); }
        }
        return File.ReadAllBytes(path);
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var result = new byte[12 + 16 + plaintext.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(GetUnixKey(), 16);
        aes.Encrypt(nonce, plaintext, result.AsSpan(28), result.AsSpan(12, 16));
        return result;
    }

    private byte[] Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < 28) throw new CryptographicException("Invalid local secret.");
        var result = new byte[ciphertext.Length - 28];
        using var aes = new AesGcm(GetUnixKey(), 16);
        aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), result);
        return result;
    }
}
