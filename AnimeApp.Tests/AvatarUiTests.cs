using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AnimeApp.UI;
using AnimeApp.ViewModels;
using Xunit;

namespace AnimeApp.Tests;
public sealed class AvatarUiTests
{
    private sealed class AvatarLoader : AnimeApp.Services.IPosterLoader
    {
        public Task<byte[]> Load(Uri uri, CancellationToken ct)
        {
            using var stream = AssetLoader.Open(new Uri("avares://AnimeApp/Assets/lumi.png"));
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            return Task.FromResult(bytes.ToArray());
        }
    }
    [AvaloniaFact]
    public async Task AvatarAppearsLeftOfNicknameInEveryThemeAndHidesOnLogout()
    {
        var (vm, _) = await Stage4UiTests.Create();
        vm.Settings.WizardVisible = false;
        vm.Account = "Test · подключён";
        var oldLoader = PosterImage.Loader;
        PosterImage.Loader = new AvatarLoader();
        vm.AccountAvatar = "https://shikimori.io/test-avatar.png";
        var appearance = AppearanceViewModel.Current; appearance.Persist = false;
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
        try
        {
            window.Show();
            foreach (var theme in new[] { "light", "dark", "lumi" })
            {
                appearance.SelectThemeCommand.Execute(theme);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var avatar = window.GetVisualDescendants().OfType<PosterImage>().Single(x => x.Url == vm.AccountAvatar);
                var label = window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == vm.Account);
                Assert.NotNull(avatar.Source);
                Assert.True(avatar.IsEffectivelyVisible);
                Assert.True(avatar.TranslatePoint(default, window)!.Value.X < label.TranslatePoint(default, window)!.Value.X);
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
                using var bitmap = new RenderTargetBitmap(new PixelSize(1200,760));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(AppContext.BaseDirectory,"screenshots","avatar-" + theme + ".png"));
            }
            await vm.Settings.DisconnectCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.HasAccountAvatar);
        }
        finally
        {
            window.Close(); PosterImage.Loader = oldLoader;
            appearance.SelectThemeCommand.Execute("dark"); appearance.Persist = true;
        }
    }
}
