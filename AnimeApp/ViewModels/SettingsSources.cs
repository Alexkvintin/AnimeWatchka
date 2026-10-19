using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using AnimeApp.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeApp.ViewModels;
public sealed partial class SettingsViewModel
{
    private static readonly HttpClient probeHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private SourceProbeResult? probe;
    public ScalingMode[] ScalingModes => VideoScaling.Modes;

    [ObservableProperty]
    private ScalingMode scaling = VideoScaling.Modes[0];
    [ObservableProperty]
    private string preferredLanguages = "uk,ru,any";
    [ObservableProperty]
    private ProviderViewModel? preferredProvider;
    public IEnumerable<ProviderViewModel> PlaybackChoices => Providers.Where(p => !p.IsWeb);

    [ObservableProperty]
    private string sourceUrl = "";
    [ObservableProperty]
    private string sourceName = "";
    [ObservableProperty]
    private string sourceLanguage = "unknown";
    [ObservableProperty]
    private string probeMessage = "Введите адрес страницы. Поддержка сайтов зависит от доступных адаптеров.";
    [ObservableProperty]
    private bool canAddSource;
    [ObservableProperty]
    private bool wizardVisible;
    [ObservableProperty]
    private bool wizardSources;
    public string[] Languages { get; } = ["ru", "uk", "ja", "multi", "unknown"];
    public MetadataChoice[] MetadataChoices { get; } = [new("shikimori", "Shikimori", true), new("myanimelist", "MyAnimeList", false), new("later", "Позже", true)];

    [ObservableProperty]
    private MetadataChoice? selectedMetadata;
    private async Task InitializeSources()
    {
        var scalingId = await repository.Setting<string>("video-scaling");
        Scaling = ScalingModes.FirstOrDefault(m => m.Id == scalingId) ?? ScalingModes[0];
        PreferredLanguages = await repository.Setting<string>("preferred-languages") ?? "uk,ru,any";
        host.Sources.PreferredLanguage = PreferredLanguages;
        var preferred = await repository.Setting<string>("preferred-provider");
        PreferredProvider = Providers.FirstOrDefault(p => p.Id == preferred);
        host.Sources.PreferredProviderId = preferred ?? "";
        var definitions = await repository.Setting<List<WebSourceDefinition>>("web-sources") ?? [];
        foreach (var d in definitions)
            host.Sources.Register(new GenericWebProvider(d, probeHttp));
        foreach (var d in TemplateStore.Load())
            host.Sources.Register(new TemplateProvider(d, probeHttp));
        var metadata = await repository.Setting<string>("metadata-source") ?? "shikimori";
        SelectedMetadata = MetadataChoices.FirstOrDefault(m => m.Id == metadata) ?? MetadataChoices[0];
    }

    partial void OnSourceUrlChanged(string value)
    {
        probe = null;
        CanAddSource = false;
    }

    [RelayCommand]
    private Task ProbeSource() => host.Run(async () =>
    {
        var url = SourceUrl.Trim();
        probe = await new SourceProbeService(probeHttp).Probe(url);
        if (SourceUrl.Trim() != url)
            return;
        ProbeMessage = probe.Message;
        SourceName = probe.Title;
        CanAddSource = probe.Support is SourceSupport.Web or SourceSupport.Partial;
        if (probe.Support == SourceSupport.Known)
        {
            var p = Providers.FirstOrDefault(p => p.Id == probe.ProviderId);
            if (p is not null)
                p.Enabled = true;
        }
    });
    [RelayCommand]
    private Task AddSource() => host.Run(async () =>
    {
        if (probe?.Support is not (SourceSupport.Web or SourceSupport.Partial) || probe.Url != SourceUrl.Trim())
            return;
        var definitions = await repository.Setting<List<WebSourceDefinition>>("web-sources") ?? [];
        if (definitions.Any(d => d.Url == probe.Url))
        {
            ProbeMessage = "Эта страница уже сохранена.";
            return;
        }

        var d = new WebSourceDefinition("web-" + Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(SourceName) ? probe.Title : SourceName.Trim(), probe.Url, SourceLanguage);
        definitions.Add(d);
        await repository.SaveSetting("web-sources", definitions);
        host.Sources.Register(new GenericWebProvider(d, probeHttp));
        host.Sources.RefreshProviders();
        CanAddSource = false;
        ProbeMessage = "Веб-источник сохранён. Откройте страницу кнопкой в списке.";
    });
    [RelayCommand]
    private Task OpenWeb(ProviderViewModel? provider) => provider is null ? Task.CompletedTask : host.Run(() => host.Sources.OpenWeb(provider));
    [RelayCommand]
    private void OpenWizard()
    {
        WizardSources = false;
        WizardVisible = true;
    }

    [RelayCommand]
    private void WizardNext()
    {
        if (SelectedMetadata?.Supported == true)
            WizardSources = true;
    }

    [RelayCommand]
    private Task FinishWizard() => host.Run(async () =>
    {
        if (SelectedMetadata?.Supported != true)
            return;
        await SaveSettingsCore();
        await repository.SaveSetting("metadata-source", SelectedMetadata.Id);
        await repository.SaveSetting("wizard-complete", true);
        WizardVisible = false;
        if (SelectedMetadata.Id != "later")
            host.Page = "Настройки";
    });
}
