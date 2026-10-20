using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AnimeApp.ViewModels;
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly LibraryRepository repository;
    private readonly PlaybackService playback;
    private readonly SyncService sync;
    private readonly IAnimeProvider provider;
    private readonly ILogger logger;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private WatchTracker tracker = new();
    private AppSettings settings = new();
    private DateTimeOffset lastSave = DateTimeOffset.MinValue;
    private bool resumeApplied;
    private bool seekPending;
    private bool closed;
    private bool marked;
    private Task syncTask = Task.CompletedTask;
    public IPlayerEngine Engine { get; }
    public PlaybackRequest Request { get; private set; }
    public IReadOnlyList<ProviderEpisode> Episodes { get; }

    public event Action? CloseRequested;
    public event Action? FullscreenRequested;
    [ObservableProperty]
    private string title = "";
    [ObservableProperty]
    private string episodeLabel = "";
    [ObservableProperty]
    private string status = "Загрузка видео…";
    [ObservableProperty]
    private double position;
    [ObservableProperty]
    private double duration;
    [ObservableProperty]
    private double seekPosition;
    [ObservableProperty]
    private int volume = 70;
    [ObservableProperty]
    private double speed = 1;
    public string SpeedLabel => $"{Speed:0.##}×";

    partial void OnSpeedChanged(double value)
    {
        Engine.SetSpeed(value);
        OnPropertyChanged(nameof(SpeedLabel));
    }

    public ScalingMode[] ScalingModes { get; } = [new("hq", "Повышение чёткости"), new("anime", "Anime4K")];

    [ObservableProperty]
    private ScalingMode? selectedScaling;
    [ObservableProperty]
    private bool scalingEnabled = true;
    public bool CanChangeScaling => Engine is MpvPlayerEngine { Fallback: null };
    public string ScalingHint => CanChangeScaling ? "Обработка изображения; не меняет разрешение потока и расход трафика." : "В совместимом режиме VLC недоступны фильтры mpv / Anime4K. Попробуйте обновить драйвер встроенной видеокарты и выключить совместимый режим в настройках воспроизведения. Если видео не работает, верните совместимый режим.";

    partial void OnSelectedScalingChanged(ScalingMode? value) => ApplyScaling();
    partial void OnScalingEnabledChanged(bool value) => ApplyScaling();
    private void ApplyScaling()
    {
        if (Engine is MpvPlayerEngine mpv)
            mpv.SetScaling(ScalingEnabled ? SelectedScaling?.Id ?? "hq" : "linear");
    }

    [ObservableProperty]
    private bool playing;
    [ObservableProperty]
    private bool canControl = true;
    [ObservableProperty]
    private bool showNext;
    [ObservableProperty]
    private bool canComplete;
    [ObservableProperty]
    private bool loading;
    [ObservableProperty]
    private bool showResume;
    [ObservableProperty]
    private bool showOptions;
    public bool ShowStatus => Status != "Приятного просмотра" && !string.IsNullOrWhiteSpace(Status);
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(ShowStatus));
    public string QualityInfo { get; private set; } = "";
    public string TimeLabel => $"{Format(Position)} / {Format(Duration)}";
    public string ResumeLabel => $"Продолжить с {Format(Request.ResumeSeconds)}";
    public bool HasNext => Index >= 0 && Index + 1 < Episodes.Count;
    public bool HasPrevious => Index > 0;
    private int Index => Episodes.ToList().FindIndex(e => e.Id == Request.EpisodeId);

    private static string Format(double value) => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(value >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public PlayerViewModel(PlaybackRequest request, IReadOnlyList<ProviderEpisode> episodes, IAnimeProvider provider, IPlayerEngine engine, LibraryRepository repository, PlaybackService playback, SyncService sync, ILogger logger)
    {
        Request = request;
        Episodes = episodes;
        this.provider = provider;
        Engine = engine;
        selectedScaling = ScalingModes.FirstOrDefault(s => s.Id == (engine as MpvPlayerEngine)?.ScalingMode) ?? ScalingModes[0];
        scalingEnabled = engine is MpvPlayerEngine m && m.ScalingMode != "linear";
        this.repository = repository;
        this.playback = playback;
        this.sync = sync;
        this.logger = logger;
    }

    public async Task Initialize()
    {
        await transitionGate.WaitAsync();
        try
        {
            if (closed)
                return;
            settings = await repository.Setting<AppSettings>("playback") ?? new();
            await OpenCurrent();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public void ReportFailure(Exception ex)
    {
        logger.LogWarning("Player operation failed: {Type}", ex.GetType().Name);
        Status = "Не удалось выполнить действие проигрывателя. Попробуйте открыть серию заново.";
    }

    private async Task OpenCurrent()
    {
        Loading = true;
        try
        {
            var history = (await repository.History(Request.UserId)).FirstOrDefault(e => e.AnimeId == Request.ShikimoriId && e.ProviderId == Request.ProviderId && e.ReleaseId == Request.ReleaseId && e.EpisodeId == Request.EpisodeId);
            Request = Request with
            {
                ResumeSeconds = settings.SavePosition && history?.Completed != true ? history?.Position ?? Request.ResumeSeconds : 0
            };
            qualityRecovery = null;
            tracker = new(history?.WatchedRanges);
            marked = history?.Completed == true;
            resumeApplied = false;
            seekPending = true;
            Position = 0;
            Duration = 0;
            ShowNext = false;
            CanComplete = false;
            Title = Request.Title;
            var episodeName = Request.Episode > 0 ? $"Серия {Request.Episode}" : Episodes.FirstOrDefault(e => e.Id == Request.EpisodeId)?.Title ?? "Спецвыпуск";
            if (provider.Id == "subtitles")
                episodeName = Episodes.FirstOrDefault(e => e.Id == Request.EpisodeId)?.Title ?? episodeName;
            EpisodeLabel = $"{provider.DisplayName}  •  {episodeName}  •  {Index + 1} из {Episodes.Count}";
            ShowResume = !settings.AutoResume && Request.ResumeSeconds > 3;
            OnPropertyChanged(nameof(ResumeLabel));
            OnPropertyChanged(nameof(HasNext));
            OnPropertyChanged(nameof(HasPrevious));
            var source = Request.Source;
            RefreshQualities();
            var variant = QualityNegotiation.Select(source.Variants, qualityPreference ?? settings.Quality);
            if (variant is not null)
                source = source with
                {
                    Location = variant.Location
                };
            var max = Request.Source.Variants?.OrderByDescending(v => v.Height).FirstOrDefault();
            QualityInfo = $"{provider.DisplayName} · выбран поток: {variant?.Quality ?? "Auto"}" + (max is null ? "" : $" · максимум источника: {max.Quality}");
            await Engine.Open(source);
            SubtitleDelay = 0;
            SubtitleControls.Delay(Engine, 0);
            RefreshSubtitles();
            Engine.SetVolume(Volume);
            Engine.SetSpeed(Speed);
            Status = "Загрузка видео…";
            lastSave = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Player open failed: {Type}", ex.GetType().Name);
            Status = ex is IOException or InvalidOperationException ? ex.Message : "Не удалось открыть видео.";
        }
        finally
        {
            Loading = false;
        }
    }

    public async Task Tick()
    {
        if (closed || Loading || qualityRecovery is not null)
            return;
        var s = Engine.Snapshot;
        RefreshSubtitles();
        Position = s.Position;
        Duration = s.Duration;
        Playing = s.Playing;
        CanControl = s.CanControl;
        SeekPosition = s.Position;
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(TimeLabel));
        if (s.Error is not null)
        {
            Status = s.Error;
            return;
        }

        if (!s.CanControl)
        {
            Status = "Веб-плеер: используйте его элементы управления. Телеметрия доступна только для поддерживаемого HTML5-видео.";
            return;
        }

        if (!resumeApplied && s.Duration > 0)
        {
            resumeApplied = true;
            if (settings.AutoResume && Request.ResumeSeconds > 3)
            {
                Engine.Seek(Math.Min(Request.ResumeSeconds, Math.Max(0, s.Duration - 1)));
                seekPending = true;
            }
        }

        tracker.Observe(s.Position, s.Playing, s.Seeking || seekPending, DateTimeOffset.UtcNow, Speed);
        seekPending = false;
        if (s.Playing && Status == "Загрузка видео…")
            Status = "Приятного просмотра";
        if (s.Ended)
        {
            ShowNext = HasNext;
            Status = HasNext ? "Серия закончилась. Можно перейти к следующей." : "Новых серий у этого источника пока нет.";
        }

        var reached = settings.AutoMark && tracker.Eligible(s.Duration, settings.Threshold) && s.Position / s.Duration * 100 >= settings.Threshold;
        if (DateTimeOffset.UtcNow - lastSave >= TimeSpan.FromSeconds(10) || reached && !marked)
            await Save();
    }

    public async Task Suspend()
    {
        await transitionGate.WaitAsync();
        try { if (!closed) { Engine.Pause(); await Save(); } }
        finally { transitionGate.Release(); }
    }

    public async Task Save()
    {
        await saveGate.WaitAsync();
        try
        {
            var s = qualityRecovery ?? Engine.Snapshot;
            if (s.Duration <= 0 && s.Position <= 0)
                return;
            var eligible = tracker.Eligible(s.Duration, settings.Threshold);
            var changed = await playback.ReportProgress(Request, s.Position, s.Duration, eligible: eligible, ranges: tracker.Ranges);
            lastSave = DateTimeOffset.UtcNow;
            if (settings.AutoMark && eligible && s.Duration > 0 && s.Position / s.Duration * 100 >= settings.Threshold && Request.CountsTowardProgress)
            {
                marked = true;
                ShowNext = HasNext;
                Status = $"Серия {Request.Episode} просмотрена · сохранено локально";
                var anime = (await repository.Library(Request.UserId)).FirstOrDefault(a => a.ShikimoriId == Request.ShikimoriId);
                CanComplete = anime?.Status == "released" && anime.EpisodesTotal > 0 && Request.Episode == anime.EpisodesTotal && anime.UserStatus != "completed";
            }

            if (changed && settings.AutoSync && Request.UserId > 0 && syncTask.IsCompleted)
                syncTask = SyncInBackground();
        }
        catch (Exception ex)
        {
            logger.LogError("Save playback failed: {Type}", ex.GetType().Name);
            Status = "Не удалось сохранить позицию на диск. Проверьте свободное место.";
        }
        finally
        {
            saveGate.Release();
        }
    }

    private async Task SyncInBackground()
    {
        try
        {
            var message = await sync.SendPending(Request.UserId);
            Status = message;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Playback sync pending: {Type}", ex.GetType().Name);
            Status = "Просмотр сохранён локально · Shikimori: ожидает синхронизации";
        }
    }

    public string PlayLabel => Engine.Snapshot.Ended && HasNext ? "▶ Следующая серия" : Playing ? "⏸ Пауза" : "▶ Смотреть";
    partial void OnShowNextChanged(bool value) => OnPropertyChanged(nameof(PlayLabel));

    partial void OnVolumeChanged(int value)
    {
        if (!closed)
            Engine.SetVolume(value);
    }

    [RelayCommand]
    private async Task TogglePlay()
    {
        if (Loading || closed) return;
        if (Engine.Snapshot.Ended && HasNext)
        {
            await Move(1);
            return;
        }
        if (Engine.Snapshot.Playing)
        {
            Engine.Pause();
            await Save();
        }
        else
            Engine.Play();
    }

    [RelayCommand]
    private void Seek()
    {
        if (Loading || closed) return;
        Engine.Seek(SeekPosition);
        seekPending = true;
    }

    [RelayCommand]
    private void SkipOpening()
    {
        var snapshot = Engine.Snapshot;
        SeekPosition = snapshot.Duration > 0 ? Math.Min(snapshot.Duration, snapshot.Position + 90) : snapshot.Position + 90;
        Seek();
        Status = "Пропущено до 90 секунд · границы опенинга источник не сообщает";
    }

    [RelayCommand]
    private void Resume()
    {
        Engine.Seek(Request.ResumeSeconds);
        seekPending = true;
        ShowResume = false;
    }

    [RelayCommand]
    private void Fullscreen() => FullscreenRequested?.Invoke();
    [RelayCommand]
    private void Back() => CloseRequested?.Invoke();
    [RelayCommand]
    private Task Next() => Move(1);
    [RelayCommand]
    private Task Previous() => Move(-1);
    private async Task Move(int direction)
    {
        if (!await transitionGate.WaitAsync(0))
            return;
        try
        {
            if (closed)
                return;
            var index = Index + direction;
            if (index < 0 || index >= Episodes.Count)
            {
                Status = "Новых серий у этого источника пока нет.";
                return;
            }

            Loading = true;
            Engine.Pause();
            await Save();
            var e = Episodes[index];
            var source = await DownloadLibrary.Resolve(repository, provider, Request.ReleaseId, e);
            if (closed)
                return;
            if ((source.Kind == PlaybackKind.WebPage) != (Request.Source.Kind == PlaybackKind.WebPage))
                throw new InvalidOperationException("Следующая серия требует другого проигрывателя. Откройте её со страницы аниме.");
            Request = Request with
            {
                Episode = e.Number,
                EpisodeId = e.Id,
                Source = source,
                ResumeSeconds = 0,
                CountsTowardProgress = Request.ShikimoriId > 0 && e.CountsTowardProgress
            };
            await OpenCurrent();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Next episode failed: {Type}", ex.GetType().Name);
            Status = ex.Message;
        }
        finally
        {
            Loading = false;
            transitionGate.Release();
        }
    }

    [RelayCommand]
    private void DismissComplete() => CanComplete = false;
    [RelayCommand]
    private async Task Complete()
    {
        var anime = (await repository.Library(Request.UserId)).FirstOrDefault(a => a.ShikimoriId == Request.ShikimoriId);
        if (anime is null || anime.Status != "released" || anime.EpisodesTotal <= 0 || anime.EpisodesWatched < anime.EpisodesTotal)
            return;
        await repository.Change(Request.UserId, anime, anime with { UserStatus = "completed" });
        CanComplete = false;
        Status = "Аниме отмечено просмотренным";
        if (settings.AutoSync && Request.UserId > 0)
            syncTask = SyncInBackground();
    }

    public async Task Close(Action? detachSurface = null)
    {
        if (closed)
            return;
        closed = true;
        qualityCancellation?.Cancel();
        await transitionGate.WaitAsync();
        try
        {
            Engine.Pause();
            await Save();
            // Native video controls must release their media-player reference before disposal.
            detachSurface?.Invoke();
            await Engine.DisposeAsync();
        }
        finally
        {
            transitionGate.Release();
        }
    }
}

