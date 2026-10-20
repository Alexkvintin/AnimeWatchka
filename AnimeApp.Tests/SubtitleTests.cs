using System.Text.Json;
using AnimeApp.Providers.Mikai;
using AnimeApp.Services.Playback;
using AnimeApp.UI;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace AnimeApp.Tests;

public sealed class SubtitleTests
{
    [Fact]
    public void SeasonUsesMostCompleteSubtitleTeamAndNeverDubbedEpisodes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikai", "cowboy-player.json")));
        var episodes = SubtitlesProvider.ParseEpisodes("1", doc.RootElement.GetProperty("result"));
        Assert.Equal(26, episodes.Count);
        Assert.Equal(Enumerable.Range(1, 26), episodes.Select(e => e.Number));
        Assert.All(episodes, e => { Assert.Contains(":t_306:sub:", e.Id); Assert.Contains("snvlpkinq", e.Title); });
        Assert.Equal("https://ashdi.vip/vod/135093", episodes[0].PageUrl);
        var all = SubtitlesProvider.ParseEpisodes("1", doc.RootElement.GetProperty("result"), true);
        Assert.Contains(all, e => e.Id.Contains(":t_128:sub:"));
        Assert.DoesNotContain(all, e => e.Id.Contains(":voice:"));
    }
    [Fact]
    public void LicensedSubtitleReleaseIsNotExposed()
    {
        using var doc = JsonDocument.Parse("{\"licensed\":true}");
        Assert.Empty(SubtitlesProvider.ParseEpisodes("1", doc.RootElement));
    }
    [Fact]
    public void UnsupportedHostsAndMissingTeamIdentityAreNotPlayable()
    {
        using var doc = JsonDocument.Parse("""
        {"releases":[{"id":"a","kind":"sub","teams":[],"episodes":[{"number":1,"sources":[{"embedUrl":"https://ashdi.vip.evil.test/1"}]}]},{"kind":"sub","teams":[],"episodes":[]}]}
        """);
        Assert.Empty(SubtitlesProvider.ParseEpisodes("1", doc.RootElement));
    }
    [Fact]
    public async Task UnsupportedPlayerDoesNotAdvertiseSubtitleControls()
    {
        var (vm, engine, _) = await PlayerViewModelTests.Create();
        try
        {
            await vm.Initialize();
            Assert.False(vm.CanUseSubtitles);
            Assert.Equal(-1, Assert.Single(vm.SubtitleTracks).Id);
            Assert.Throws<IOException>(() => SubtitleControls.AddFile(engine, "https://example.org/sub.srt"));
        }
        finally { await vm.Close(); }
    }
    [AvaloniaFact]
    public void SubtitleButtonExistsOutsideVideoSettings()
    {
        var window = new PlayerWindow();
        try
        {
            window.Show();
            var cc = Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), b => Equals(b.Content, "CC"));
            Assert.True(cc.IsVisible);
        }
        finally { window.Close(); }
    }
}
