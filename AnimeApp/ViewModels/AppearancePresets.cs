using System.IO.Compression;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using AnimeApp.UI;

namespace AnimeApp.ViewModels;
public sealed partial class AppearanceViewModel
{
    public bool HasPreset1 => File.Exists(SlotPath("1"));
    public bool HasPreset2 => File.Exists(SlotPath("2"));
    public bool HasPreset3 => File.Exists(SlotPath("3"));
    private void RefreshPresets() { OnPropertyChanged(nameof(HasPreset1)); OnPropertyChanged(nameof(HasPreset2)); OnPropertyChanged(nameof(HasPreset3)); }
    private sealed record ThemePackage(int Version, Preferences Settings);
    private Preferences Snapshot() => new(Theme, Accent, Surface, Canvas, TextColor, BackgroundOpacity, Blur, imagePath, PanelOpacity, 3, CharacterPath);
    private string SlotPath(string slot)
    {
        if (slot is not ("1" or "2" or "3")) throw new InvalidDataException("Доступны три слота.");
        return Path.Combine(StorageRoot, "presets", "slot" + slot + ".awtheme");
    }
    [RelayCommand] private void SavePreset(string slot) => RunAppearance(() => { ExportCore(SlotPath(slot)); RefreshPresets(); Error = $"Пресет {slot} сохранён. Повторное сохранение заменит его."; });
    [RelayCommand] private void LoadPreset(string slot) => RunAppearance(() => { ImportCore(SlotPath(slot)); Error = $"Пресет {slot} применён."; });
    [RelayCommand] private void ResetCharacter() { CharacterPath = ""; Error = "Используются стандартные персонажи темы."; }
    public void SetCharacter(string path) => RunAppearance(() =>
    {
        using var check = CharacterFrames.Open(path);
        var target = CopyAsset(path);
        CharacterPath = target;
        Error = "Персонаж сохранён.";
    });
    public void ExportAppearance(string path) => RunAppearance(() => { ExportCore(path); Error = "Оформление упаковано вместе с изображениями."; });
    public void ImportAppearance(string path) => RunAppearance(() => { ImportCore(path); EditorVisible = true; Error = "Оформление импортировано. Сохраните его в один из трёх слотов, если хотите оставить пресет."; });
    private void RunAppearance(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { Error = ex is InvalidDataException ? ex.Message : "Не удалось открыть или сохранить оформление. Проверьте файл и доступ к папке."; EditorVisible = true; }
    }
    private string CopyAsset(string path)
    {
        var directory = Path.Combine(StorageRoot, "appearance-assets"); Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        File.Copy(path, target); return target;
    }
    private void ExportCore(string path)
    {
        var settings = Snapshot();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                string Pack(string source, string name)
                {
                    if (!File.Exists(source)) throw new InvalidDataException("Изображение оформления недоступно. Выберите его заново.");
                    if (new FileInfo(source).Length > 24 * 1024 * 1024) throw new InvalidDataException("Изображение больше 24 МБ.");
                    zip.CreateEntryFromFile(source, name); return name;
                }
                settings = settings with {
                    Image = string.IsNullOrEmpty(imagePath) || imagePath == "none" ? imagePath : Pack(imagePath, "background"),
                    Character = string.IsNullOrEmpty(CharacterPath) ? "" : Pack(CharacterPath, "character")
                };
                using var stream = zip.CreateEntry("theme.json").Open();
                JsonSerializer.Serialize(stream, new ThemePackage(1, settings));
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void ImportCore(string path)
    {
        if (new FileInfo(path).Length > 50 * 1024 * 1024) throw new InvalidDataException("Файл оформления больше 50 МБ.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 3 || zip.Entries.Select(e => e.FullName).Distinct().Count() != zip.Entries.Count ||
            zip.Entries.Any(e => e.FullName is not ("theme.json" or "background" or "character") || e.Length > 24 * 1024 * 1024))
            throw new InvalidDataException("Неверный состав файла оформления.");
        var manifest = zip.GetEntry("theme.json") ?? throw new InvalidDataException("Нет описания оформления.");
        if (manifest.Length > 16384) throw new InvalidDataException("Неверное описание оформления.");
        using var json = manifest.Open();
        using var boundedJson = new MemoryStream();
        var jsonBuffer = new byte[4096]; int jsonRead;
        while ((jsonRead = json.Read(jsonBuffer)) > 0) { if (boundedJson.Length + jsonRead > 16384) throw new InvalidDataException("Неверное описание оформления."); boundedJson.Write(jsonBuffer, 0, jsonRead); }
        boundedJson.Position = 0;
        var package = JsonSerializer.Deserialize<ThemePackage>(boundedJson) ?? throw new InvalidDataException("Пустое оформление.");
        var p = package.Settings;
        if (package.Version != 1 || p is null || !Color.TryParse(p.Accent, out _) || !Color.TryParse(p.Surface, out _) || !Color.TryParse(p.Canvas, out _) || !Color.TryParse(p.Text, out _) ||
            p.Opacity is not (>= 0 and <= 1) || p.PanelOpacity is not (>= .35 and <= 1) || p.Blur is not (>= 0 and <= 40) ||
            p.Theme is not ("dark" or "light" or "lumi" or "forest" or "sand") || p.Image is not ("" or "none" or "background") || p.Character is not ("" or "character"))
            throw new InvalidDataException("Неподдерживаемые настройки оформления.");
        var staging = Path.Combine(StorageRoot, "appearance-assets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Bitmap? next = null;
        bool committed = false;
        try
        {
            string Unpack(string name)
            {
                var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Не хватает изображения в оформлении.");
                var destination = Path.Combine(staging, name);
                using var input = entry.Open(); using var output = File.Create(destination);
                var buffer = new byte[81920]; int read; long total = 0;
                while ((read = input.Read(buffer)) > 0) { total += read; if (total > 24 * 1024 * 1024) throw new InvalidDataException("Изображение слишком большое."); output.Write(buffer, 0, read); }
                return destination;
            }
            var nextImage = p.Image == "background" ? Unpack("background") : p.Image;
            var nextCharacter = p.Character == "character" ? Unpack("character") : "";
            if (nextCharacter != "") { using var check = CharacterFrames.Open(nextCharacter); }
            if (p.Image == "background") { using var codec = SkiaSharp.SKCodec.Create(nextImage) ?? throw new InvalidDataException("Не удалось прочитать фон.");
                if (codec.Info.Width > 16384 || codec.Info.Height > 16384 || (long)codec.Info.Width * codec.Info.Height > 40_000_000) throw new InvalidDataException("Размер фона слишком большой.");
                using var stream = File.OpenRead(nextImage); next = Bitmap.DecodeToWidth(stream, 2560); }
            else if (p.Theme == "lumi" && p.Image == "") { using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://AnimeApp/Assets/lumi-wallpaper.png")); next = new Bitmap(stream); }
            loading = true;
            Theme = p.Theme; Accent = p.Accent; Surface = p.Surface; Canvas = p.Canvas; TextColor = p.Text;
            BackgroundOpacity = p.Opacity; PanelOpacity = p.PanelOpacity; Blur = p.Blur;
            imagePath = nextImage; CharacterPath = nextCharacter;
            Background?.Dispose(); Background = next; next = null;
            committed = true;
        }
        finally
        {
            loading = false; next?.Dispose();
            if (!committed) Directory.Delete(staging, true);
        }
        Apply(); Save();
    }
}
