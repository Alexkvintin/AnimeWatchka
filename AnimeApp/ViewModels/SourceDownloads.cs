using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeApp.Services.Playback;
using System.Diagnostics;

namespace AnimeApp.ViewModels;
public sealed partial class SourcesViewModel
{
    private static readonly HttpClient DownloadHttp = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(2) };
    private CancellationTokenSource? downloadCancellation;
    [ObservableProperty] private bool downloading;
    [ObservableProperty] private string downloadStatus = "";
    [ObservableProperty] private double downloadPercent;
    [ObservableProperty] private bool downloadIndeterminate = true;
    [ObservableProperty] private string downloadQuality = "720p";
    public string[] DownloadQualities { get; } = ["480p", "720p", "1080p", "Auto"];
    public bool HasDownloadStatus => !string.IsNullOrEmpty(DownloadStatus);
    partial void OnDownloadStatusChanged(string value) => OnPropertyChanged(nameof(HasDownloadStatus));
    public string DownloadFolder { get; private set; } = "";
    [RelayCommand] private void CancelDownload() => downloadCancellation?.Cancel();
    [RelayCommand] private void OpenDownloadFolder()
    {
        if (Directory.Exists(DownloadFolder)) Process.Start(new ProcessStartInfo(DownloadFolder) { UseShellExecute = true });
    }
    public async Task DownloadSelected(string folder)
    {
        if (Downloading) return;
        var provider = SelectedProvider; var release = SelectedRelease; var episode = SelectedEpisode; var anime = host.SelectedAnime;
        if (provider is null || release is null || episode is null || anime is null) { DownloadStatus = "Выберите релиз и серию."; return; }
        DownloadFolder = folder; Downloading = true; DownloadPercent = 0; DownloadIndeterminate = true;
        using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation;
        var quality = DownloadQuality;
        var label = $"{anime.Title} · {provider.Name} · серия {episode.Number}";
        DownloadStatus = label + " · получение адреса…";
        var lastUpdate = DateTime.MinValue;
        var progress = new Progress<DownloadProgress>(p => {
            if (!Downloading || downloadCancellation != cancellation) return;
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 200 && p.Percent < 100) return;
            lastUpdate = DateTime.UtcNow; DownloadPercent = p.Percent; DownloadIndeterminate = p.Total is null && p.TotalParts == 0;
            DownloadStatus = label + " · " + p.Label;
        });
        try
        {
            await repository.SaveSetting("download-folder", folder);
            var source = await provider.Provider.GetPlayback(release.Id, episode, cancellation.Token);
            var result = await new EpisodeDownloader(DownloadHttp).Download(source, folder, label, quality, progress, cancellation.Token, episode.Number);
            DownloadPercent = 100; DownloadIndeterminate = false;
            DownloadStatus = label + " · сохранено. Открыть можно кнопкой «Открыть скачанное».";
        }
        catch (OperationCanceledException) { DownloadStatus = cancellation.IsCancellationRequested ? "Скачивание отменено; временные файлы удалены." : "Источник слишком долго не отвечает. Повторите скачивание."; }
        catch (Exception ex)
        { DownloadStatus = ex is InvalidDataException ? ex.Message : "Не удалось скачать серию. Проверьте сеть, свободное место и доступ к папке; повторная попытка начнёт загрузку заново."; }
        finally { Downloading = false; downloadCancellation = null; }
    }
    public async Task OpenDownloaded(string directory)
    {
        try
        {
            var result = DownloadedEpisodeProvider.Read(directory);
            await playback.Start(new(host.UserId, 0, result.Title, "downloads", directory, result.File, result.Episode, new(PlaybackKind.LocalFile, result.File), CountsTowardProgress: false));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        { DownloadStatus = "Это не завершённая загрузка AnimeWatchka или её файлы перемещены. Выберите папку самой серии."; }
    }
}
