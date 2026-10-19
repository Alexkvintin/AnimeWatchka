using AnimeApp.Services.Playback;
using Avalonia.Platform;
using LibVLCSharp.Avalonia;

namespace AnimeApp.UI;
public sealed class MpvVideoView(MpvPlayerEngine engine) : VideoView
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var child = base.CreateNativeControlCore(parent);
        try
        {
            engine.Attach(child.Handle);
        }
        catch (IOException)
        {
            engine.UseFallback();
            MediaPlayer = engine.Fallback!.Player;
        }

        return child;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        MediaPlayer = null;
        engine.DisposeNative();
        base.DestroyNativeControlCore(control);
    }
}
