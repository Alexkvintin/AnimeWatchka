using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Shikimori;
using AnimeApp.Services.Providers;
using AnimeApp.Services.Playback;
using AnimeApp.Providers;
using AnimeApp.Providers.AnimeVost;
using AnimeApp.ViewModels;
using AnimeApp.UI;

namespace AnimeApp;
public static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed class App : Application
{
    private ServiceProvider? services;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public static ServiceProvider CreateServices(string? directory = null)
    {
        var path = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp");
        var s = new ServiceCollection();
        s.AddLogging(b => b.AddConsole().AddProvider(new Utils.FileLoggerProvider(Path.Combine(path, "app.log")))).AddSingleton(new DatabaseService(Path.Combine(path, "library.db"))).AddSingleton<LibraryRepository>().AddSingleton(OAuthOptions.Load()).AddSingleton<ICredentialStore>(new WindowsCredentialStore(Path.Combine(path, "credentials.dat"))).AddSingleton<CredentialManager>(sp => new CredentialManager(sp.GetRequiredService<ICredentialStore>())).AddSingleton<ITokenStore>(new WindowsTokenStore(Path.Combine(path, "tokens.dat"))).AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }).AddSingleton<ShikimoriService>().AddSingleton<IShikimoriService>(sp => sp.GetRequiredService<ShikimoriService>()).AddSingleton<IUserLibraryProvider>(sp => sp.GetRequiredService<ShikimoriService>()).AddSingleton<IMetadataProvider>(sp => sp.GetRequiredService<ShikimoriService>()).AddSingleton<IScheduleProvider>(sp => sp.GetRequiredService<ShikimoriService>()).AddSingleton<SyncService>().AddSingleton<IAnimeVostParser, AnimeVostParser>().AddSingleton<IAnimeProvider, AnimeVostProvider>().AddSingleton<IAnimeProvider, AnimeApp.Providers.AniLibria.AniLibriaProvider>().AddSingleton<AnimeApp.Providers.Mikai.MikaiClient>().AddSingleton<IAnimeProvider>(sp => new AnimeApp.Providers.Mikai.MikaiProvider(sp.GetRequiredService<AnimeApp.Providers.Mikai.MikaiClient>(), "glass-moon", "Glass Moon", 3)).AddSingleton<IAnimeProvider>(sp => new AnimeApp.Providers.Mikai.MikaiProvider(sp.GetRequiredService<AnimeApp.Providers.Mikai.MikaiClient>(), "fanvoxua", "FanVoxUA", 2)).AddSingleton<IAnimeProvider, AnimeApp.Providers.Mikai.SubtitlesProvider>().AddSingleton<IAnimeProvider, LocalFilesProvider>().AddSingleton<ProviderRegistry>().AddSingleton<EmbeddedPlaybackLauncher>().AddSingleton<IPlaybackLauncher>(sp => sp.GetRequiredService<EmbeddedPlaybackLauncher>()).AddSingleton<PlaybackService>().AddSingleton<MainViewModel>();
        return s.BuildServiceProvider();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppearanceViewModel.Current.Load();
            services = CreateServices();
            var vm = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow
            {
                DataContext = vm
            };
            desktop.MainWindow = window;
            PlayerWindow? activePlayer = null;
            bool closingRoot = false;
            TrayController? tray = null;
            if (OperatingSystem.IsWindows())
                tray = new TrayController(window, () => vm.Settings.CloseToTray,
                    async () => { if (activePlayer is not null) await activePlayer.SuspendToTray(); },
                    () => activePlayer?.RestoreFromTray());
            services.GetRequiredService<EmbeddedPlaybackLauncher>().OpenRequested = async (request, ct) =>
            {
                if (activePlayer is not null)
                {
                    activePlayer.Activate();
                    throw new InvalidOperationException("Сначала закройте текущий просмотр.");
                }

                IAnimeProvider provider = request.ProviderId == "downloads" ? new DownloadedEpisodeProvider() : services.GetRequiredService<ProviderRegistry>().Get(request.ProviderId);
                var episodes = request.Source.Kind == PlaybackKind.LocalFile && request.ProviderId != "downloads" && vm.Sources.SelectedRelease?.Id == request.ReleaseId && vm.Sources.SelectedProvider?.Id == request.ProviderId
                    ? vm.Sources.Episodes.ToArray() : await provider.GetEpisodes(request.ReleaseId, ct);
                var scaling = await services.GetRequiredService<LibraryRepository>().Setting<string>("video-scaling", ct) ?? "linear";
                var compatibility = await services.GetRequiredService<LibraryRepository>().Setting<bool>("player-compatibility", ct);
                var engine = PlayerEngineFactory.Create(request.Source.Kind, scaling, compatibility);
                var playerVm = new PlayerViewModel(request, episodes, provider, engine, services.GetRequiredService<LibraryRepository>(), services.GetRequiredService<PlaybackService>(), services.GetRequiredService<SyncService>(), services.GetRequiredService<ILogger<PlayerViewModel>>());
                try
                {
                    activePlayer = new PlayerWindow(playerVm);
                    activePlayer.Closed += async (_, _) =>
                    {
                        activePlayer = null;
                        if (!closingRoot)
                            await vm.Run(vm.Reload);
                    };
                    activePlayer.Show(window);
                    activePlayer.Activate();
                }
                catch
                {
                    activePlayer = null;
                    await engine.DisposeAsync();
                    throw;
                }
            };
            window.Closing += async (_, e) =>
            {
                if (e.Cancel) return;
                if (activePlayer is not null && !closingRoot)
                {
                    e.Cancel = true;
                    closingRoot = true;
                    await activePlayer.Shutdown();
                    window.Close();
                }
            };
            var retry = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(2)
            };
            retry.Tick += async (_, _) => await vm.RetryPending();
            window.Closed += (_, _) => retry.Stop();
            var startupStarted = false;
            window.Opened += async (_, _) =>
            {
                if (startupStarted) return;
                startupStarted = true;
                try
                {
                    await services.GetRequiredService<DatabaseService>().Initialize();
                    try
                    {
                        if (PosterService.Shared is PosterService posters)
                            await posters.Trim();
                    }
                    catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
                    {
                        services.GetRequiredService<ILogger<App>>().LogWarning("Poster cache cleanup unavailable: {Type}", ex.GetType().Name);
                    }

                    await vm.Initialize();
                    retry.Start();
                }
                catch (Exception ex)
                {
                    services.GetRequiredService<ILogger<App>>().LogError(ex, "Startup failed");
                    vm.Message = "Ошибка запуска: " + ex.Message;
                }
            };
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}


