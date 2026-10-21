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

    private void StartSubtitleSearch()
    {
        subtitleSearch?.Cancel();
        subtitleSearch?.Dispose();
        subtitleSearch = new();
        OnlineSubtitles = [];
        SelectedOnlineSubtitle = null;
        _ = FindSubtitles(Request, subtitleSearch.Token);
    }
    private async Task FindSubtitles(PlaybackRequest request, CancellationToken ct)
    {
        if (SubtitleFinder is null) return;
        OnlineSubtitleStatus = "Ищем русские и украинские субтитры…";
        try
        {
            var found = await SubtitleFinder.Find(request.ShikimoriId, request.Title, request.Episode, ct);
            ct.ThrowIfCancellationRequested();
            populatingOnlineSubtitles = true;
            try { OnlineSubtitles = found; SelectedOnlineSubtitle = found.FirstOrDefault(); }
            finally { populatingOnlineSubtitles = false; }
            OnlineSubtitleStatus = found.Count == 0 ? "Для этой серии отдельные RU/UK субтитры не найдены." : $"Найдено переводов: {found.Count}.";
            // Mikai uses hard subtitles. Do not overlay a second translation on top of them.
            if (request.ProviderId == "subtitles")
                OnlineSubtitleStatus += " В этом видео украинский текст вшит; отдельный перевод наложится поверх него.";
            else if (SubtitleControls.Tracks(Engine).Count <= 1)
                foreach (var translation in found.Take(5))
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
            _ = ApplyOnlineSubtitle(value, search.Token);
    }
    [RelayCommand]
    private Task LoadOnlineSubtitle() => SelectedOnlineSubtitle is { } selected && subtitleSearch is { } search
        ? ApplyOnlineSubtitle(selected, search.Token) : Task.CompletedTask;

    private async Task<bool> ApplyOnlineSubtitle(OnlineSubtitle selected, CancellationToken ct)
    {
        if (SubtitleFinder is null) return false;
        var version = ++subtitleSelectionVersion;
        OnlineSubtitleStatus = "Подключаем субтитры…";
        try
        {
            var path = await SubtitleFinder.Download(selected, ct);
            ct.ThrowIfCancellationRequested();
            if (closed || version != subtitleSelectionVersion) return false;
            SubtitleControls.AddFile(Engine, path);
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

    public bool CanUseSubtitles => SubtitleControls.Supported(Engine);
    public string SubtitleHint => SubtitleTracks.Count > 1
        ? "Выберите язык из доступных дорожек. Положительная задержка показывает текст позже."
        : "Автоматически найденные переводы доступны выше. Вшитый в видео текст отключить или изменить нельзя.";

    partial void OnSelectedSubtitleChanged(SubtitleChoice? value)
    {
        if (!readingSubtitles && value is not null)
        {
            ++subtitleSelectionVersion;
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
            if (!SubtitleTracks.SequenceEqual(tracks)) SubtitleTracks = tracks;
            SelectedSubtitle = SubtitleTracks.FirstOrDefault(t => t.Id == SubtitleControls.Selected(Engine)) ?? SubtitleTracks[0];
            OnPropertyChanged(nameof(CanUseSubtitles));
            OnPropertyChanged(nameof(SubtitleHint));
            OnPropertyChanged(nameof(CanStyleSubtitles));
            OnPropertyChanged(nameof(SubtitleStyleHint));
        }
        finally { readingSubtitles = false; }
    }

    public void OpenSubtitleFile(string path)
    {
        SubtitleControls.AddFile(Engine, path);
        ShowSubtitles = true;
        RefreshSubtitles();
    }
}
