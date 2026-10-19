using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Shikimori;
using AnimeApp.Services.Playback;
using Microsoft.Extensions.Logging;

namespace AnimeApp.ViewModels;
public sealed partial class ProviderViewModel : ObservableObject
{
    public IAnimeProvider Provider { get; }
    public string Id => Provider.Id;
    public string Name => Provider.DisplayName;
    public string Language => Provider.Language;
    public string Url => Provider.Url;
    public string Capabilities => Provider.Capabilities;
    public bool IsWeb => Provider.Features.HasFlag(ProviderCapabilities.WebPlayback) && !Provider.Features.HasFlag(ProviderCapabilities.Episodes);

    [ObservableProperty]
    private bool enabled = true;
    [ObservableProperty]
    private int priority;
    [ObservableProperty]
    private string availability = "Не проверено";
    public ProviderViewModel(IAnimeProvider provider)
    {
        Provider = provider;
    }

    public override string ToString() => Name;
}
