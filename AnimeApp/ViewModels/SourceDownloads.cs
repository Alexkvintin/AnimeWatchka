using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeApp.Services.Playback;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AnimeApp.ViewModels;
public sealed partial class SourcesViewModel
{
    private static readonly HttpClient DownloadHttp = new(new HttpClientHandler { AllowAutoRedirect = true, MaxConnectionsPerServer = 4 }) { Timeout = TimeSpan.FromMinutes(2) };
    private CancellationTokenSource? downloadCancellation;
    private IReadOnlyList<DownloadEntry> savedEpisodes = [];
    [ObservableProperty] private bool downloading;
    [ObservableProperty] private string downloadStatus = "";
    [ObservableProperty] private double downloadPercent;
    [ObservableProperty] private bool downloadIndeterminate = true;
    [ObservableProperty] private string downloadQuality = "720p";
    [ObservableProperty] private bool downloadsVisible;
    public ObservableCollection<DownloadEntry> Downloads { get; } = [];
    public string[] DownloadQualities { get; } = ["480p", "720p", "1080p", "Auto"];
    public bool HasDownloadStatus => !string.IsNullOrEmpty(DownloadStatus);
    partial void OnDownloadStatusChanged(string value) => OnPropertyChanged(nameof(HasDownloadStatus));
    public string DownloadFolder { get; private set; } = "";
    [RelayCommand] private void CancelDownload() => downloadCancellation?.Cancel();
    [RelayCommand] private void CloseDownloads() => DownloadsVisible = false;
    private async Task<string> DownloadRoot() => (await repository.Setting<DownloadPreferences>("downloads") ?? new()).Root;
    public async Task RefreshDownloads()
    {
        DownloadFolder = await DownloadRoot();
        savedEpisodes = await Task.Run(() => DownloadLibrary.Read(DownloadFolder));
        Downloads.Clear();
        var permitted = (await host.FilterContent(savedEpisodes.Select(e => new AnimeApp.Models.Anime { ShikimoriId = e.Data.Identity?.AnimeId ?? 0 }))).Select(a => a.ShikimoriId).ToHashSet();
        foreach (var entry in savedEpisodes.Where(e => permitted.Contains(e.Data.Identity?.AnimeId ?? 0)).OrderBy(e => e.Data.Title).ThenBy(e => e.Data.Episode)) Downloads.Add(entry);
        RefreshTiles();
    }
    [RelayCommand] private Task ShowDownloads() => host.Run(async () => { await RefreshDownloads(); DownloadsVisible = true; });
    [RelayCommand] private Task OpenDownloadFolder() => host.Run(async () =>
    {
        DownloadFolder = await DownloadRoot(); Directory.CreateDirectory(DownloadFolder);
        Process.Start(new ProcessStartInfo(DownloadFolder) { UseShellExecute = true });
    });
    [RelayCommand] private Task PlayDownloaded(DownloadEntry? entry) => entry is null ? Task.CompletedTask : OpenDownloaded(entry.Directory);
    [RelayCommand] private Task DownloadEpisode() => DownloadSelection(false);
    [RelayCommand] private Task DownloadSeason() => DownloadSelection(true);
    private async Task DownloadSelection(bool season)
    {
        if (Downloading) return;
        var provider = SelectedProvider; var release = SelectedRelease; var anime = host.SelectedAnime;
        var episodes = season ? Episodes.Where(e => e.Available).ToArray() : SelectedEpisode is { Available: true } selected ? [selected] : [];
        if (provider is null || release is null || anime is null || episodes.Length == 0) { DownloadStatus = "Выберите релиз с доступными сериями."; return; }
        Downloading = true; DownloadPercent = 0; DownloadIndeterminate = true;
        using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation;
        var label = $"{anime.Title} · {provider.Name}";
        DownloadStatus = label + " · подготовка…";
        var lastUpdate = DateTime.MinValue;
        var progress = new Progress<SeasonProgress>(p => {
            if (!Downloading || downloadCancellation != cancellation || cancellation.IsCancellationRequested) return;
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < 150 && p.Transfer is not null) return;
            lastUpdate = DateTime.UtcNow;
            DownloadPercent = Math.Min(100, (p.Done + (p.Transfer?.Percent ?? 0) / 100) * 100 / p.Total);
            DownloadIndeterminate = false;
            DownloadStatus = $"{label} · готово {p.Done}/{p.Total} · серия {p.Episode}" + (p.Transfer is { } transfer ? " · " + transfer.Label : "");
        });
        try
        {
            DownloadFolder = await DownloadRoot(); Directory.CreateDirectory(DownloadFolder);
            var probe = Path.Combine(DownloadFolder, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var result = await new SeasonDownloader(DownloadHttp).Download(provider.Provider, release, episodes, DownloadFolder, anime.Title,
                anime.ShikimoriId, DownloadQuality, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DownloadPercent = 100; DownloadIndeterminate = false;
            DownloadStatus = $"{label} · скачано: {result.Downloaded}; уже на диске: {result.Skipped}." +
                (result.Failed.Count > 0 ? " Не удалось скачать серии: " + string.Join(", ", result.Failed) + ". Повторите загрузку — готовые серии будут пропущены." : " Готово.");
        }
        catch (OperationCanceledException) { DownloadStatus = "Скачивание отменено. Готовые серии сохранены, недокачанная серия удалена."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { DownloadStatus = "Не удалось записать файлы. Проверьте свободное место и выберите доступную папку в настройках → Место загрузки аниме."; }
        finally
        {
            Downloading = false; downloadCancellation = null;
            try { await RefreshDownloads(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public async Task OpenDownloaded(string directory)
    {
        try
        {
            var result = DownloadedEpisodeProvider.Read(directory);
            await playback.Start(new(host.UserId, 0, result.Title, "downloads", directory, result.File, result.Episode, new(PlaybackKind.LocalFile, result.File), CountsTowardProgress: false));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        { DownloadStatus = "Загрузка не завершена или файлы перемещены. Обновите раздел «Скачанное»."; }
    }
}
