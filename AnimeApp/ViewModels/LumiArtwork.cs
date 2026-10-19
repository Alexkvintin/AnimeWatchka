using Avalonia.Media.Imaging;
using Avalonia.Platform;
namespace AnimeApp.ViewModels;
public sealed partial class MainViewModel
{
    private static readonly Dictionary<string,Bitmap> LumiImages = new();
    public Bitmap LumiImage
    {
        get
        {
            var pose = Page switch {
                "Поиск" => "search", "Настройки" => "settings", "Онгоинги" => "schedule",
                "История" => "history", "Запланировано" or "Отложено" => "planned",
                "Смотрю" or "Пересматриваю" or "Аниме" => "watching", _ => "collection"
            };
            if (!LumiImages.TryGetValue(pose,out var bitmap))
            {
                using var stream=AssetLoader.Open(new Uri($"avares://AnimeApp/Assets/Lumi/{pose}.png"));
                bitmap=Bitmap.DecodeToWidth(stream,320);
                LumiImages[pose]=bitmap;
            }
            return bitmap;
        }
    }
}
