using AnimeApp.Services.Torrents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AnimeApp.ViewModels;
public sealed partial class SourcesViewModel
{
    private readonly AnimeApp.Services.Playback.OpenSubtitlesService openSubtitleSettings = new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(10) },
        new AnimeApp.Services.Shikimori.ProtectedFile<AnimeApp.Services.Playback.OpenSubtitleCredentials>(AnimeApp.Services.Playback.OpenSubtitlesService.CredentialsPath));
    [ObservableProperty] private string openSubtitleKey = "";
    [ObservableProperty] private string openSubtitleUser = "";
    [ObservableProperty] private string openSubtitlePassword = "";
    [ObservableProperty] private string openSubtitleConnection = "Anime365 работает без входа. OpenSubtitles — дополнительный источник.";
    [RelayCommand] private async Task ConnectOpenSubtitles() {
        try { await openSubtitleSettings.Login(OpenSubtitleKey,OpenSubtitleUser,OpenSubtitlePassword); OpenSubtitleConnection="OpenSubtitles подключён. Ключ и токен защищены Windows."; }
        catch { OpenSubtitleConnection="Не удалось подключить OpenSubtitles. Проверьте API-ключ, данные входа и доступность сервиса."; }
        finally { OpenSubtitleKey=""; OpenSubtitlePassword=""; }
    }
    [RelayCommand] private async Task DisconnectOpenSubtitles() { await openSubtitleSettings.Clear(); OpenSubtitleConnection="OpenSubtitles отключён. Anime365 остаётся доступен."; }
    private CancellationTokenSource? torrentCancellation;
    [RelayCommand] private void CancelTorrentRequest() => torrentCancellation?.Cancel();
    public bool IsTorrent => SelectedProvider?.Id == "torrent-subtitles";
    [ObservableProperty] private string torrentInput = "";
    [ObservableProperty] private string torrentSourceName = "";
    [ObservableProperty] private string torrentSourceUrl = "";
    [ObservableProperty] private TorrentSearchResult? selectedTorrent;
    [ObservableProperty] private TorrentSearchSource? selectedTorrentSource;
    [ObservableProperty] private int torrentDownloadKiB;
    [ObservableProperty] private int torrentUploadKiB = 256;
    [ObservableProperty] private bool torrentBusy;
    public ObservableCollection<TorrentSearchResult> TorrentResults { get; } = [];
    public ObservableCollection<TorrentSearchSource> TorrentSources { get; } = [];
    private TorrentProvider? Torrent => SelectedProvider?.Provider as TorrentProvider;
    private async Task LoadTorrentSettings(CancellationToken ct) {
        if (Torrent is {} provider) await provider.EnsureDefaultCatalogs(ct);
        TorrentSources.Clear();
        foreach(var source in await repository.Setting<List<TorrentSearchSource>>("torrent-search-sources",ct) ?? []) TorrentSources.Add(source);
        var options=await repository.Setting<TorrentOptions>("torrent-options",ct) ?? new();
        TorrentDownloadKiB=options.DownloadKiB; TorrentUploadKiB=options.UploadKiB;
    }
    [RelayCommand] private async Task SearchTorrents() {
        if(Torrent is not {} provider) return;
        torrentCancellation?.Cancel();
        var request = new CancellationTokenSource();
        torrentCancellation = request;
        var identity = host.SelectedAnime?.Identity;

        bool Current() => torrentCancellation == request && Torrent == provider && host.SelectedAnime?.Identity == identity;
        TorrentBusy=true; TorrentResults.Clear(); SelectedTorrent=null; SourceMessage="Ищем оригинальное видео. Перевод на языке системы подберётся в плеере…";
        try {
            var names = host.SelectedAnime is {} anime ? TorrentProvider.SearchNames(anime) : [];
            IEnumerable<string> queries = names.Contains(SourceQuery, StringComparer.OrdinalIgnoreCase) ? names : new[] { SourceQuery };
            if(names.Contains(SourceQuery,StringComparer.OrdinalIgnoreCase) && names.Count>0 && host.SelectedAnime?.Kind is "tv" or "ona" or "ova")
                queries=new[]{names[0]+" - "+((host.SelectedAnime?.EpisodesWatched??0)+1).ToString("00",System.Globalization.CultureInfo.InvariantCulture)}.Concat(names.Take(3));
            var results = await provider.FindRussian(queries, request.Token, includeOriginalAudio: true);
            request.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            foreach(var result in results) TorrentResults.Add(result);
            SelectedTorrent=TorrentResults.FirstOrDefault();
            var catalog=await provider.SaveCatalog(identity?.ToString() ?? SourceQuery,host.SelectedAnime?.Title ?? SourceQuery,results,request.Token,host.SelectedAnime?.EpisodesTotal ?? 0,names);
            request.Token.ThrowIfCancellationRequested();
            if(!Current()) return;
            episodeCache.Remove((provider.Id,catalog.Id));
            await UseRelease(provider.Id,catalog,request.Token);
            if(Current()) SourceMessage=Episodes.Count==0 ? "Серии пока не найдены. Ручной выбор доступен в «Дополнительно»." : "Выберите серию. Видео и перевод на языке системы подберутся автоматически.";
        }
        catch(Exception ex) { if(Current()) SourceMessage=ex is OperationCanceledException ? "Поиск отменён." : ex.Message; }
        finally { if(torrentCancellation == request) { TorrentBusy=false; torrentCancellation=null; } request.Dispose(); }

    }
    [RelayCommand] private Task OpenTorrentResult() { if(SelectedTorrent is {} result) { TorrentInput=result.Magnet; return ImportTorrent(result.Title,result); } return Task.CompletedTask; }
    [RelayCommand] private Task AddTorrent() => ImportTorrent(host.SelectedAnime?.Title ?? "Торрент");
    public async Task ImportTorrentFile(string path) { TorrentInput=path; await AddTorrent(); }
    private async Task ImportTorrent(string title, TorrentSearchResult? result=null) {
        if(Torrent is not {} provider || TorrentBusy) return;
        torrentCancellation?.Cancel();
        var request=new CancellationTokenSource(); torrentCancellation=request;
        var identity=host.SelectedAnime?.Identity;
        bool Current() => torrentCancellation==request && Torrent==provider && host.SelectedAnime?.Identity==identity;
        TorrentBusy=true; SourceMessage="Получаем файлы раздачи. Для magnet нужны доступные участники…";
        try {
            var release=result is null ? await provider.Import(TorrentInput,title,request.Token) : await provider.ImportResult(result,request.Token);
            request.Token.ThrowIfCancellationRequested();
            if(!Current()) return;
            await UseRelease(provider.Id,release,request.Token);
            request.Token.ThrowIfCancellationRequested();
            if(Current()) SourceMessage=$"Серий в раздаче: {Episodes.Count}. Проверьте названия файлов перед просмотром.";
        }
        catch(Exception ex) { if(Current()) SourceMessage=ex is OperationCanceledException ? "Получение файлов отменено." : ex.Message; }
        finally { if(torrentCancellation==request) { TorrentBusy=false; torrentCancellation=null; } request.Dispose(); }
    }
    [RelayCommand] private async Task SaveTorrentSource() {
        try { var source=new TorrentSearchSource(TorrentSourceName.Trim(),TorrentSourceUrl.Trim()); TorrentProvider.ValidateSource(source); if(TorrentSources.Count>=20) throw new IOException("Можно добавить до 20 источников."); TorrentSources.Add(source); await SaveTorrentSources(); SourceMessage="Источник добавлен."; }
        catch(Exception ex) { SourceMessage=ex.Message; }
    }
    [RelayCommand] private async Task ImportTorrentSources() {
        try {
            var imported=await TorrentProvider.ReadManifest(TorrentSourceUrl);
            if(TorrentSources.Count+imported.Count>20) throw new IOException("Можно добавить до 20 источников.");
            foreach(var source in imported) if(!TorrentSources.Any(s=>s.SearchUrl==source.SearchUrl)) TorrentSources.Add(source);
            await SaveTorrentSources(); SourceMessage="Манифест импортирован. Выберите и включите нужные источники.";
        } catch(Exception ex) { SourceMessage=ex.Message; }
    }
    [RelayCommand] private async Task RemoveTorrentSource() { if(SelectedTorrentSource is {} source) { TorrentSources.Remove(source); await SaveTorrentSources(); } }
    [RelayCommand] private async Task ToggleTorrentSource() { if(SelectedTorrentSource is {} source) { var index=TorrentSources.IndexOf(source); TorrentSources[index]=source with { Enabled=!source.Enabled }; SelectedTorrentSource=TorrentSources[index]; await SaveTorrentSources(); } }
    private Task SaveTorrentSources() => repository.SaveSetting("torrent-search-sources",TorrentSources.ToList());
    [RelayCommand] private async Task SaveTorrentLimits() { await repository.SaveSetting("torrent-options",new TorrentOptions(Math.Clamp(TorrentDownloadKiB,0,1000000),Math.Clamp(TorrentUploadKiB,1,1000000))); SourceMessage="Ограничения применятся при следующем открытии раздачи. 0 для загрузки — без ограничения."; }
    [RelayCommand] private async Task StopTorrent() { torrentCancellation?.Cancel(); try { if(Torrent is {} provider) { await provider.Stop(); SourceMessage="Торрент остановлен. Скачанные фрагменты сохранены."; } } catch(Exception ex) { SourceMessage=ex.Message; } }
    [RelayCommand] private async Task ClearTorrentCache() { try { if(Torrent is {} provider) { await provider.Clear(); SourceMessage="Временный кэш очищен."; } } catch(Exception ex) { SourceMessage=ex.Message; } }
}
