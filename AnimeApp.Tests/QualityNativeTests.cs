using AnimeApp.Services.Playback;
using System.Text;
using Xunit;
namespace AnimeApp.Tests;
public class QualityNativeTests
{
    [Fact]
    public async Task VlcLoadsPausedAtRequestedTimeAndNextOpenResetsStart()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,"clock.wav");
        const int bytes=8000*2*20;
        using(var w=new BinaryWriter(File.Create(file)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+bytes);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(8000);w.Write(16000);w.Write((short)2);w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));w.Write(bytes);w.Write(new byte[bytes]);
        }
        await using var engine=new VlcPlayerEngine("--vout=dummy","--aout=dummy");
        await engine.Open(new(PlaybackKind.LocalFile,file,StartSeconds:6,StartPaused:true));
        for(int i=0;i<50 && engine.Snapshot.Position<5;i++) await Task.Delay(100);
        await Task.Delay(200);
        Assert.InRange(engine.Snapshot.Position,5,7);
        Assert.False(engine.Snapshot.Playing);
        await engine.Open(new(PlaybackKind.LocalFile,file));
        await Task.Delay(500);
        Assert.True(engine.Snapshot.Playing);
        Assert.InRange(engine.Snapshot.Position,0,3);
    }
}
