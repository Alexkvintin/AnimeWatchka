using Avalonia.Controls;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace AnimeApp.UI;

// Frame decoding is bounded before allocation; the original file is kept for sharing.
internal sealed class CharacterFrames : IDisposable
{
    public List<Bitmap> Frames { get; } = [];
    public List<int> Delays { get; } = [];
    public static CharacterFrames Open(string path)
    {
        if (new FileInfo(path).Length > 24 * 1024 * 1024) throw new InvalidDataException("Файл персонажа больше 24 МБ.");
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("Не удалось прочитать персонажа.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Gif)) throw new InvalidDataException("Выберите PNG или GIF.");
        var info = codec.Info;
        int count = Math.Max(1, codec.FrameCount);
        if (info.Width > 4096 || info.Height > 4096 || (long)info.Width * info.Height * count > 80_000_000 || count > 300)
            throw new InvalidDataException("Изображение слишком большое: уменьшите размер или число кадров GIF.");
        var result = new CharacterFrames();
        try
        {
            using var pixels = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var frames = codec.FrameInfo;
            for (var i = 0; i < count; i++)
            {
                var status = codec.GetPixels(pixels.Info, pixels.GetPixels(), new SKCodecOptions(i, -1));
                if (status != SKCodecResult.Success) throw new InvalidDataException("Повреждённое изображение персонажа.");
                using var image = SKImage.FromBitmap(pixels);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = data.AsStream();
                result.Frames.Add(Bitmap.DecodeToWidth(stream, Math.Min(320, info.Width)));
                result.Delays.Add(frames.Length > i ? Math.Max(40, frames[i].Duration) : 100);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose() { foreach (var frame in Frames) frame.Dispose(); Frames.Clear(); }
}

public sealed class CharacterImage : Image
{
    public static readonly StyledProperty<string> CharacterPathProperty = AvaloniaProperty.Register<CharacterImage, string>(nameof(CharacterPath), "");
    public static readonly StyledProperty<Avalonia.Media.IImage?> FallbackProperty = AvaloniaProperty.Register<CharacterImage, Avalonia.Media.IImage?>(nameof(Fallback));
    public string CharacterPath { get => GetValue(CharacterPathProperty); set => SetValue(CharacterPathProperty, value); }
    public Avalonia.Media.IImage? Fallback { get => GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }
    private CharacterFrames? frames;
    private readonly DispatcherTimer timer = new();
    private int index;
    private bool attached;
    public CharacterImage()
    {
        timer.Tick += (_, _) => {
            if (frames is null || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) is Window { IsActive: false }) return;
            index = (index + 1) % frames.Frames.Count; Source = frames.Frames[index];
            timer.Interval = TimeSpan.FromMilliseconds(frames.Delays[index]);
        };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CharacterPathProperty || change.Property == FallbackProperty) Reload();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); attached = true; Reload(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { attached = false; timer.Stop(); Source = null; frames?.Dispose(); frames = null; base.OnDetachedFromVisualTree(e); }
    private void Reload()
    {
        if (!attached) return;
        timer.Stop(); Source = null; frames?.Dispose(); frames = null; index = 0;
        try { if (!string.IsNullOrEmpty(CharacterPath)) frames = CharacterFrames.Open(CharacterPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
        Source = frames?.Frames[0] ?? Fallback;
        if (frames?.Frames.Count > 1) { timer.Interval = TimeSpan.FromMilliseconds(frames.Delays[0]); timer.Start(); }
    }
}
