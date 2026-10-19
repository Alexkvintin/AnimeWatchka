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
public sealed partial class SettingsViewModel(MainViewModel host, LibraryRepository repository, ShikimoriService api, SyncService sync, ITokenStore tokens) : ObservableObject
{
    [ObservableProperty]
    private bool playerCompatibility;
    public bool ManualAuthorization => !BrowserCallback.Supported(api.RedirectUri);
    public bool DeveloperSettings => Environment.GetEnvironmentVariable("ANIMEAPP_DEVELOPER_SETTINGS") == "1";
    [RelayCommand]
    private void OpenMikai() => Process.Start(new ProcessStartInfo("https://mikai.me") { UseShellExecute = true });
    [ObservableProperty]
    private string clientId = "";
    [ObservableProperty]
    private string clientSecret = "";
    [ObservableProperty]
    private string redirectUri = "urn:ietf:wg:oauth:2.0:oob";
    [ObservableProperty]
    private string credentialStatus = "Данные OAuth-приложения не заданы";
    [ObservableProperty]
    private bool editingCredentials;
    [ObservableProperty]
    private bool authorizing;
    public bool IsConnected => host.UserId > 0;
    public string DataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp");
    public string Version => "AnimeWatchka · " + typeof(SettingsViewModel).Assembly.GetName().Version!.ToString(3);
    public string ReleaseNotes => "0.1.16\n• Скачивание выбранной серии с выбором качества и отменой.\n• Офлайн-просмотр MP4/WebM/MKV и обычных HLS-серий внутри приложения.\n• Прогресс скачивания виден при переходах между разделами.\n• Очистка незавершённых загрузок после сбоя; готовые серии сохраняются.";

