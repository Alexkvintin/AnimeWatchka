using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace AnimeApp.UI;
public sealed partial class DetailsView : UserControl
{
    private async void OpenTorrent(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AnimeApp.ViewModels.MainViewModel vm || TopLevel.GetTopLevel(this) is not {} top) return;
        try {
            var files=await top.StorageProvider.OpenFilePickerAsync(new() { Title="Открыть торрент", AllowMultiple=false, FileTypeFilter=[new("Torrent") { Patterns=["*.torrent"] }] });
            if(files.Count>0 && files[0].Path.IsFile) await vm.Sources.ImportTorrentFile(files[0].Path.LocalPath);
        } catch(Exception ex) { vm.Sources.SourceMessage=ex.Message; }
    }
    public DetailsView() => AvaloniaXamlLoader.Load(this);
}
