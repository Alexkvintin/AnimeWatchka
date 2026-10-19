using System.Security.Cryptography;
using System.Text.Json;

namespace AnimeApp.Services.Shikimori;
public sealed record OAuthCredentials(string ClientId, string ClientSecret, string RedirectUri = "urn:ietf:wg:oauth:2.0:oob")
{
    public override string ToString() => "OAuth credentials [protected]";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("Введите Client ID OAuth-приложения Shikimori.");
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Введите Client Secret OAuth-приложения Shikimori.");
        if (RedirectUri != "urn:ietf:wg:oauth:2.0:oob" && !BrowserCallback.Supported(RedirectUri) && (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
            throw new InvalidOperationException("Redirect URI должен быть urn:ietf:wg:oauth:2.0:oob или HTTPS-адресом, зарегистрированным в Shikimori.");
    }
}

public interface ICredentialStore
{
    Task<OAuthCredentials?> Read(CancellationToken ct = default);
    Task Write(OAuthCredentials credentials, CancellationToken ct = default);
    Task Clear(CancellationToken ct = default);
}

public sealed class ProtectedFile<T>(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<T?> Read(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI required.");
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
                return default;
            var encrypted = await File.ReadAllBytesAsync(path, ct);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<T>(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Не удалось расшифровать защищённые данные. Сохраните данные подключения заново под текущим пользователем Windows.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Write(T value, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI required.");
        await gate.WaitAsync(ct);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var plain = JsonSerializer.SerializeToUtf8Bytes(value);
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            await File.WriteAllBytesAsync(temporary, encrypted, ct);
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    public async Task Clear(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class WindowsCredentialStore(string path) : ICredentialStore
{
    private readonly ProtectedFile<OAuthCredentials> file = new(path);
    public Task<OAuthCredentials?> Read(CancellationToken ct = default) => file.Read(ct);
    public Task Write(OAuthCredentials value, CancellationToken ct = default) => file.Write(value, ct);
    public Task Clear(CancellationToken ct = default) => file.Clear(ct);
}

public sealed class CredentialManager(ICredentialStore store, Func<OAuthOptions>? environment = null)
{
    public OAuthOptions Current { get; private set; } = new();
    public string Source { get; private set; } = "не заданы";

    public async Task Reload(CancellationToken ct = default)
    {
        var saved = await store.Read(ct);
        if (saved is not null && (string.IsNullOrWhiteSpace(saved.ClientId) || string.IsNullOrWhiteSpace(saved.ClientSecret)))
            saved = null;
        var env = (environment ?? OAuthOptions.Load)();
        // Upgrade this application's legacy OOB settings without touching tokens or
        // overwriting a developer's credentials for a different OAuth application.
        if (env.Configured && env.IsBrokerConfigured && BrowserCallback.Supported(env.RedirectUri) &&
            (saved is null || saved.ClientId == env.ClientId))
        {
            Current = env;
            Source = "настройки приложения";
            return;
        }
        Current = saved is not null ? new(saved.ClientId, saved.ClientSecret, saved.RedirectUri, env.BaseUrl) : env;
        Source = saved is not null ? "сохранены в Windows" : env.Configured ? "переменные окружения" : "не заданы";
    }

    public async Task Save(OAuthCredentials value, CancellationToken ct = default)
    {
        value.Validate();
        await store.Write(value, ct);
        await Reload(ct);
    }

    public async Task Delete(CancellationToken ct = default)
    {
        await store.Clear(ct);
        await Reload(ct);
    }
}
