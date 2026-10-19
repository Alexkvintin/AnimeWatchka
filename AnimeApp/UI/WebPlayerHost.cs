using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using AnimeApp.Services.Playback;

namespace AnimeApp.UI;
public sealed class WebPlayerHost : NativeControlHost
{
    public event Action<CoreWebView2>? BrowserInitialized;
    private readonly WebPlayerEngine engine;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Controller? controller;
    private bool destroyed;
    private TaskCompletionSource navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? browserFolder;
    private ulong? activeNavigation;
    private bool navigating;
    public WebPlayerHost(WebPlayerEngine engine, string? browserExecutableFolder = null)
    {
        this.engine = engine;
        browserFolder = browserExecutableFolder;
        engine.Navigate = async url =>
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (destroyed)
                throw new ObjectDisposedException(nameof(WebPlayerHost));
            navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activeNavigation = null;
            navigating = true;
            controller!.CoreWebView2.Navigate(url);
            try
            {
                await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                navigating = false;
            }
        };
        engine.Execute = async script =>
        {
            try
            {
                if (controller is not null)
                    await controller.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Web player command failed: {0}", ex.GetType().Name);
            }
        };
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var hwnd = CreateWindowEx(0, "STATIC", "", 0x40000000 | 0x10000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(browserFolder, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "WebView"));
                if (destroyed)
                    return;
                controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
                if (destroyed)
                {
                    controller.Close();
                    return;
                }

                controller.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (!navigating || activeNavigation != e.NavigationId)
                        return;
                    if (e.IsSuccess)
                        navigation.TrySetResult();
                    else
                        navigation.TrySetException(new IOException($"Не удалось загрузить страницу веб-плеера ({e.WebErrorStatus}). Проверьте соединение или откройте страницу релиза."));
                };
                controller.DefaultBackgroundColor = System.Drawing.Color.Black;
                controller.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                controller.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                controller.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                    {
                        if (!navigating && e.Uri == "about:blank")
                            return;
                        e.Cancel = true;
                        if (navigating)
                            navigation.TrySetException(new IOException("Веб-источник попытался открыть незащищённый адрес."));
                    }
                    else if (navigating)
                        activeNavigation = e.NavigationId;
                };
                controller.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        using var d = JsonDocument.Parse(e.WebMessageAsJson);
                        var r = d.RootElement;
                        if (r.GetProperty("kind").GetString() != "animeapp-video")
                            return;
                        var position = r.GetProperty("position").GetDouble();
                        var duration = r.GetProperty("duration").GetDouble();
                        if (!double.IsFinite(position) || !double.IsFinite(duration) || position < 0 || duration < 0)
                            return;
                        engine.Update(new(position, duration, r.GetProperty("playing").GetBoolean(), r.GetProperty("ended").GetBoolean(), Seeking: r.GetProperty("seeking").GetBoolean()));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning("Invalid WebView media event: {0}", ex.GetType().Name);
                    }
                };
                await controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Script);
                BrowserInitialized?.Invoke(controller.CoreWebView2);
                Resize();
                ready.TrySetResult();
            }
            catch (Exception ex)
            {
                ready.TrySetException(new InvalidOperationException("Не удалось открыть WebView. Установите Microsoft Edge WebView2 Runtime.", ex));
            }
        });
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            Resize();
    }

    private void Resize()
    {
        if (controller is null)
            return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        controller.IsVisible = IsVisible;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        destroyed = true;
        ready.TrySetCanceled();
        navigation.TrySetCanceled();
        controller?.Close();
        controller = null;
        DestroyWindow(control.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    // Only ordinary HTML5 media on the loaded document. Cross-origin inaccessible frames are not bypassed.
    private const string Script = """
 (()=>{if(window!==window.top)return;let video;function find(){video=document.querySelector('video');return video;}
 window.animeAppMedia={play:()=>find()?.play(),pause:()=>find()?.pause(),seek:s=>{if(find())video.currentTime=s;},volume:v=>{if(find())video.volume=v;},speed:s=>{if(find())video.playbackRate=s;}};
 setInterval(()=>{if(!find())return;window.chrome.webview.postMessage({kind:'animeapp-video',position:Number.isFinite(video.currentTime)?video.currentTime:0,duration:Number.isFinite(video.duration)?video.duration:0,playing:!video.paused,ended:video.ended,seeking:video.seeking});},1000);})();
 """;
}
