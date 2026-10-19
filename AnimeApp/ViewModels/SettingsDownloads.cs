using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeApp.Services.Playback;
namespace AnimeApp.ViewModels;
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool automaticDownloads = true;
    [ObservableProperty] private string customDownloadFolder = "";
    public string AutomaticDownloadFolder => new DownloadPreferences().Root;
    public string DownloadLocationMessage => "Auto: " + AutomaticDownloadFolder;
    public async Task SaveDownloadLocation()
    {
        var settings = new DownloadPreferences(AutomaticDownloads, CustomDownloadFolder.Trim());
        _ = settings.Root;
        await repository.SaveSetting("downloads", settings);
        await host.Sources.RefreshDownloads();
        host.Message = "Папка загрузки сохранена. Существующие файлы не перемещаются.";
    }
    [RelayCommand] private Task SaveDownloads() => host.Run(SaveDownloadLocation);
}