    [RelayCommand]
    private Task ClearPosters() => host.Run(async () =>
    {
        if (PosterService.Shared is PosterService posters)
            await posters.Trim(clear: true);
        host.Message = "Кэш постеров очищен. Постеры открытых карточек могут оставаться в памяти до перезапуска.";
    });
    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(DataPath);
        Process.Start(new ProcessStartInfo(DataPath) { UseShellExecute = true });
    }

    private void RefreshCredentials()
    {
        var c = api.Credentials;
        if (c is null)
            return;
        ClientId = c.Current.ClientId;
        RedirectUri = c.Current.RedirectUri;
        ClientSecret = "";
        CredentialStatus = c.Current.Configured ? $"Client Secret: сохранён · источник: {c.Source}" : "Данные OAuth-приложения не заданы";
        EditingCredentials = !c.Current.Configured;
    }

    [RelayCommand]
    private void EditCredentials()
    {
        ClientSecret = "";
        EditingCredentials = true;
    }

    [RelayCommand]
    private Task SaveCredentials() => host.Run(async () =>
    {
        var manager = api.Credentials ?? throw new InvalidOperationException("Защищённое хранилище недоступно.");
        var old = manager.Current;
        var secret = string.IsNullOrWhiteSpace(ClientSecret) && ClientId.Trim() == old.ClientId ? old.ClientSecret : ClientSecret.Trim();
        var value = new OAuthCredentials(ClientId.Trim(), secret, RedirectUri.Trim());
        await manager.Save(value);
        ClientSecret = "";
        if (old.ClientId != value.ClientId || old.ClientSecret != value.ClientSecret || old.RedirectUri != value.RedirectUri)
            await DisconnectCore();
        RefreshCredentials();
        EditingCredentials = false;
        host.Message = "Данные OAuth-приложения защищены Windows. Можно подключаться без перезапуска.";
    });
    [RelayCommand]
    private Task DeleteCredentials() => host.Run(async () =>
    {
        if (api.Credentials is null)
            return;
        await api.Credentials.Delete();
        await DisconnectCore();
        RefreshCredentials();
        host.Message = "Сохранённые credentials удалены. Если заданы переменные окружения, используется весь комплект из них.";
    });
    [ObservableProperty]
    private bool autoResume = true;
    public string Account => host.Account;
    public int PendingCount => host.PendingCount;
    public ObservableCollection<ProviderViewModel> Providers => host.Sources.Providers;

    public async Task Initialize()
    {
        if (api.Credentials is not null)
        {
            await api.Credentials.Reload();
            RefreshCredentials();
        }

        await InitializeSources();
        PlayerCompatibility = await repository.Setting<bool>("player-compatibility");
        CloseToTray = await repository.Setting<bool>("close-to-tray");
        var s = await repository.Setting<AppSettings>("playback") ?? new();
        AutoMark = s.AutoMark;
        AutoResume = s.AutoResume;
        Threshold = s.Threshold;
        AutoSync = s.AutoSync;
        SavePosition = s.SavePosition;
        Quality = s.Quality;
        LocalFolder = s.LocalFolder;
        var user = await repository.Setting<UserIdentity>("account") ?? await repository.Setting<UserIdentity>("host.Account");
        if (user is not null && await tokens.Read()is not null)
        {
            await repository.SaveSetting("account", user);
            await repository.SaveSetting<UserIdentity?>("host.Account", null);
            host.UserId = user.Id;
            host.Account = user.Nickname;
            host.AccountAvatar = user.Avatar;
        }

        foreach (var p in Providers)
        {
            var ps = await repository.Setting<ProviderSettings>("provider:" + p.Id);
            p.Enabled = ps?.Enabled ?? true;
            p.Priority = ps?.Priority ?? Providers.IndexOf(p);
        }

        var ordered = Providers.OrderBy(p => p.Priority).ToArray();
        for (var i = 0; i < ordered.Length; i++) Providers.Move(Providers.IndexOf(ordered[i]), i);
        host.Sources.RefreshProviders();
        WizardVisible = await repository.Setting<bool?>("wizard-complete") != true && user is null;
        if (user is not null)
            await repository.SaveSetting("wizard-complete", true);
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(IsConnected));
    }

    public string[] Qualities { get; } = ["Auto", "1080p", "720p", "480p"];

    [ObservableProperty]
    private string authCode = "";
    [ObservableProperty]
    private bool autoMark = true;
    [ObservableProperty]
    private int threshold = 90;
    [ObservableProperty]
    private bool autoSync = true;
    [ObservableProperty]
    private bool savePosition = true;
    [ObservableProperty]
    private string quality = "Auto";
    [ObservableProperty]
    private string localFolder = "";
    [RelayCommand]
    private Task Synchronize() => host.Run(async () =>
    {
        host.Message = await sync.Synchronize(host.UserId);
        await host.Reload();
    });
    public ObservableCollection<SyncConflict> Conflicts { get; } = [];
    public ObservableCollection<UnlinkedProgress> UnlinkedProgress { get; } = [];
    public ObservableCollection<Anime> RecoveryCandidates { get; } = [];
    private UnlinkedProgress? recovering;
    [ObservableProperty] private string recoveryMessage = "";
    [RelayCommand] private Task FindUnlinked(UnlinkedProgress? entry) => host.Run(async () =>
    {
        if (entry is null) return;
        recovering = entry; RecoveryCandidates.Clear(); RecoveryMessage = "Ищем тайтл в Shikimori…";
        foreach (var a in await api.Search(entry.Title)) RecoveryCandidates.Add(a);
        RecoveryMessage = RecoveryCandidates.Count > 0 ? "Выберите тот же тайтл и сезон, чтобы восстановить прогресс." : "Совпадений нет. Прогресс остаётся на устройстве.";
    });
    [RelayCommand] private Task RestoreUnlinked(Anime? target) => host.Run(async () =>
    {
        if (target is null || recovering is null || recovering.Change.UserId != host.UserId) return;
        var details = await api.Details(target.ShikimoriId);
        var remote = await api.ReadRate(host.UserId, details);
        await repository.RestoreUnlinked(host.UserId, recovering, remote ?? details);
        RecoveryCandidates.Clear(); RecoveryMessage = ""; recovering = null;
        host.Message = await sync.SendPending(host.UserId);
        await host.Reload();
    });

    internal void RefreshConflicts()
    {
        if (sync.Identity?.Id == host.UserId)
        {
            host.Account = sync.Identity.Nickname + " · подключён";
            host.AccountAvatar = sync.Identity.Avatar;
            OnPropertyChanged(nameof(Account));
        }

        Conflicts.Clear();
        foreach (var c in sync.Conflicts.Where(x => x.Change.UserId == host.UserId))
            Conflicts.Add(c);
    }

    [RelayCommand]
    private Task UseShikimori(SyncConflict? conflict) => Resolve(conflict, false);
    [RelayCommand]
    private Task KeepLocal(SyncConflict? conflict) => Resolve(conflict, true);
    private Task Resolve(SyncConflict? conflict, bool keep) => host.Run(async () =>
    {
        if (conflict is null)
            return;
        await sync.Resolve(host.UserId, conflict, keep);
        await host.Reload();
        host.Message = "Решение применено к выбранному тайтлу и проверено на Shikimori.";
    });
    [RelayCommand]
    private Task Authorize() => host.Run(async () =>
    {
        if (BrowserCallback.Supported(api.RedirectUri))
        {
            using var callback = new BrowserCallback(api.RedirectUri);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            Authorizing = true;
            try
            {
                Process.Start(new ProcessStartInfo(api.AuthorizationUrl(callback.State)) { UseShellExecute = true });
                host.Message = "Подтвердите вход в браузере.";
                AuthCode = await callback.Receive(timeout.Token);
                await ConnectCore();
            }
            finally
            {
                Authorizing = false;
                AuthCode = "";
            }

            return;
        }

        Process.Start(new ProcessStartInfo(api.AuthorizationUrl()) { UseShellExecute = true });
        Authorizing = true;
        host.Message = "Разрешите доступ в браузере и вставьте полученный код ниже.";
    });
    [RelayCommand]
    private Task Connect() => host.Run(ConnectCore);
    private async Task ConnectCore()
    {
        await api.Connect(AuthCode);
        var user = await api.CurrentUser();
        host.UserId = user.Id;
        host.ResetAccountView();
        host.Account = user.Nickname;
        host.AccountAvatar = user.Avatar;
        OnPropertyChanged(nameof(Account));
        AuthCode = "";
        Authorizing = false;
        OnPropertyChanged(nameof(IsConnected));
        await repository.SaveSetting("account", user);
        await host.Reload();
        host.Message = await sync.Synchronize(host.UserId);
        await host.Reload();
    }

    [RelayCommand]
    private Task Disconnect() => host.Run(DisconnectCore);
    private async Task DisconnectCore()
    {
        RecoveryCandidates.Clear(); UnlinkedProgress.Clear(); RecoveryMessage = ""; recovering = null;
        await tokens.Clear();
        await repository.SaveSetting<UserIdentity?>("account", null);
        await repository.SaveSetting<UserIdentity?>("host.Account", null);
        host.UserId = 0;
        host.ResetAccountView();
        host.Account = "Аккаунт не подключён";
        host.AccountAvatar = "";
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(IsConnected));
        Authorizing = false;
        AuthCode = "";
        await host.Reload();
        host.Message = "Аккаунт отключён. Его кэш и очередь сохранены отдельно.";
    }

    [RelayCommand]
    private Task SaveSettings() => host.Run(SaveSettingsCore);
    private async Task SaveSettingsCore()
    {
        if (Threshold < 1 || Threshold > 100)
            throw new InvalidOperationException("Порог просмотра: от 1 до 100%.");
        await repository.SaveSetting("player-compatibility", PlayerCompatibility);
        await repository.SaveSetting("video-scaling", Scaling.Id);
        await repository.SaveSetting("preferred-languages", PreferredLanguages);
        await repository.SaveSetting("preferred-provider", PreferredProvider?.Id ?? "");
        host.Sources.PreferredProviderId = PreferredProvider?.Id ?? "";
        host.Sources.PreferredLanguage = PreferredLanguages;
        await repository.SaveSetting("playback", new AppSettings(AutoMark, Threshold, AutoSync, SavePosition, Quality, LocalFolder, AutoResume));
        foreach (var p in host.Sources.Providers)
            await repository.SaveSetting("provider:" + p.Id, new ProviderSettings(p.Id, p.Enabled, p.Priority));
        host.Sources.RefreshProviders();
        host.Message = "Настройки сохранены.";
    }

    [RelayCommand]
    private Task CheckProviders() => host.Run(async () =>
    {
        foreach (var p in host.Sources.Providers)
            p.Availability = (await p.Provider.CheckAvailability()).Message;
        host.Message = "Проверка источников завершена.";
    });
}


