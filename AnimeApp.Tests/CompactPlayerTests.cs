using AnimeApp.UI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;
namespace AnimeApp.Tests;
public class CompactPlayerTests
{
    [AvaloniaFact]
    public async Task SmallWindowKeepsVideoSpaceAndOptionsDoNotRestartPlayback()
    {
        var (vm, engine, _) = await PlayerViewModelTests.Create();
        var window = new PlayerWindow(vm) { Width=1000, Height=650 };
        window.Show(); await window.Ready;
        engine.Current = new(50,100,true);
        vm.Status="Приятного просмотра";
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var controls=window.FindControl<Border>("Controls")!;
        var compactHeight=controls.Bounds.Height;
        Assert.InRange(compactHeight,60,200);
        Assert.False(vm.ShowOptions);
        vm.ShowOptions=true;
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.True(controls.Bounds.Height>compactHeight);
        Assert.Equal(50,engine.Snapshot.Position);
        Assert.True(engine.Snapshot.Playing);
        vm.ShowOptions=false;
        vm.ShowNext=true; vm.Playing=true;
        Assert.Equal("⏸ Пауза",vm.PlayLabel);
        engine.Current=new(100,100,false,Ended:true);
        Assert.Equal("▶ Следующая серия",vm.PlayLabel);
        vm.Status="Не удалось открыть видео.";
        Assert.True(vm.ShowStatus);
        vm.Status="Приятного просмотра";
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"screenshots"));
        using var bitmap=new RenderTargetBitmap(new PixelSize(1000,650));
        bitmap.Render(window); bitmap.Save(Path.Combine(AppContext.BaseDirectory,"screenshots","compact-player.png"));
        await window.Shutdown();
    }
}
