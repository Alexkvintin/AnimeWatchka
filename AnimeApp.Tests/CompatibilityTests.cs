using AnimeApp.Models;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using Xunit;
namespace AnimeApp.Tests;
public class CompatibilityTests
{
    [Fact]
    public void OldVulkanLoaderIsRejectedBeforeLoadingMpv()
    {
        Assert.False(PlayerEngineFactory.HasRequiredVulkanExports(name => name == "vkGetInstanceProcAddr"));
        Assert.True(PlayerEngineFactory.HasRequiredVulkanExports(_ => true));
    }
    [Fact]
    public async Task ExplicitCompatibilityUsesVlcEvenWhenAnimeScalingIsSelected()
    {
        await using var engine=PlayerEngineFactory.Create(PlaybackKind.DirectMedia,"anime",true);
        var vlc=Assert.IsType<VlcPlayerEngine>(engine);
        Assert.Contains("программное",vlc.FallbackReason);
        await using var web=PlayerEngineFactory.Create(PlaybackKind.WebPage,"anime",true);
        Assert.IsType<WebPlayerEngine>(web);
    }
    [Fact]
    public void AmbiguousHighMatchesRequireSelection()
    {
        var best=new ReleaseSuggestion(new("one","Title"),1,"");
        var other=new ReleaseSuggestion(new("two","Title"),0.9,"");
        Assert.Null(ReleaseMatcher.Unambiguous([best,other]));
        Assert.Same(best,ReleaseMatcher.Unambiguous([best,other with {Score=0.3}]));
        Assert.Null(ReleaseMatcher.Unambiguous([]));
    }
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task CompatibilityPreferenceSurvivesSettingsReload()
    {
        var (vm, repo)=await Stage4UiTests.Create();
        vm.Settings.PlayerCompatibility=true;
        await vm.Settings.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(await repo.Setting<bool>("player-compatibility"));
        vm.Settings.PlayerCompatibility=false;
        await vm.Settings.Initialize();
        Assert.True(vm.Settings.PlayerCompatibility);
    }
    [Theory]
    [InlineData("Black Lagoon","Black Clover")]
    [InlineData("A very long anime season title 2","A very long anime season title 3")]
    [InlineData("!!!","???")]
    public void UnrelatedOrDifferentNumberedSeasonsAreNeverAutoMatched(string name,string title)
    {
        Assert.True(new ReleaseMatcher().Rank(new Anime{Name=name},new("test",title)).Score<0.6);
    }
}
