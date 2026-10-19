using System.Net;
using System.Security.Cryptography;

namespace AnimeApp.Services.Shikimori;
public sealed class BrowserCallback : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Uri redirect;
    public string State { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static bool Supported(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 1024 && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" && uri.AbsolutePath.EndsWith('/');
    public BrowserCallback(string url)
    {
        if (!Supported(url))
            throw new ArgumentException("Нужен зарегистрированный локальный адрес возврата.");
        redirect = new Uri(url);
        listener.Prefixes.Add(url);
        listener.Start();
    }

    public async Task<string> Receive(CancellationToken ct)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var valid = context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == redirect.AbsolutePath && context.Request.QueryString["state"] == State;
            var code = valid ? context.Request.QueryString["code"] : null;
            var denied = valid && context.Request.QueryString["error"] is not null;
            context.Response.StatusCode = valid ? 200 : 400;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            var bytes = System.Text.Encoding.UTF8.GetBytes(valid ? "Можно вернуться в AnimeWatchka." : "Неверный запрос входа.");
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
            if (denied)
                throw new InvalidOperationException("Вход отменён в браузере.");
            if (!string.IsNullOrWhiteSpace(code))
                return code;
        }
    }

    public void Dispose() => listener.Close();
}
