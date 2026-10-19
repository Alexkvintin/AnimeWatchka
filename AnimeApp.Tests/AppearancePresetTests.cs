using AnimeApp.ViewModels;
using AnimeApp.UI;
using Avalonia.Headless.XUnit;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace AnimeApp.Tests;
public class AppearancePresetTests
{
    private static string Root() { var p = Path.Combine(Path.GetTempPath(), "AnimeWatchka-presets-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    [AvaloniaFact]
    public void SlotsSnapshotAssetsAndTransferWithoutOriginals()
    {
        var root = Root();
        var a = new AppearanceViewModel { StorageRoot = Path.Combine(root, "sender") };
        a.SelectThemeCommand.Execute("lumi");
        a.SetBackground(Fixture("character.png")); a.SetCharacter(Fixture("character.gif"));
        var character = File.ReadAllBytes(a.CharacterPath);
        for (int i = 1; i <= 3; i++) { a.Accent = $"#11223{i}"; a.SavePresetCommand.Execute(i.ToString()); }
        a.Accent = "#ABCDEF"; a.SavePresetCommand.Execute("2");
        a.LoadPresetCommand.Execute("1"); Assert.Equal("#112231", a.Accent);
        a.LoadPresetCommand.Execute("2"); Assert.Equal("#ABCDEF", a.Accent);
        a.LoadPresetCommand.Execute("3"); Assert.Equal("#112233", a.Accent);
        a.SavePresetCommand.Execute("4"); Assert.Contains("три", a.Error);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(a.StorageRoot, "presets")).Length);
        var package = Path.Combine(root, "shared.awtheme"); a.ExportAppearance(package);
        using (var zip = ZipFile.OpenRead(package)) {
            Assert.Equal(3, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("theme.json")!.Open());
            Assert.DoesNotContain(root, reader.ReadToEnd());
        }
        a.Background?.Dispose();
        Directory.Delete(a.StorageRoot, true);
        var b = new AppearanceViewModel { StorageRoot = Path.Combine(root, "recipient") };
        b.ImportAppearance(package); Assert.Contains("импортировано", b.Error);
        Assert.Equal("#112233", b.Accent); Assert.Equal(character, File.ReadAllBytes(b.CharacterPath)); Assert.NotNull(b.Background);
        var restored = new AppearanceViewModel { StorageRoot = b.StorageRoot }; restored.Load();
        Assert.Equal(b.CharacterPath, restored.CharacterPath); Assert.Equal(b.Accent, restored.Accent);
        b.Background?.Dispose(); restored.Background?.Dispose();
    }
    [AvaloniaFact]
    public void RejectInvalidPackageWithoutChangingAppearance()
    {
        var root = Root(); var a = new AppearanceViewModel { StorageRoot = root };
        a.SelectThemeCommand.Execute("dark"); a.Accent = "#123456";
        var valid = Path.Combine(root, "valid.awtheme"); a.ExportAppearance(valid);
        var bad = Path.Combine(root, "bad.awtheme"); File.Copy(valid, bad);
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update)) zip.CreateEntry("../escape");
        a.ImportAppearance(bad); Assert.Equal("#123456", a.Accent); Assert.Contains("Неверный состав", a.Error);
        Assert.False(File.Exists(Path.Combine(root, "escape")));
        File.Copy(valid, bad, true);
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update)) {
            var entry = zip.GetEntry("theme.json")!; JsonNode node;
            using (var stream = entry.Open()) node = JsonNode.Parse(stream)!;
            entry.Delete(); node["Settings"]!["Character"] = "character";
            using (var writer = new StreamWriter(zip.CreateEntry("theme.json").Open())) writer.Write(node.ToJsonString());
            using var image = new StreamWriter(zip.CreateEntry("character").Open()); image.Write("broken gif");
        }
        a.ImportAppearance(bad); Assert.Equal("#123456", a.Accent); Assert.Equal("", a.CharacterPath); Assert.Contains("прочитать", a.Error);
    }
    [AvaloniaFact]
    public async Task GifAnimatesInWindowAndStopsOnRemoval()
    {
        var image = new CharacterImage { CharacterPath = Fixture("character.gif"), Width = 80, Height = 80 };
        var window = new Avalonia.Controls.Window { Content = image, Width = 200, Height = 200 };
        window.Show(); window.Activate(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var first = image.Source; Assert.NotNull(first);
        await Task.Delay(95); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.NotSame(first, image.Source);
        window.Content = null; Assert.Null(image.Source);
        window.Close();
    }
    [AvaloniaFact]
    public void GifContainsDistinctFramesAndPngKeepsAlpha()
    {
        using var gif = CharacterFrames.Open(Fixture("character.gif"));
        Assert.Equal(2, gif.Frames.Count); Assert.Equal(new[] {80,120}, gif.Delays);
        using var first = new MemoryStream(); using var second = new MemoryStream();
        gif.Frames[0].Save(first); gif.Frames[1].Save(second); Assert.False(first.ToArray().SequenceEqual(second.ToArray()));
        using var png = CharacterFrames.Open(Fixture("character.png"));
        using var output = new MemoryStream(); png.Frames[0].Save(output);
        using var pixels = SkiaSharp.SKBitmap.Decode(output.ToArray()); Assert.Equal(0, pixels.GetPixel(0,0).Alpha);
    }
}
