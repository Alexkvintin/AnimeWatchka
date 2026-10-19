using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
namespace AnimeApp.Providers.Uakino;
public sealed class UakinoBrowser
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CoreWebView2Controller? controller;
    public async Task<string> Load(string url, bool episodes, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "uakino.best") throw new ArgumentException("Недопустимый адрес UAKino.");
        await gate.WaitAsync(ct);
        try { return await Dispatcher.UIThread.InvokeAsync(() => LoadCore(url, episodes, ct)); }
        finally { gate.Release(); }
    }
    private async Task<string> LoadCore(string url, bool episodes, CancellationToken ct)
    {
        if (controller is null)
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var hwnd = window?.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("Главное окно ещё не готово.");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "WebView"));
            controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            window!.Closed += (_, _) => { controller?.Close(); controller = null; };
            // A rendered viewport is necessary for the lazy playlist loader.
            controller.Bounds = new(-11000, -11000, 1000, 800);
            controller.IsVisible = true;
            controller.CoreWebView2.IsMuted = true;
            controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            controller.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            controller.CoreWebView2.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme != "https" || target.Host != "uakino.best") e.Cancel = true; };
        }
        controller.IsVisible = true;
        var web = controller.CoreWebView2;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) done.TrySetResult(); else done.TrySetException(new IOException("UAKino не загрузился: " + e.WebErrorStatus + " HTTP " + e.HttpStatusCode)); }
        web.NavigationCompleted += Completed;
        try
        {
            var target = new Uri(url);
            if (target.AbsolutePath == "/ua/" && target.Query.StartsWith("?do=search&subaction=search&story="))
            {
                var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(target.Query[1..]));
                var request = web.Environment.CreateWebResourceRequest("https://uakino.best/ua/", "POST", body, "Content-Type: application/x-www-form-urlencoded\r\n");
                web.NavigateWithWebResourceRequest(request);
            }
            else web.Navigate(url);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            for (var i = 0; i < 25; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (episodes) await web.ExecuteScriptAsync("document.querySelector('.playlists-ajax')?.scrollIntoView()");
                var script = episodes ? "!!document.querySelector('.playlists-videos [data-file], .players-section iframe[src]:not([src*=youtube])')" : "!!document.querySelector('#dle-content')";
                if (await web.ExecuteScriptAsync(script) == "true")
                    return JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.documentElement.outerHTML")) ?? "";
                await Task.Delay(500, ct);
            }
            throw new IOException("UAKino не предоставил содержимое страницы. Проверьте сайт в браузере: возможна проверка доступа или временный сбой.");
        }
        catch (TimeoutException ex) { throw new IOException("UAKino не ответил вовремя. Повторите попытку позже.", ex); }
        finally { web.NavigationCompleted -= Completed; web.Stop(); if (controller is not null) controller.IsVisible = false; }
    }
}



