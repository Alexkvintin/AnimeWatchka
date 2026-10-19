namespace AnimeApp.Models;
public static class StatusLabels
{
    public static string Get(string? value) => value switch
    {
        "watching" => "Смотрю",
        "planned" => "Запланировано",
        "completed" => "Завершено",
        "on_hold" => "Отложено",
        "dropped" => "Брошено",
        "rewatching" => "Пересматриваю",
        "released" => "Вышло",
        "ongoing" or "airing" => "Выходит",
        "anons" => "Анонс",
        _ => "Не указан"
    };
}
