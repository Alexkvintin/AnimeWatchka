using AnimeApp.Services.Playback;
using SkiaSharp;
using Xunit;

namespace AnimeApp.Tests;
public sealed class SubtitleFontTests
{
    [Theory]
    [InlineData("BalsamiqSans-Regular.ttf", "Balsamiq Sans")]
    [InlineData("Stick-Regular.ttf", "Stick")]
    public void BundledFontsHaveCorrectFamilyAndCyrillicLatinGlyphs(string file, string family)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fonts", file);
        Assert.Equal(family, SubtitleFonts.ReadFamily(path));
        using var face = SKTypeface.FromFile(path);
        using var font = new SKFont(face);
        Assert.True(font.ContainsGlyphs("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя"));
    }
    [Fact]
    public void ImportedFontIsCopiedDeduplicatedAndSurvivesSourceRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "renamed.ttf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fonts", "BalsamiqSans-Regular.ttf"), source);
        var store = new SubtitleFonts(Path.Combine(root, "saved"));
        var imported = store.Import(source);
        Assert.Equal(imported.Id, store.Import(source).Id);
        File.Delete(source);
        Assert.Equal("Balsamiq Sans", Assert.Single(store.List(), f => f.Id == imported.Id).Family);
        Assert.Single(Directory.GetFiles(store.DirectoryPath, "custom-*"));
    }
    [Fact]
    public void InvalidFontIsRejectedBeforeCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "broken.ttf");
        File.WriteAllText(source, "This is not a font");
        var store = new SubtitleFonts(Path.Combine(root, "saved"));
        Assert.Throws<IOException>(() => store.Import(source));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }
    [Fact]
    public async Task StyleSelectionIsPersisted()
    {
        var (vm, _, repo) = await PlayerViewModelTests.Create();
        try
        {
            await vm.Initialize();
            vm.SelectedSubtitleFont = vm.SubtitleFontChoices.Single(f => f.Id == "manga");
            vm.SubtitleSize = 52;
            vm.PreserveSubtitleStyles = false;
            await vm.SaveSubtitleStyleCommand.ExecuteAsync(null);
            Assert.Equal(new SubtitleStyle("manga", 52, false), await repo.Setting<SubtitleStyle>("subtitle-style"));
        }
        finally { await vm.Close(); }
    }
}
