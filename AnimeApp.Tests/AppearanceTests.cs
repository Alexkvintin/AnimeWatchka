using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AnimeApp.UI;
using AnimeApp.ViewModels;
using AnimeApp.Models;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using Xunit;

namespace AnimeApp.Tests;
public class AppearanceTests
{
    private sealed class Source(string id) : AnimeApp.Services.Providers.IAnimeProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public string Language => "ru";
        public Task<ProviderAvailability> CheckAvailability(CancellationToken ct = default) => Task.FromResult(new ProviderAvailability(true, ""));
        public Task<IReadOnlyList<ProviderRelease>> Search(string q, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderRelease>>([]);
        public Task<ProviderRelease> GetRelease(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProviderEpisode>> GetEpisodes(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderEpisode>>([]);
        public Task<PlaybackSource> GetPlayback(string id, ProviderEpisode e, CancellationToken ct = default) => throw new NotSupportedException();
    }
    [AvaloniaFact]
    public async Task DragAndTogglePersistAndControlPlaybackOrder()
    {
        var (vm, repo) = await Stage4UiTests.Create(new Source("First"), new Source("Second"));
        var view = new SourceOrderView { DataContext = vm.Settings };
        var window = new Window { Content = view, Width = 600, Height = 240 };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var handles = view.GetVisualDescendants().OfType<Border>().Where(b => b.Child is TextBlock { Text: "⠿" }).ToArray();
        var from = handles[1].TranslatePoint(new Point(15, 15), window)!.Value;
        var to = handles[0].TranslatePoint(new Point(15, 15), window)!.Value;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(to); window.MouseUp(to, MouseButton.Left);
        for (var i = 0; i < 30 && (await repo.Setting<ProviderSettings>("provider:First"))?.Priority != 1; i++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal("Second", vm.Settings.Providers[0].Id);
        Assert.Equal(1, (await repo.Setting<ProviderSettings>("provider:First"))!.Priority);
        var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>().First();
        window.UpdateLayout();
        var togglePoint = toggle.TranslatePoint(new Point(20, toggle.Bounds.Height / 2), window)!.Value;
        window.MouseDown(togglePoint, MouseButton.Left); window.MouseUp(togglePoint, MouseButton.Left);
        for (var i = 0; i < 30 && (await repo.Setting<ProviderSettings>("provider:Second"))?.Enabled != false; i++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.False((await repo.Setting<ProviderSettings>("provider:Second"))!.Enabled);
        window.Close();
    }
    [AvaloniaFact]
    public async Task GuestHasNoPersonalCacheAndLogoutCannotRestoreItWithBack()
    {
        var (vm, repo) = await Stage4UiTests.Create();
        await repo.Cache(0, [new Anime { ShikimoriId = 52991, Name = "Old guest cache" }]);
        await repo.Cache(7, [new Anime { ShikimoriId = 1, Name = "Private" }]);
        vm.UserId = 0; await vm.Reload(); Assert.Empty(vm.Items);
        vm.UserId = 7; await vm.Reload(); Assert.Single(vm.Items);
        await vm.OpenAnimeCommand.ExecuteAsync(vm.Items[0]);
        vm.AccountAvatar = "https://shikimori.io/avatar.png";
        Assert.True(vm.HasAccountAvatar);
        await vm.Settings.DisconnectCommand.ExecuteAsync(null);
        Assert.False(vm.HasAccountAvatar);
        Assert.Empty(vm.AccountAvatar);
        Assert.Empty(vm.Items); Assert.Empty(vm.History); Assert.Null(vm.SelectedAnime); Assert.False(vm.CanGoBack);
        await vm.GoBackCommand.ExecuteAsync(null); Assert.Empty(vm.Items);
        Assert.Single(await repo.Library(7)); // Account cache is retained safely for reconnecting.
    }
    [AvaloniaFact]
    public async Task ThemesRenderAndVersionMatchesAssembly()
    {
        var (vm, _) = await Stage4UiTests.Create();
        vm.Settings.WizardVisible = false;
        vm.UserId = 0; await vm.Reload();
        var appearance = AppearanceViewModel.Current; appearance.Persist = false;
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
        window.Show();
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            foreach (var theme in new[] { "light", "dark", "forest", "sand", "lumi" })
            {
                appearance.SelectThemeCommand.Execute(theme);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1200, 760));
                bitmap.Render(window); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", theme + ".png"));
                Assert.Equal(theme == "lumi", appearance.IsLumi);
            }
            Assert.Contains(typeof(MainViewModel).Assembly.GetName().Version!.ToString(3), vm.Settings.Version);
            appearance.EditorVisible = true;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var palette = new RenderTargetBitmap(new PixelSize(1200, 760));
            palette.Render(window); palette.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "palette.png"));
        }
        finally { appearance.EditorVisible = false; window.Close(); appearance.SelectThemeCommand.Execute("dark"); appearance.Persist = true; }
    }
}
