using AnimeApp.UI;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
namespace AnimeApp.Tests;
public class PlayerQualityTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task QualityPreservesEpisodePositionAndPauseWithoutCompleting(bool playing)
    {
        var (vm,engine,repo)=await PlayerViewModelTests.Create(autoResume:false,variants:true);
        await vm.Initialize();
        engine.Current=new(63,100,playing);
        vm.SelectedStreamQuality="720p";
        await vm.ApplyStreamQualityCommand.ExecuteAsync(null);
        Assert.Equal("https://example.org/720.mp4",engine.LastSource!.Location);
        Assert.Equal(63,engine.Snapshot.Position);
        Assert.Equal(playing,engine.Snapshot.Playing);
        Assert.Equal(1,vm.Request.Episode);
        Assert.False((await repo.History(0)).Single().Completed);
        await vm.Tick();
        Assert.Equal(63,engine.Snapshot.Position);
        await vm.Close();
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedQualityRestoresOldStreamOrKeepsSavedPosition(bool rollbackFails)
    {
        var(vm,engine,repo)=await PlayerViewModelTests.Create(variants:true);
        await vm.Initialize(); engine.Current=new(63,100,false);
        engine.FailedLocations.Add("https://example.org/720.mp4");
        if(rollbackFails) engine.FailedLocations.Add("https://example.org/1080.mp4");
        vm.SelectedStreamQuality="720p";
        await vm.ApplyStreamQualityCommand.ExecuteAsync(null);
        Assert.Equal("1080p",vm.SelectedStreamQuality);
        if(!rollbackFails) Assert.Equal(63,engine.Snapshot.Position);
        await vm.Tick();
        await vm.Close();
        Assert.Equal(63,(await repo.History(0)).Single().Position);
        Assert.False((await repo.History(0)).Single().Completed);
    }
    [AvaloniaFact]
    public async Task RepeatedEpisodeChangesKeepFullscreenAndWindow()
    {
        var (vm,engine,_)=await PlayerViewModelTests.Create();
        var window=new PlayerWindow(vm);
        window.Show(); await window.Ready;
        window.ToggleFullscreen();
        for(int i=0;i<20;i++)
        {
            await vm.NextCommand.ExecuteAsync(null);
            Assert.True(window.Fullscreen.Active);
            Assert.Equal(WindowState.FullScreen,window.WindowState);
            await vm.PreviousCommand.ExecuteAsync(null);
            Assert.True(window.Fullscreen.Active);
            Assert.False(engine.Disposed);
        }
        await window.Shutdown();
    }
}
