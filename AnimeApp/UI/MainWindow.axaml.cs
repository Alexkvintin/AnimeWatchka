using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AnimeApp.ViewModels;
using Avalonia.Markup.Xaml;

namespace AnimeApp.UI;
public sealed partial class MainWindow : Window
{
    private async void ChooseBackground(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Фон AnimeWatchka", AllowMultiple = false,
            FileTypeFilter = [Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll]
        });
        if (files.Count > 0 && files[0].Path.IsFile)
            AnimeApp.ViewModels.AppearanceViewModel.Current.SetBackground(files[0].Path.LocalPath);
    }
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => {
            e.DragEffects = ThemeDropPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, (_, e) => {
            if (ThemeDropPath(e) is { } path) AppearanceViewModel.Current.ImportAppearance(path);
            e.Handled = true;
        });
        Closed += (_, _) => { if (DataContext is MainViewModel vm) vm.Sources.CancelDownloadCommand.Execute(null); };
        Opened += (_, _) => UpdateNavigation();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AnimeApp.ViewModels.MainViewModel model)
            {
                model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Page)) UpdateNavigation(); };
                UpdateNavigation();
            }
        };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.XButton1Pressed && DataContext is AnimeApp.ViewModels.MainViewModel vm)
            {
                vm.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.BrowserBack || e.Key == Avalonia.Input.Key.Left && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
            {
                if (DataContext is AnimeApp.ViewModels.MainViewModel vm)
                    vm.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    private static string? ThemeDropPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        return files?.Length == 1 && files[0].Path.IsFile && string.Equals(Path.GetExtension(files[0].Path.LocalPath), ".awtheme", StringComparison.OrdinalIgnoreCase) ? files[0].Path.LocalPath : null;
    }
    private static readonly FilePickerFileType ThemeFile = new("Оформление AnimeWatchka") { Patterns = ["*.awtheme"] };
    private async void ChooseCharacter(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Персонаж PNG / GIF", AllowMultiple = false, FileTypeFilter = [new("Персонажи") { Patterns = ["*.png", "*.gif"] }] });
        if (files.Count > 0 && files[0].Path.IsFile) AppearanceViewModel.Current.SetCharacter(files[0].Path.LocalPath);
    }
    private async void ImportTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Импорт оформления", AllowMultiple = false, FileTypeFilter = [ThemeFile] });
        if (files.Count > 0 && files[0].Path.IsFile) AppearanceViewModel.Current.ImportAppearance(files[0].Path.LocalPath);
    }
    private async void ExportTheme(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Поделиться оформлением", SuggestedFileName = "Моё оформление.awtheme", DefaultExtension = "awtheme", FileTypeChoices = [ThemeFile] });
        if (file?.Path.IsFile == true) AppearanceViewModel.Current.ExportAppearance(file.Path.LocalPath);
    }
    private async void CleanInterruptedDownloads(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.Sources.Downloading) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Папка загрузок: удалить незавершённые загрузки AnimeWatchka", AllowMultiple = false });
        if (folders.Count == 0 || !folders[0].Path.IsFile || vm.Sources.Downloading) return;
        try { var count = AnimeApp.Services.Playback.EpisodeDownloader.RemoveInterrupted(folders[0].Path.LocalPath); vm.Sources.DownloadStatus = $"Удалено незавершённых загрузок: {count}. Готовые серии сохранены."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { vm.Sources.DownloadStatus = "Не удалось удалить некоторые временные файлы. Проверьте доступ к папке."; }
    }
    private async void OpenDownloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Папка скачанной серии AnimeWatchka", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].Path.IsFile) await vm.Sources.OpenDownloaded(folders[0].Path.LocalPath);
    }
    private void UpdateNavigation()
    {
        if (DataContext is not AnimeApp.ViewModels.MainViewModel model) return;
        foreach (var button in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<Button>().Where(b => b.Classes.Contains("nav")))
            button.Classes.Set("active", button.CommandParameter as string == model.Page);
    }

    private void SearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is AnimeApp.ViewModels.MainViewModel vm)
            vm.SearchCommand.Execute(null);
    }
}
