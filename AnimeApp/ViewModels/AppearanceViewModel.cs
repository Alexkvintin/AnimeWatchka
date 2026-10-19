using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;

namespace AnimeApp.ViewModels;

public sealed partial class AppearanceViewModel : ObservableObject
{
    public static AppearanceViewModel Current { get; } = new();
    internal string StorageRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp");
    private string FilePath => Path.Combine(StorageRoot, "appearance.json");
    private bool loading;
    internal bool Persist { get; set; } = true;
    [ObservableProperty] private string theme = "dark";
    [ObservableProperty] private string accent = "#8BB8EE";
    [ObservableProperty] private string surface = "#171C24";
    [ObservableProperty] private string canvas = "#101319";
    [ObservableProperty] private string textColor = "#EDF0F5";
    [ObservableProperty] private double backgroundOpacity = 0.18;
    [ObservableProperty] private double blur = 0;
    [ObservableProperty] private double panelOpacity = 0.78;
    [ObservableProperty] private Bitmap? background;
    [ObservableProperty] private bool editorVisible;
    [ObservableProperty] private string error = "";
    public bool IsLumi => Theme == "lumi";
    public bool ShowCharacter => IsLumi || !string.IsNullOrEmpty(CharacterPath);
    [ObservableProperty] private string characterPath = "";
    partial void OnCharacterPathChanged(string value) { OnPropertyChanged(nameof(ShowCharacter)); if (!loading) Save(); }
    public IEffect BackgroundEffect => new BlurEffect { Radius = Blur };
    private string imagePath = "";
    public void Load()
    {
        loading = true;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) is { } p)
            {
                Theme = p.Theme; Accent = p.Accent; Surface = p.Surface; Canvas = p.Canvas;
                TextColor = p.Text; BackgroundOpacity = Math.Clamp(p.Opacity, 0, 1); Blur = Math.Clamp(p.Blur, 0, 40);
                PanelOpacity = p.PanelOpacity is >= 0.35 and <= 1 ? p.PanelOpacity : 0.78;
                imagePath = p.Image; CharacterPath = File.Exists(p.Character) ? p.Character : "";
                if (p.Schema < 2 && IsLumi && string.IsNullOrEmpty(imagePath))
                {
                    Canvas = "#14101F"; Surface = "#211D32"; Accent = "#F49DC9"; TextColor = "#FFF2FB";
                    BackgroundOpacity = .85; PanelOpacity = .76; Blur = 0;
                }
                if (File.Exists(imagePath)) Background = new Bitmap(imagePath);
                else if (IsLumi && imagePath != "none") LoadLumiWallpaper();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { Error = "Не удалось загрузить оформление."; }
        finally { loading = false; Apply(); }
    }
    [RelayCommand] private void SelectTheme(string mode)
    {
        loading = true;
        Theme = mode;
        (Canvas, Surface, Accent, TextColor) = mode switch
        {
            "light" => ("#F4F5F7", "#FFFFFF", "#245FA5", "#202630"),
            "forest" => ("#101D1A", "#1A3028", "#8FD5AF", "#EDF7F1"),
            "sand" => ("#F5EEE3", "#FFF9F0", "#8B563B", "#352A23"),
            "lumi" => ("#14101F", "#211D32", "#F49DC9", "#FFF2FB"),
            _ => ("#101319", "#171C24", "#8BB8EE", "#EDF0F5")
        };
        Background?.Dispose(); Background = null; imagePath = "";
        PanelOpacity = mode == "lumi" ? 0.76 : 0.80; BackgroundOpacity = mode == "lumi" ? 0.85 : 0.5; Blur = 0;
        if (mode == "lumi") LoadLumiWallpaper();
        loading = false; Apply(); Save();
    }
    [RelayCommand] private void Edit() => EditorVisible = !EditorVisible;
    [RelayCommand] private void ApplyCustom() { if (Apply()) { Save(); } }
    [RelayCommand] private void ClearBackground() { Background?.Dispose(); Background = null; imagePath = "none"; Apply(); Save(); }
    public void SetBackground(string path)
    {
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(FilePath)!, "background" + Path.GetExtension(path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = File.OpenRead(path);
            var bitmap = Bitmap.DecodeToWidth(stream, 2560);
            if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) File.Copy(path, target, true);
            Background?.Dispose(); Background = bitmap; imagePath = target; Apply(); Save(); Error = "";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { Error = "Не удалось открыть изображение."; }
    }
    private void LoadLumiWallpaper()
    {
        var uri = new Uri("avares://AnimeApp/Assets/lumi-wallpaper.png");
        if (Avalonia.Platform.AssetLoader.Exists(uri)) { using var stream = Avalonia.Platform.AssetLoader.Open(uri); Background = new Bitmap(stream); }
    }
    private void ColorChanged() { if (!loading && Apply()) Save(); }
    partial void OnAccentChanged(string value) => ColorChanged();
    partial void OnSurfaceChanged(string value) => ColorChanged();
    partial void OnCanvasChanged(string value) => ColorChanged();
    partial void OnTextColorChanged(string value) => ColorChanged();
    partial void OnPanelOpacityChanged(double value) { if (!loading) { Apply(); Save(); } }
    partial void OnThemeChanged(string value) { OnPropertyChanged(nameof(IsLumi)); OnPropertyChanged(nameof(ShowCharacter)); }
    partial void OnBlurChanged(double value) { OnPropertyChanged(nameof(BackgroundEffect)); if (!loading) Save(); }
    partial void OnBackgroundOpacityChanged(double value) { if (!loading) Save(); }
    private bool Apply()
    {
        if (Application.Current is not { } app) return false;
        if (!Color.TryParse(Canvas, out var bg) || !Color.TryParse(Surface, out var panel) || !Color.TryParse(Accent, out var accentColor) || !Color.TryParse(TextColor, out var foreground))
        { Error = "Укажите цвета в формате #RRGGBB."; return false; }
        var light = bg.R * .2126 + bg.G * .7152 + bg.B * .0722 > 150;
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        app.Resources["EditorBrush"] = new SolidColorBrush(panel);
        panel = Color.FromArgb((byte)(Math.Clamp(PanelOpacity, .35, 1) * 255), panel.R, panel.G, panel.B);
        app.Resources["OutlineBrush"] = new SolidColorBrush(Color.FromArgb(IsLumi ? (byte)125 : (byte)55, accentColor.R, accentColor.G, accentColor.B));
        app.Resources["AtmosphereBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(light ? Color.Parse("#DCE7F3") : Color.Parse("#263244"), 0), new(bg, .5), new(light ? Color.Parse("#F3E3EF") : Color.Parse("#382839"), 1) }
        };
        foreach (var (key, color) in new[] { ("CanvasBrush", bg), ("PanelBrush", panel), ("SidebarBrush", panel), ("ButtonBrush", light ? Color.Parse("#E6EAF0") : Color.Parse(IsLumi ? "#A03E3355" : "#A0303440")), ("TextBrush", foreground), ("MutedBrush", light ? Color.Parse("#586273") : Color.Parse("#ADB5C4")), ("AccentBrush", accentColor), ("AccentTextBrush", accentColor.R * .2126 + accentColor.G * .7152 + accentColor.B * .0722 > 150 ? Colors.Black : Colors.White) })
            app.Resources[key] = new SolidColorBrush(color);
        app.Resources["SystemAccentColor"] = accentColor;
        Error = "";
        return true;
    }
    private void Save()
    {
        if (!Persist) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(Snapshot())); File.Move(FilePath + ".tmp", FilePath, true); }
        catch (IOException) { Error = "Не удалось сохранить оформление."; }
    }
    private sealed record Preferences(string Theme, string Accent, string Surface, string Canvas, string Text, double Opacity, double Blur, string Image, double PanelOpacity = 0.78, int Schema = 0, string Character = "");
}
