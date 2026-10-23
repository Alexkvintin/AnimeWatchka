using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeApp.ViewModels;
public sealed record DownloadGroup(string Title,string Poster,string Voice,IReadOnlyList<DownloadTile> Episodes);
public sealed partial class DownloadTile : ObservableObject
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Poster { get; init; } = "";
    public string Voice { get; init; } = "";
    public string Key { get; init; } = "";
    [ObservableProperty] private DownloadEntry? entry;
    [ObservableProperty] private bool active;
    [ObservableProperty] private double percent;
    public bool Ready => Entry is not null;
    partial void OnEntryChanged(DownloadEntry? value) => OnPropertyChanged(nameof(Ready));
}
