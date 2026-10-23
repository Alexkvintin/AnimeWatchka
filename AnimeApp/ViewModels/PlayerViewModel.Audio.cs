using AnimeApp.Services.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
namespace AnimeApp.ViewModels;
public sealed partial class PlayerViewModel
{
    public string TorrentStatus => provider is AnimeApp.Services.Torrents.TorrentProvider torrent ? torrent.Status : "";
    [ObservableProperty] private IReadOnlyList<SubtitleChoice> audioTracks = [];
    [ObservableProperty] private SubtitleChoice? selectedAudioTrack;
    private bool readingAudio;
    private string? preferredAudioLabel;
    private bool restoreAudioSelection;
    partial void OnSelectedAudioTrackChanged(SubtitleChoice? value) {
        if(readingAudio || value is null) return;
        preferredAudioLabel=value.Label;
        var engine=Engine is MpvPlayerEngine { Fallback: {} fallback } ? fallback : Engine;
        if(engine is MpvPlayerEngine mpv) mpv.Command("set","aid",value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if(engine is VlcPlayerEngine vlc) vlc.Player.SetAudioTrack(value.Id);
    }
    private void RefreshAudio() {
        OnPropertyChanged(nameof(TorrentStatus));
        var tracks=new List<SubtitleChoice>(); var selected=-1;
        var engine=Engine is MpvPlayerEngine { Fallback: {} fallback } ? fallback : Engine;
        if(engine is MpvPlayerEngine mpv) {
            int.TryParse(mpv.Property("track-list/count"),out var count);
            for(var i=0;i<Math.Min(count,256);i++) {
                var prefix=$"track-list/{i}/";
                if(mpv.Property(prefix+"type")!="audio" || !int.TryParse(mpv.Property(prefix+"id"),out var id)) continue;
                var label=string.Join(" · ",new[]{mpv.Property(prefix+"lang"),mpv.Property(prefix+"title")}.Where(s=>!string.IsNullOrWhiteSpace(s)));
                tracks.Add(new(id,label.Length>0 ? label : $"Дорожка {id}"));
            }
            int.TryParse(mpv.Property("aid"),out selected);
        } else if(engine is VlcPlayerEngine vlc) { tracks.AddRange(vlc.Player.AudioTrackDescription.Where(t=>t.Id>=0).Select(t=>new SubtitleChoice(t.Id,t.Name))); selected=vlc.Player.AudioTrack; }
        if(restoreAudioSelection && tracks.Count>0) {
            restoreAudioSelection=false;
            var preferred=tracks.FirstOrDefault(t=>t.Label==preferredAudioLabel);
            if(preferred is not null) OnSelectedAudioTrackChanged(preferred);
        }
        readingAudio=true;
        try { if(!AudioTracks.SequenceEqual(tracks)) AudioTracks=tracks; SelectedAudioTrack=AudioTracks.FirstOrDefault(t=>t.Id==selected); }
        finally { readingAudio=false; }
    }
}
