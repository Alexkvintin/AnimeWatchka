using Avalonia.Controls;
using Avalonia.Platform;
namespace AnimeApp.UI;

/// <summary>Owns the tray lifetime; system shutdown must never be turned into hiding.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Window window;
    private readonly Func<bool> enabled;
    private readonly Func<Task> suspend;
    private readonly Action resume;
    private readonly TrayIcon icon;
    private bool exiting;
    private bool hiding;
    public TrayController(Window window, Func<bool> enabled, Func<Task> suspend, Action resume)
    {
        this.window = window; this.enabled = enabled; this.suspend = suspend; this.resume = resume;
        using var stream = AssetLoader.Open(new Uri("avares://AnimeApp/Assets/app.ico"));
        icon = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "AnimeWatchka", IsVisible = false };
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Открыть AnimeWatchka");
        var exit = new NativeMenuItem("Выйти");
        open.Click += (_, _) => Restore();
        exit.Click += (_, _) => { exiting = true; window.Close(); };
        menu.Items.Add(open); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
        icon.Menu = menu; icon.Clicked += (_, _) => Restore();
        window.Closing += OnClosing;
        window.Closed += (_, _) => Dispose();
    }
    internal static bool ShouldHide(bool enabled, bool exiting, WindowCloseReason reason, bool programmatic)
        => enabled && !exiting && !programmatic && reason == WindowCloseReason.WindowClosing;
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (!ShouldHide(enabled(), exiting, args.CloseReason, args.IsProgrammatic)) return;
        args.Cancel = true;
        if (hiding) return;
        hiding = true;
        try
        {
            await suspend();
            icon.IsVisible = true;
            window.Hide();
        }
        catch
        {
            // Keep a usable visible window if saving/pausing failed.
            window.Show(); window.Activate();
        }
        finally { hiding = false; }
    }
    public void Restore()
    {
        if (hiding) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate(); resume(); icon.IsVisible = false;
    }
    public void Dispose() { window.Closing -= OnClosing; icon.Dispose(); }
}
