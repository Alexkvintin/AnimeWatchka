using CommunityToolkit.Mvvm.ComponentModel;
namespace AnimeApp.ViewModels;
public sealed partial class MainViewModel
{
    public string[] LibrarySorts { get; } = ["Недавно просмотренные", "Недавно добавленные", "По названию", "По моей оценке"];
    [ObservableProperty] private string librarySort = "Недавно просмотренные";
    private readonly SemaphoreSlim sortSaveGate = new(1, 1);
    partial void OnLibrarySortChanged(string value)
    {
        if (ShowLibraryYear) ApplyFilter();
        _ = PersistLibrarySort();
    }
    private async Task PersistLibrarySort()
    {
        await sortSaveGate.WaitAsync();
        try { await repository.SaveSetting("library-sort", LibrarySort); }
        catch (Exception ex) { Message = "Не удалось сохранить сортировку: " + ex.Message; }
        finally { sortSaveGate.Release(); }
    }
}
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool closeToTray;
    private readonly SemaphoreSlim traySaveGate = new(1, 1);
    partial void OnCloseToTrayChanged(bool value) => _ = PersistTray();
    private async Task PersistTray()
    {
        await traySaveGate.WaitAsync();
        try { await repository.SaveSetting("close-to-tray", CloseToTray); }
        catch (Exception ex) { host.Message = "Не удалось сохранить настройку трея: " + ex.Message; }
        finally { traySaveGate.Release(); }
    }
}
