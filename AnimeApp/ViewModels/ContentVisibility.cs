using AnimeApp.Models;

namespace AnimeApp.ViewModels;
public sealed partial class MainViewModel
{
    internal bool IsVisibleTitle(long id) => Settings.ShowAdultContent || library.Any(a => a.ShikimoriId == id);
    internal Task<IReadOnlyList<Anime>> FilterContent(IEnumerable<Anime> items, CancellationToken ct = default)
        => api.FilterContent(items, ct);
    internal async Task RefreshContentVisibility()
    {
        searchLoading?.Cancel();
        detailsLoading?.Cancel();
        navigation.Clear();
        library = [];
        Message = "";
        Items.Clear(); Rows.Clear(); History.Clear(); HistoryItems.Clear();
        Related.Clear(); Seasons.Clear(); Schedule.Items.Clear();
        SelectedAnime = null;
        Sources.Downloads.Clear();
        if (!Settings.ShowAdultContent) Sources.CancelDownloadCommand.Execute(null);
        Sources.DownloadStatus = "";
        Settings.RecoveryCandidates.Clear(); Settings.RecoveryMessage = "";
        Page = "Настройки";
        await Reload();
        await Sources.RefreshDownloads();
    }
}
