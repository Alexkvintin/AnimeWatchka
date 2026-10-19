using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AnimeApp.Services.Playback;
using AnimeApp.ViewModels;
using LibVLCSharp.Avalonia;

namespace AnimeApp.UI;
public sealed partial class PlayerWindow : Window
{
    private readonly PlayerViewModel vm;
    private readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    public FullscreenState Fullscreen { get; } = new();

    private WindowState previousState;
    private PixelPoint previousPosition;
    private Size previousSize;
    private Grid? overlay;
    private readonly DispatcherTimer hudTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private bool closing;
    private bool tickRunning;
    private bool dragging;
    private Window? overlayWindow;
    private Task startup = Task.CompletedTask;
    internal Task Ready => startup;
    public async Task SuspendToTray()
    {
        await startup;
        await vm.Suspend();
        if (Fullscreen.Active) ToggleFullscreen();
        Hide();
    }
    public void RestoreFromTray()
    {
        Show(); Activate();
    }
    public async Task Shutdown()
    {
        if (closing)
            return;
        closing = true;
        timer.Stop();
        hudTimer.Stop();
        await vm.Close(() =>
        {
            var surface = this.FindControl<ContentControl>("Surface")!;
            if (surface.Content is VideoView video)
                video.MediaPlayer = null;
            surface.Content = null;
        });
        Close();
    }

    public PlayerWindow()
    {
        vm = null!;
        AvaloniaXamlLoader.Load(this);
    }

