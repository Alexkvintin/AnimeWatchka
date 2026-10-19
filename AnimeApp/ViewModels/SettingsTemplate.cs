using AnimeApp.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeApp.ViewModels;
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private string templateSearch = "";
    [ObservableProperty]
    private string templateRelease = "";
    [ObservableProperty]
    private string templateEpisodes = "";
    [ObservableProperty]
    private string templatePoster = "";
    [ObservableProperty]
    private string templateVideo = "video source[src], video[src]";
    [ObservableProperty]
    private string templateQuery = "";
    [ObservableProperty]
    private string templateMessage = "Только обычный HTML. CSS-селекторы не выполняют код. Нумерация серий требует проверки; автоотметка выключена.";
    [ObservableProperty]
    private bool templateVerified;
    private TemplateDefinition? testedTemplate;
    private TemplateDefinition Definition(string id) => new(id, SourceName.Trim(), SourceUrl.Trim(), TemplateSearch.Trim(), TemplateRelease.Trim(), TemplateEpisodes.Trim(), TemplatePoster.Trim(), TemplateVideo.Trim(), SourceLanguage);
    [RelayCommand]
    private Task TestTemplate() => host.Run(async () =>
    {
        TemplateVerified = false;
        testedTemplate = null;
        var d = Definition("template-" + Guid.NewGuid().ToString("N"));
        d.Validate();
        if (string.IsNullOrWhiteSpace(TemplateQuery))
            throw new ArgumentException("Введите название для проверочного поиска.");
        var provider = new TemplateProvider(d, probeHttp);
        var releases = await provider.Search(TemplateQuery);
        if (releases.Count == 0)
        {
            TemplateMessage = "Селектор релизов не нашёл результатов.";
            return;
        }

        var release = await provider.GetRelease(releases[0].Id);
        var episodes = await provider.GetEpisodes(release.Id);
        if (episodes.Count == 0)
        {
            TemplateMessage = "Релиз найден, но селектор серий не сработал.";
            return;
        }

        await provider.GetPlayback(release.Id, episodes[0]);
        testedTemplate = d;
        TemplateVerified = true;
        TemplateMessage = $"Проверено: {release.Title}; релизов {releases.Count}, серий {episodes.Count}. Прямая HTML5-ссылка найдена; воспроизведение отдельно проверяется плеером.";
    });
    [RelayCommand]
    private Task SaveTemplate() => host.Run(async () =>
    {
        if (testedTemplate is null || Definition(testedTemplate.Id) != testedTemplate)
        {
            TemplateVerified = false;
            TemplateMessage = "Настройки изменились: повторите проверку.";
            return;
        }

        await TemplateStore.Save(testedTemplate);
        host.Sources.Register(new TemplateProvider(testedTemplate, probeHttp));
        host.Sources.RefreshProviders();
        TemplateVerified = false;
        TemplateMessage = "Адаптер сохранён отдельно от веб-источников.";
    });
}
