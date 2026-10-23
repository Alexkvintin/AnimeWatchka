using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AnimeApp.UI;
public sealed class PosterImage : Image
{
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<PosterImage, string?>(nameof(Url));
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    public static readonly StyledProperty<string?> FallbackUrlProperty = AvaloniaProperty.Register<PosterImage, string?>(nameof(FallbackUrl));
    public string? FallbackUrl { get => GetValue(FallbackUrlProperty); set => SetValue(FallbackUrlProperty, value); }

    private CancellationTokenSource? loading;
    private Bitmap? bitmap;
    private bool attached;
    public static AnimeApp.Services.IPosterLoader Loader { get; set; } = AnimeApp.Services.PosterService.Shared;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (attached && (change.Property == UrlProperty || change.Property == FallbackUrlProperty))
            _ = Load(Url);
    }

    private async Task Load(string? url)
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new();
        var ct = loading.Token;
        Source = null;
        bitmap?.Dispose();
        bitmap = null;
        foreach (var candidate in new[]
        {
            url,
            FallbackUrl
        }.Distinct())
        {
            var local=candidate is not null && Path.IsPathFullyQualified(candidate) && File.Exists(candidate) && new[]{".jpg",".jpeg",".png",".webp"}.Contains(Path.GetExtension(candidate).ToLowerInvariant());
            if (!local && (!AnimeApp.Services.PosterService.IsUsable(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out _)))
                continue;
            try
            {
                if(local && new FileInfo(candidate!).Length>4*1024*1024) continue;
                var bytes = local ? await File.ReadAllBytesAsync(candidate!,ct) : await Loader.Load(new Uri(candidate!), ct);
                ct.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, 320);
                Source = bitmap;
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)when (ex is HttpRequestException or ArgumentException or IOException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceWarning("Poster unavailable ({0})", ex.GetType().Name);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        loading?.Cancel();
        Source = null;
        bitmap?.Dispose();
        bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = true;
        base.OnAttachedToVisualTree(e);
        _ = Load(Url);
    }
}
