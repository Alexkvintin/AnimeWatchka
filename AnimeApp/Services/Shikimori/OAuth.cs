using System.Security.Cryptography;
using System.Text.Json;

namespace AnimeApp.Services.Shikimori;
public record OAuthOptions(string ClientId = "", string ClientSecret = "", string RedirectUri = "urn:ietf:wg:oauth:2.0:oob", string BaseUrl = "https://shikimori.io", string ApplicationName = "AnimeApp", string TokenBrokerUrl = "")
{
    public override string ToString() => "OAuth options [protected]";
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && (!string.IsNullOrWhiteSpace(ClientSecret) || IsBrokerConfigured);
    public bool IsBrokerConfigured => Uri.TryCreate(TokenBrokerUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query);

    public static OAuthOptions Load()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "oauth-public.json");
        var publicConfig = File.Exists(file) ? JsonSerializer.Deserialize<PublicOAuthConfig>(File.ReadAllText(file)) : null;
        return new(Environment.GetEnvironmentVariable("SHIKIMORI_CLIENT_ID") ?? publicConfig?.ClientId ?? "",
            Environment.GetEnvironmentVariable("SHIKIMORI_CLIENT_SECRET") ?? "",
            Environment.GetEnvironmentVariable("SHIKIMORI_REDIRECT_URI") ?? publicConfig?.RedirectUri ?? "http://127.0.0.1:17842/callback/",
            Environment.GetEnvironmentVariable("SHIKIMORI_BASE_URL") ?? "https://shikimori.io",
            TokenBrokerUrl: publicConfig?.TokenBrokerUrl ?? "");
    }
}
public record PublicOAuthConfig(string ClientId, string TokenBrokerUrl, string RedirectUri = "http://127.0.0.1:17842/callback/");

public record OAuthToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "OAuth token [protected]";
}

public interface ITokenStore
{
    Task<OAuthToken?> Read(CancellationToken ct = default);
    Task Write(OAuthToken token, CancellationToken ct = default);
    Task Clear(CancellationToken ct = default);
}

public sealed class WindowsTokenStore(string path) : ITokenStore
{
    private readonly ProtectedFile<OAuthToken> file = new(path);
    public Task<OAuthToken?> Read(CancellationToken ct = default) => file.Read(ct);
    public Task Write(OAuthToken value, CancellationToken ct = default) => file.Write(value, ct);
    public Task Clear(CancellationToken ct = default) => file.Clear(ct);
}
