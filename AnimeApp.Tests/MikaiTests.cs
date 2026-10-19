using System.Text.Json;
using AnimeApp.Providers.Mikai;
using AnimeApp.Services.Shikimori;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace AnimeApp.Tests;
public sealed class MikaiTests
{
    [AvaloniaFact]
    public async Task CalendarKeepsStudioSelectionWhenChoicesAreUnchanged()
    {
        var (vm, _) = await Stage4UiTests.Create(new MikaiProvider(new(new HttpClient()), "glass-moon", "Glass Moon", 3));
        vm.Schedule.RefreshChoices();
        var selected = vm.Schedule.SelectedSource;
        Assert.NotNull(selected);
        var resets = 0;
        vm.Schedule.ProviderChoices.CollectionChanged += (_, _) => resets++;
        vm.Schedule.RefreshChoices();
        Assert.Same(selected, vm.Schedule.SelectedSource);
        Assert.Equal(0, resets);
    }
    [Fact]
    public void DirectVideoUsesMainFileAndRejectsOtherHosts()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikai", "ashdi.html"));
        var stream = MikaiClient.AshdiMedia(html);
        Assert.NotNull(stream);
        Assert.Contains("gm_sousou_no_frieren", stream);
        Assert.EndsWith(".m3u8", stream);
        Assert.Null(MikaiClient.AshdiMedia("new Playerjs({file:'https://untrusted.example/video.m3u8'})"));
        Assert.Null(MikaiClient.AshdiMedia("new Playerjs({preroll:'https://ashdi.vip/ad.m3u8'})"));
    }
    [Theory]
    [InlineData("glass-moon", "https://ashdi.vip/vod/104234")]
    [InlineData("fanvoxua", "https://ashdi.vip/vod/104245")]
    public void TeamHasOneDubbedEpisodePerNumber(string team, string first)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikai", "frieren-player.json")));
        var episodes = MikaiProvider.ParseEpisodes("1272", doc.RootElement.GetProperty("result"), team);
        Assert.Equal(28, episodes.Count);
        Assert.Equal(Enumerable.Range(1, 28), episodes.Select(e => e.Number));
        Assert.Equal(first, episodes[0].PageUrl);
        Assert.Empty(MikaiProvider.ParseEpisodes("1272", doc.RootElement.GetProperty("result"), "unknown"));
    }
    [Fact]
    public void LicensedTitlesAreNotPlayable()
    {
        using var doc = JsonDocument.Parse("{\"licensed\":true,\"releases\":[]}");
        Assert.Empty(MikaiProvider.ParseEpisodes("1", doc.RootElement, "glass-moon"));
    }
    [Fact]
    public void PublicOAuthRequiresHttpsBrokerAndDoesNotNeedSecret()
    {
        Assert.True(new OAuthOptions("id", TokenBrokerUrl: "https://auth.example/token").Configured);
        Assert.False(new OAuthOptions("id", TokenBrokerUrl: "http://auth.example/token").Configured);
        Assert.False(new OAuthOptions("id", TokenBrokerUrl: "https://user:password@auth.example/token").Configured);
    }
    [AvaloniaFact]
    public async Task SidebarScrollsInsideCompactWindow()
    {
        var (vm, _) = await Stage4UiTests.Create();
        vm.Settings.WizardVisible = false;
        await vm.NavigateCommand.ExecuteAsync("Настройки");
        var window = new AnimeApp.UI.MainWindow { Width = 1100, Height = 600, DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var nav = window.FindControl<ScrollViewer>("SidebarNavigation")!;
            Assert.True(nav.Viewport.Height > 0);
            Assert.True(nav.Extent.Height > nav.Viewport.Height);
            nav.Offset = new(0, nav.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            Assert.True(nav.Offset.Y > 0);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1100, 600));
            bitmap.Render(window);
            bitmap.Save(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/sidebar-compact.png")));
        }
        finally { window.Close(); }
    }
}
