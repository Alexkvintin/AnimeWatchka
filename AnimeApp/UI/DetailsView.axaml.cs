using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AnimeApp.UI;
public sealed partial class DetailsView : UserControl
{
    private async void DownloadEpisode(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AnimeApp.ViewModels.MainViewModel vm || TopLevel.GetTopLevel(this) is not { } top || vm.Sources.Downloading) return;
        if (vm.Sources.SelectedEpisode is null) { vm.Sources.DownloadStatus = "Выберите серию для скачивания."; return; }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = "Куда сохранить серию", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].Path.IsFile) await vm.Sources.DownloadSelected(folders[0].Path.LocalPath);
    }
    public DetailsView() => AvaloniaXamlLoader.Load(this);
}
