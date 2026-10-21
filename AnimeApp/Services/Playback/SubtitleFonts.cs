using System.Security.Cryptography;
using SkiaSharp;

namespace AnimeApp.Services.Playback;

public record SubtitleFont(string Id, string Label, string Family)
{
    public override string ToString() => Label;
}
public record SubtitleStyle(string FontId = "standard", double Size = 40, bool PreserveAss = true);

public sealed class SubtitleFonts
{
    private static readonly object preparationGate = new();
    public string DirectoryPath { get; }
    public SubtitleFonts(string? root = null)
    {
        DirectoryPath = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "SubtitleFonts");
    }
    public void Prepare()
    {
        lock (preparationGate)
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var name in new[] { "BalsamiqSans-Regular.ttf", "Stick-Regular.ttf" })
            {
                var source = Path.Combine(AppContext.BaseDirectory, "Fonts", name);
                if (File.Exists(source) && !File.Exists(Path.Combine(DirectoryPath, name)))
                    File.Copy(source, Path.Combine(DirectoryPath, name));
            }
        }
    }
    public IReadOnlyList<SubtitleFont> List()
    {
        Prepare();
        var result = new List<SubtitleFont> { new("standard", "Стандартный", "Arial"), new("manga", "Манга · Balsamiq Sans", "Balsamiq Sans"), new("stick", "Иероглифный · Stick", "Stick") };
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "custom-*"))
        {
            try { var family = ReadFamily(file); result.Add(new(Path.GetFileName(file), "Свой · " + family, family)); }
            catch (IOException) { }
        }
        return result;
    }
    public static string ReadFamily(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > 20 * 1024 * 1024 ||
            Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf"))
            throw new IOException("Выберите шрифт TTF или OTF размером до 20 МБ.");
        using var face = SKTypeface.FromFile(path);
        if (face is null || string.IsNullOrWhiteSpace(face.FamilyName)) throw new IOException("Не удалось прочитать шрифт.");
        return face.FamilyName;
    }
    public SubtitleFont Import(string path)
    {
        var family = ReadFamily(path);
        Prepare();
        using var stream = File.OpenRead(path);
        var id = "custom-" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + Path.GetExtension(path).ToLowerInvariant();
        var destination = Path.Combine(DirectoryPath, id);
        if (!File.Exists(destination)) File.Copy(path, destination);
        return new(id, "Свой · " + family, family);
    }
}
