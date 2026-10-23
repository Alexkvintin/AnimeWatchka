using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeApp.ViewModels;

public sealed partial class PlayerViewModel
{
    public AutomaticSubtitles? SubtitleFinder { get; init; }
    private CancellationTokenSource? subtitleSearch;
    [ObservableProperty] private IReadOnlyList<OnlineSubtitle> onlineSubtitles = [];
    [ObservableProperty] private OnlineSubtitle? selectedOnlineSubtitle;
    [ObservableProperty] private string onlineSubtitleStatus = "";
    private bool populatingOnlineSubtitles;
    private long subtitleSelectionVersion;
    private bool subtitlesDisabledByUser;
    private string? preferredSubtitleAuthor;
    private string? preferredSubtitleService;
    private string? preferredEmbeddedSubtitle;
    private bool restoreSubtitleSelection;
    private static bool PreferredLanguageTrack(SubtitleChoice track) => MediaLanguage.MatchesTrack(track.Label);

    private void StartSubtitleSearch()
    {
        subtitleSearch?.Cancel();
        subtitleSearch?.Dispose();
        subtitleSearch = new();
        ++subtitleSelectionVersion;
        restoreSubtitleSelection = true;
        OnlineSubtitles = [];
        populatingOnlineSubtitles=true;
        SelectedOnlineSubtitle = null;
        populatingOnlineSubtitles=false;
        _ = FindSubtitles(Request, subtitleSearch.Token);
    }
    private async Task FindSubtitles(PlaybackRequest request, CancellationToken ct)
    {
        if (HasBurnedSubtitles)
        {
            OnlineSubtitleStatus = "В этом релизе текст вшит в видео. Второй перевод отключён, чтобы субтитры не перекрывались.";
            return;
        }

        if (SubtitleFinder is null) return;
        OnlineSubtitleStatus = "Ищем русские, украинские и английские субтитры…";
        try
        {
            var found = await SubtitleFinder.Find(request.ShikimoriId, request.Title, request.Episode, ct);
            ct.ThrowIfCancellationRequested();
            populatingOnlineSubtitles = true;
            try { OnlineSubtitles = found; SelectedOnlineSubtitle = found.FirstOrDefault(s=>s.Author==preferredSubtitleAuthor && s.Service==preferredSubtitleService) ?? found.FirstOrDefault(s=>s.Language==MediaLanguage.Default) ?? found.FirstOrDefault(); }
            finally { populatingOnlineSubtitles = false; }
            OnlineSubtitleStatus = found.Count == 0 ? "Для этой серии отдельные RU/UK/EN субтитры не найдены." : $"Найдено переводов: {found.Count}.";
            var candidates=found.Where(s=>!request.Source.IsTorrentStream || s.Language==MediaLanguage.Default || preferredSubtitleAuthor is not null && s.Author==preferredSubtitleAuthor && s.Service==preferredSubtitleService).OrderByDescending(s=>s==SelectedOnlineSubtitle).ToArray();
            // At most one automatic OpenSubtitles download per episode: it consumes an account quota.
            var attempts=candidates.Where(s=>s.Service!="OpenSubtitles").Take(4)
                .Concat(candidates.Where(s=>s.Service=="OpenSubtitles").Take(1)).OrderByDescending(s=>s==SelectedOnlineSubtitle);
            if (!subtitlesDisabledByUser && (request.Source.IsTorrentStream ? (preferredSubtitleAuthor is not null || !SubtitleControls.Tracks(Engine).Any(PreferredLanguageTrack)) && preferredEmbeddedSubtitle is null : SubtitleControls.Tracks(Engine).Count <= 1))
                foreach (var translation in attempts)
                {
                    populatingOnlineSubtitles = true;
                    try { SelectedOnlineSubtitle = translation; }
                    finally { populatingOnlineSubtitles = false; }
                    var expectedVersion = subtitleSelectionVersion + 1;
                    if (await ApplyOnlineSubtitle(translation, ct) || subtitleSelectionVersion != expectedVersion) break;
                    ct.ThrowIfCancellationRequested();
                }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) OnlineSubtitleStatus = "Каталог субтитров недоступен. Видео продолжает работать."; }
    }
    partial void OnSelectedOnlineSubtitleChanged(OnlineSubtitle? value)
    {
        if (!populatingOnlineSubtitles && value is not null && subtitleSearch is { } search)
        {
            preferredSubtitleAuthor=value.Author; preferredSubtitleService=value.Service;
            _ = SaveSubtitleLanguage(value.Language);
            preferredEmbeddedSubtitle=null;
            subtitlesDisabledByUser=false;
            _ = ApplyOnlineSubtitle(value, search.Token);
        }
    }
    private async Task SaveSubtitleLanguage(string language) {
        try { await repository.SaveSetting("subtitle-language",language); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }
    [RelayCommand]
    private Task LoadOnlineSubtitle() => SelectedOnlineSubtitle is { } selected && subtitleSearch is { } search
        ? ApplyOnlineSubtitle(selected, search.Token) : Task.CompletedTask;

    private async Task<bool> ApplyOnlineSubtitle(OnlineSubtitle selected, CancellationToken ct)
    {
        if (SubtitleFinder is null || HasBurnedSubtitles) return false;
        var version = ++subtitleSelectionVersion;
        OnlineSubtitleStatus = "Подключаем субтитры…";
        try
        {
            var path = await SubtitleFinder.Download(selected, ct);
            ct.ThrowIfCancellationRequested();
            if (closed || version != subtitleSelectionVersion) return false;
            if(Request.Source.IsTorrentStream) {
                for(var wait=0; wait<150 && Engine.Snapshot.Duration<=0; wait++) await Task.Delay(100,ct);
                ct.ThrowIfCancellationRequested();
                if(closed || version!=subtitleSelectionVersion) return false;
                if(Engine.Snapshot.Duration<=0) throw new IOException("Видео ещё буферизуется. Выберите перевод после начала воспроизведения.");
            }
            restoreSubtitleSelection=false;
            SubtitleControls.AddFile(Engine, path);
            ApplySubtitleStyle();
            SubtitleControls.Delay(Engine,SubtitleDelay);
            RefreshSubtitles();
            OnlineSubtitleStatus = $"Подключены: {selected}. Если текст опережает видео, измените задержку.";
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        { if (!ct.IsCancellationRequested && version == subtitleSelectionVersion) OnlineSubtitleStatus = "Не удалось загрузить перевод. Попробуйте выбрать другой."; }
        return false;
    }
    [ObservableProperty] private bool showSubtitles;
    [ObservableProperty] private bool subtitlesEnabled;
    private int lastSubtitleId = -1;
    public bool HasBurnedSubtitles => Request.ProviderId == "subtitles";
    partial void OnSubtitlesEnabledChanged(bool value)
    {
        if (readingSubtitles) return;
        subtitlesDisabledByUser = !value;
        if (!value)
        {
            lastSubtitleId = SubtitleControls.Selected(Engine);
            ++subtitleSelectionVersion;
            SubtitleControls.Select(Engine, -1);
        }
        else if (!HasBurnedSubtitles)
        {
            var track = SubtitleTracks.FirstOrDefault(t => t.Id == lastSubtitleId && t.Id >= 0)
                ?? SubtitleTracks.FirstOrDefault(t => t.Id >= 0);
            if (track is not null) SubtitleControls.Select(Engine, track.Id);
            else if (SelectedOnlineSubtitle is not null) LoadOnlineSubtitleCommand.Execute(null);
            else { ShowOptions = true; OnlineSubtitleStatus = "Ищем доступные переводы. Можно выбрать свой файл."; }
        }
    }
    [ObservableProperty] private IReadOnlyList<SubtitleChoice> subtitleTracks = [new(-1, "Выключены")];
    [ObservableProperty] private SubtitleChoice? selectedSubtitle;
    [ObservableProperty] private double subtitleDelay;
    private bool readingSubtitles;
    private readonly SubtitleFonts fontStore = new();
    [ObservableProperty] private IReadOnlyList<SubtitleFont> subtitleFontChoices = [];
    [ObservableProperty] private SubtitleFont? selectedSubtitleFont;
    [ObservableProperty] private double subtitleSize = 40;
    [ObservableProperty] private bool preserveSubtitleStyles = true;
    [ObservableProperty] private string subtitleStyleStatus = "";
    public bool CanStyleSubtitles => SubtitleControls.CanStyle(Engine);
    public string SubtitleStyleHint => CanStyleSubtitles
        ? "Размер и шрифт меняют отдельный текст. Для ASS можно сохранить оформление автора."
        : "В режиме совместимости VLC выбор шрифта недоступен. Дорожки и задержка работают.";

    private async Task LoadSubtitleStyle()
    {
        SubtitleFontChoices = fontStore.List();
        var style = await repository.Setting<SubtitleStyle>("subtitle-style") ?? new();
        SelectedSubtitleFont = SubtitleFontChoices.FirstOrDefault(f => f.Id == style.FontId) ?? SubtitleFontChoices[0];
        SubtitleSize = double.IsFinite(style.Size) ? Math.Clamp(style.Size, 16, 80) : 40;
        PreserveSubtitleStyles = style.PreserveAss;
    }
    private void ApplySubtitleStyle()
    {
        if (SelectedSubtitleFont is { } font)
            SubtitleControls.Style(Engine, font, SubtitleSize, PreserveSubtitleStyles);
    }
    [RelayCommand]
    private async Task SaveSubtitleStyle()
    {
        try
        {
            await PersistSubtitleStyle();
            SubtitleStyleStatus = "Оформление субтитров сохранено.";
        }
        catch (Exception ex) { ReportFailure(ex); }
    }
    private async Task PersistSubtitleStyle()
    {
        ApplySubtitleStyle();
        await repository.SaveSetting("subtitle-style", new SubtitleStyle(SelectedSubtitleFont?.Id ?? "standard", SubtitleSize, PreserveSubtitleStyles));
    }
    public async Task ImportSubtitleFont(string path)
    {
        var font = fontStore.Import(path);
        SubtitleFontChoices = fontStore.List();
        SelectedSubtitleFont = SubtitleFontChoices.First(f => f.Id == font.Id);
        await PersistSubtitleStyle();
        SubtitleStyleStatus = "Шрифт сохранён. Заново откройте плеер, чтобы загрузить новый файл шрифта.";
    }

    public bool CanUseSubtitles => !HasBurnedSubtitles && SubtitleControls.Supported(Engine);
    public string SubtitleHint => SubtitleTracks.Count > 1
        ? "Выберите язык из доступных дорожек. Положительная задержка показывает текст позже."
        : "Автоматически найденные переводы доступны выше. Вшитый в видео текст отключить или изменить нельзя.";

    partial void OnSelectedSubtitleChanged(SubtitleChoice? value)
    {
        if (!readingSubtitles && value is not null)
        {
            ++subtitleSelectionVersion;
            subtitlesDisabledByUser = value.Id < 0;
            preferredEmbeddedSubtitle = value.Id >= 0 ? value.Label : null;
            restoreSubtitleSelection = false;
            SubtitleControls.Select(Engine, value.Id);
        }
    }
    partial void OnSubtitleDelayChanged(double value) => SubtitleControls.Delay(Engine, value);

    private void RefreshSubtitles()
    {
        readingSubtitles = true;
        try
        {
            var tracks = SubtitleControls.Tracks(Engine);
            if(restoreSubtitleSelection && tracks.Count>1) {
                var preferred=tracks.FirstOrDefault(t=>t.Id>=0 && t.Label==preferredEmbeddedSubtitle)
                    ?? (Request.Source.IsTorrentStream ? tracks.FirstOrDefault(PreferredLanguageTrack) : null);
                if(subtitlesDisabledByUser) SubtitleControls.Select(Engine,-1);
                else if(preferred is not null) SubtitleControls.Select(Engine,preferred.Id);
                restoreSubtitleSelection=false;
            }
            if (!SubtitleTracks.SequenceEqual(tracks)) SubtitleTracks = tracks;
            SelectedSubtitle = SubtitleTracks.FirstOrDefault(t => t.Id == SubtitleControls.Selected(Engine)) ?? SubtitleTracks[0];
            SubtitlesEnabled = SelectedSubtitle.Id >= 0;
            OnPropertyChanged(nameof(CanUseSubtitles));
            OnPropertyChanged(nameof(SubtitleHint));
            OnPropertyChanged(nameof(CanStyleSubtitles));
            OnPropertyChanged(nameof(SubtitleStyleHint));
        }
        finally { readingSubtitles = false; }
    }

    public void OpenSubtitleFile(string path)
    {
        if (HasBurnedSubtitles) throw new IOException("В этом видео уже вшиты субтитры. Наложение второго текста отключено.");
        SubtitleControls.AddFile(Engine, path);
        ShowOptions = true;
        RefreshSubtitles();
    }
}
