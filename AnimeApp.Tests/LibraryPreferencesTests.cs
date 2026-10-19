using AnimeApp.Models;
using AnimeApp.UI;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
namespace AnimeApp.Tests;
public class LibraryPreferencesTests
{
    [AvaloniaFact]
    public async Task SortingPreservesFiltersAndUnknownDatesStayLast()
    {
        var (vm, repo) = await Stage4UiTests.Create();
        await repo.Cache(7, [
            new Anime { ShikimoriId=1, Name="Zulu", Score=9, AddedAt=DateTimeOffset.Parse("2020-01-01Z"), UserStatus="watching" },
            new Anime { ShikimoriId=2, Name="Alpha", Score=5, AddedAt=DateTimeOffset.Parse("2025-01-01Z"), UserStatus="watching" },
            new Anime { ShikimoriId=3, Name="Beta", Score=0, UserStatus="completed" }]);
        await vm.Reload();
        vm.LibrarySort="Недавно добавленные";
        Assert.Equal(new long[]{2,1,3},vm.Items.Select(a=>a.ShikimoriId));
        vm.LibrarySort="По моей оценке";
        Assert.Equal(new long[]{1,2,3},vm.Items.Select(a=>a.ShikimoriId));
        vm.LibrarySort="По названию";
        Assert.Equal(new long[]{2,3,1},vm.Items.Select(a=>a.ShikimoriId));
        await vm.NavigateCommand.ExecuteAsync("Смотрю");
        Assert.Equal(new long[]{2,1},vm.Items.Select(a=>a.ShikimoriId));
        for(int i=0;i<50 && await repo.Setting<string>("library-sort")!="По названию";i++) await Task.Delay(10);
        Assert.Equal("По названию",await repo.Setting<string>("library-sort"));
        vm.Settings.CloseToTray=true;
        for(int i=0;i<50 && !await repo.Setting<bool>("close-to-tray");i++) await Task.Delay(10);
        Assert.True(await repo.Setting<bool>("close-to-tray"));
    }
    [AvaloniaFact]
    public async Task HidingPlayerPausesAndSavesWithoutLosingPlaybackSession()
    {
        var (vm, engine, repo) = await PlayerViewModelTests.Create();
        await vm.Initialize();
        var window = new PlayerWindow(vm);
        window.Show();
        await window.Ready;
        engine.Current = new(65,100,true);
        await window.SuspendToTray();
        Assert.False(window.IsVisible);
        Assert.False(engine.Snapshot.Playing);
        Assert.Equal(65, (await repo.History(0)).Single().Position);
        Assert.False(engine.Disposed);
        window.RestoreFromTray();
        Assert.True(window.IsVisible);
        Assert.False(engine.Snapshot.Playing);
        await window.Shutdown();
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing,false,true)]
    [InlineData(WindowCloseReason.WindowClosing,true,false)]
    [InlineData(WindowCloseReason.OSShutdown,false,false)]
    [InlineData(WindowCloseReason.ApplicationShutdown,false,false)]
    public void TrayNeverBlocksSystemOrProgrammaticExit(WindowCloseReason reason,bool programmatic,bool hide)
    {
        Assert.Equal(hide,TrayController.ShouldHide(true,false,reason,programmatic));
        Assert.False(TrayController.ShouldHide(false,false,reason,programmatic));
        Assert.False(TrayController.ShouldHide(true,true,reason,programmatic));
    }
}
