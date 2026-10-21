using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeApp.ViewModels;

public sealed partial class PlayerViewModel
{
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
        : "Отдельных дорожек нет. Можно открыть файл субтитров. Вшитый в видео текст отключить или изменить нельзя.";

    partial void OnSelectedSubtitleChanged(SubtitleChoice? value)
    {
        if (!readingSubtitles && value is not null)
            SubtitleControls.Select(Engine, value.Id);
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
