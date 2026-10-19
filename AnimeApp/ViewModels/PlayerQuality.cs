using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
namespace AnimeApp.ViewModels;
public sealed partial class PlayerViewModel
{
    public ObservableCollection<string> StreamQualities { get; } = [];
    [ObservableProperty] private string? selectedStreamQuality;
    private string? qualityPreference;
    private PlaybackSnapshot? qualityRecovery;
    private CancellationTokenSource? qualityCancellation;
    public bool HasStreamQualities => StreamQualities.Count > 1 && Request.Source.VideoTrack is null;
    private void RefreshQualities()
    {
        StreamQualities.Clear();
        foreach(var q in (Request.Source.Variants ?? []).OrderByDescending(v=>v.Height).Select(v=>v.Quality).Distinct()) StreamQualities.Add(q);
        SelectedStreamQuality = QualityNegotiation.Select(Request.Source.Variants, qualityPreference ?? settings.Quality)?.Quality;
        OnPropertyChanged(nameof(HasStreamQualities));
    }
    [RelayCommand]
    private async Task ApplyStreamQuality()
    {
        if (!HasStreamQualities || Loading || closed || !await transitionGate.WaitAsync(0)) return;
        var before=Engine.Snapshot;
        var previous=QualityNegotiation.Select(Request.Source.Variants,qualityPreference ?? settings.Quality);
        try
        {
            if (qualityRecovery is not null) { Status="Откройте серию заново: предыдущий поток не восстановлен."; return; }
            var target=Request.Source.Variants?.FirstOrDefault(v=>v.Quality==SelectedStreamQuality);
            if (target is null || target.Location==previous?.Location) return;
            qualityCancellation=new CancellationTokenSource();
            var token=qualityCancellation.Token;
            Loading=true;
            Engine.Pause();
            await Save();
            qualityRecovery = before;
            resumeApplied = true;
            seekPending = true;
            async Task OpenVariant(MediaVariant variant)
            {
                await Engine.Open(Request.Source with { Location=variant.Location, StartSeconds=before.Position, StartPaused=!before.Playing }, token);
                Engine.SetVolume(Volume); Engine.SetSpeed(Speed);
                var deadline=DateTimeOffset.UtcNow.AddSeconds(15);
                while (true)
                {
                    var current=Engine.Snapshot;
                    if (current.Error is not null) throw new IOException("Не удалось загрузить выбранное качество.");
                    if (current.Duration>0 && Math.Abs(current.Position-before.Position)<5 && current.Playing==before.Playing) { qualityRecovery=null; break; }
                    if (DateTimeOffset.UtcNow>=deadline) throw new IOException("Источник не загрузил выбранное качество вовремя.");
                    await Task.Delay(100,token);
                }
            }
            try { await OpenVariant(target); }
            catch
            {
                if (!closed && previous is not null) await OpenVariant(previous);
                throw;
            }
            qualityPreference=target.Quality;
            resumeApplied=true;
            seekPending=true;
            QualityInfo=$"{provider.DisplayName} · выбран поток: {target.Quality}";
            Status=$"Качество: {target.Quality}";
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            SelectedStreamQuality=previous?.Quality;
        }
        finally { qualityCancellation?.Dispose(); qualityCancellation=null; Loading=false; transitionGate.Release(); }
    }
}
