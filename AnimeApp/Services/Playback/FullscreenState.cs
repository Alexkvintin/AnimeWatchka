namespace AnimeApp.Services.Playback;
public sealed class FullscreenState
{
    public bool Active { get; private set; }

    private DateTimeOffset interaction;
    private (double X, double Y)? pointer;
    public void PointerMoved(double x, double y, bool nearControls, DateTimeOffset now)
    {
        var previous = pointer;
        pointer = (x, y);
        if (previous is not null && x == previous.Value.X && y == previous.Value.Y) return;
        Interact(now);
    }
    public void Enter(DateTimeOffset now)
    {
        Active = true;
        Interact(now);
    }

    public void Exit() => Active = false;
    public void Interact(DateTimeOffset now) => interaction = now;
    public bool ControlsVisible(DateTimeOffset now) => !Active || now - interaction < TimeSpan.FromSeconds(7.5);
}

public record ScalingMode(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class VideoScaling
{
    public static ScalingMode[] Modes { get; } = [new("linear", "Обычное"), new("hq", "Высокое качество · mpv"), new("anime", "Anime Enhanced · Anime4K"), new("super", "Driver Super Resolution · если поддерживается"), new("processor", "Video Processor · VLC")];

    public static string[] Options(string mode) => ["--vout=direct3d11", "--d3d11-upscale-mode=" + (mode is "linear" or "processor" or "super" ? mode : "linear")];
}
