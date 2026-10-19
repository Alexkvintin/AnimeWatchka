using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AnimeApp.UI;
public sealed partial class SettingsView : UserControl
{
    private async void ChooseDownloadFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AnimeApp.ViewModels.MainViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = "Место загрузки аниме", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].Path.IsFile) vm.Settings.CustomDownloadFolder = folders[0].Path.LocalPath;
    }
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}

