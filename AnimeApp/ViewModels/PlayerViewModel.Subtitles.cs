using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeApp.ViewModels;

public sealed partial class PlayerViewModel
{
    [ObservableProperty] private bool showSubtitles;
    [ObservableProperty] private IReadOnlyList<SubtitleChoice> subtitleTracks = [new(-1, "Выключены")];
    [ObservableProperty] private SubtitleChoice? selectedSubtitle;
    [ObservableProperty] private double subtitleDelay;
    private bool readingSubtitles;

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