    public PlayerWindow(PlayerViewModel vm)
    {
        this.vm = vm;
        DataContext = vm;
        AvaloniaXamlLoader.Load(this);
        var surface = this.FindControl<ContentControl>("Surface")!;
        surface.Content = vm.Engine switch
        {
            VlcPlayerEngine vlc => new VideoView
            {
                MediaPlayer = vlc.Player
            },
            MpvPlayerEngine mpv => new MpvVideoView(mpv),
            WebPlayerEngine web => new WebPlayerHost(web),
            _ => new TextBlock
            {
                Text = "Тестовый проигрыватель"
            }
        };
        var slider = this.FindControl<Slider>("Timeline")!;
        slider.AddHandler(PointerPressedEvent, (_, _) => dragging = true, RoutingStrategies.Tunnel);
        slider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            vm.SeekCommand.Execute(null);
            dragging = false;
        }, RoutingStrategies.Tunnel);
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
                vm.SeekCommand.Execute(null);
        };
        vm.CloseRequested += Close;
        vm.FullscreenRequested += ToggleFullscreen;
        if (surface.Content is VideoView vv)
        {
            overlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Focusable = true,
                DataContext = vm
            };
            vv.Content = overlay;
            overlay.PointerMoved += OnPointerMoved;
            overlay.AttachedToVisualTree += (_, _) =>
            {
                if (TopLevel.GetTopLevel(overlay) is Window floating && floating != overlayWindow)
                {
                    overlayWindow = floating;
                    floating.AddHandler(KeyDownEvent, OnPlayerKey, RoutingStrategies.Tunnel);
                    floating.AddHandler(KeyUpEvent, OnPlayerKeyReleased, RoutingStrategies.Tunnel);
                    floating.Activated += (_, _) => overlay.Focus();
                    Dispatcher.UIThread.Post(() => overlay.Focus());
                }
            };
            overlay.PointerPressed += (_, e) =>
            {
                Interact();
                if (e.ClickCount == 2 && e.Source == overlay)
                    ToggleFullscreen();
                if (e.Source == overlay) overlay.Focus();
            };
            overlay.AddHandler(KeyDownEvent, OnPlayerKey, RoutingStrategies.Tunnel);
            overlay.AddHandler(KeyUpEvent, OnPlayerKeyReleased, RoutingStrategies.Tunnel);
        }

        PointerMoved += OnPointerMoved;
        hudTimer.Tick += (_, _) =>
        {
            var controlsHovered = this.FindControl<Border>("Controls")!.IsPointerOver;
            if (controlsHovered || vm.ShowOptions || dragging) Fullscreen.Interact(DateTimeOffset.UtcNow);
            var visible = Fullscreen.ControlsVisible(DateTimeOffset.UtcNow);
            var controls = this.FindControl<Border>("Controls")!;
            if (controls.IsVisible != visible)
            {
                controls.IsVisible = visible;
                if (!visible) overlay?.Focus();
            }
            if (overlay is not null)
                overlay.Cursor = new Cursor(visible ? StandardCursorType.Arrow : StandardCursorType.None);
            if (vm.Engine is VlcPlayerEngine vlc)
            {
                uint w = 0, h = 0;
                vlc.Player.Size(0, ref w, ref h);
                var screen = Screens.ScreenFromWindow(this);
                this.FindControl<TextBlock>("VideoInfo")!.Text = vm.QualityInfo + "\n" + $"Источник: {w}×{h} · {vlc.CodecInfo} · Экран: {screen?.Bounds.Width}×{screen?.Bounds.Height}\nScaling: {vlc.ScalerInfo} {vlc.FallbackReason}";
            }
            else if (vm.Engine is MpvPlayerEngine mpv)
                this.FindControl<TextBlock>("VideoInfo")!.Text = vm.QualityInfo + "\n" + mpv.VideoInfo;
        };
        hudTimer.Start();
        timer.Tick += async (_, _) =>
        {
            if (tickRunning || dragging || closing)
                return;
            tickRunning = true;
            try
            {
                await vm.Tick();
            }
            catch (Exception ex)
            {
                vm.ReportFailure(ex);
            }
            finally
            {
                tickRunning = false;
            }
        };
        var started = false;
        Opened += async (_, _) =>
        {
            if (started) return;
            started = true;
            startup = vm.Initialize();
            await startup;
            Dispatcher.UIThread.Post(() => overlay?.Focus());
            if (!closing)
                timer.Start();
        };
        Closing += async (_, e) =>
        {
            if (closing)
                return;
            e.Cancel = true;
            await Shutdown();
        };
        AddHandler(KeyDownEvent, OnPlayerKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPlayerKeyReleased, RoutingStrategies.Tunnel);
    }

    private void OnPlayerKeyReleased(object? sender, KeyEventArgs e)
    {
        // Buttons activate on key-up; consuming only key-down still clicks the focused control.
        if (e.Key is Key.Space or Key.F or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.O)
            e.Handled = true;
    }

    private void Interact()
    {
        Fullscreen.Interact(DateTimeOffset.UtcNow);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Source is not Visual visual || TopLevel.GetTopLevel(visual) is not {} top) return;
        var point = e.GetPosition(top);
        var screen = top.PointToScreen(point);
        // Layout/cursor changes can synthesize PointerMoved without physical movement.
        Fullscreen.PointerMoved(screen.X, screen.Y, point.Y >= top.Bounds.Height - 180, DateTimeOffset.UtcNow);
    }

    private void OnPlayerKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;
        Interact();
        switch (e.Key)
        {
            case Key.Escape:
                if (Fullscreen.Active)
                    ToggleFullscreen();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.Space:
                vm.TogglePlayCommand.Execute(null);
                break;
            case Key.Left:
                vm.SeekPosition = Math.Max(0, vm.Engine.Snapshot.Position - 10);
                vm.SeekCommand.Execute(null);
                break;
            case Key.Right:
                var snapshot = vm.Engine.Snapshot;
                vm.SeekPosition = Math.Min(snapshot.Duration, snapshot.Position + 10);
                vm.SeekCommand.Execute(null);
                break;
            case Key.Up:
                vm.Volume = Math.Min(100, vm.Volume + 5);
                break;
            case Key.Down:
                vm.Volume = Math.Max(0, vm.Volume - 5);
                break;
            case Key.O:
                vm.SkipOpeningCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    public void ToggleFullscreen()
    {
        var root = this.FindControl<Grid>("PlayerRoot")!;
        var header = this.FindControl<Grid>("Header")!;
        var controls = this.FindControl<Border>("Controls")!;
        if (!Fullscreen.Active)
        {
            previousState = WindowState;
            previousPosition = Position;
            previousSize = new(Width, Height);
            Fullscreen.Enter(DateTimeOffset.UtcNow);
            header.IsVisible = false;
            root.Margin = new(0);
            if (overlay is not null)
            {
                root.Children.Remove(controls);
                controls.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
                controls.Margin = new(32, 0, 32, 24);
                controls.MaxWidth = 1120;
                controls.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                overlay.Children.Add(controls);
            }
            else
                controls.IsVisible = false;
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.FullScreen;
            overlay?.Focus();
        }
        else
        {
            Fullscreen.Exit();
            if (overlay is not null)
            {
                overlay.Children.Remove(controls);
                root.Children.Add(controls);
            }

            controls.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            controls.Margin = new(0, 8, 0, 0);
            controls.MaxWidth = double.PositiveInfinity;
            controls.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            controls.IsVisible = true;
            header.IsVisible = true;
            root.Margin = new(20);
            SystemDecorations = SystemDecorations.Full;
            WindowState = previousState;
            if (previousState == WindowState.Normal)
            {
                Width = previousSize.Width;
                Height = previousSize.Height;
                Position = previousPosition;
            }

            Activate();
        }
    }
}
