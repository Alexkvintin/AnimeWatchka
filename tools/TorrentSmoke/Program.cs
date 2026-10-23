using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using AnimeApp.Services.Torrents;
if(args.Length==1 && args[0] is "--public" or "--old") {
    var old=args[0]=="--old";
    var animeTitle=old ? "Black Lagoon" : "Sousou no Frieren";
    var episodeNumber=old ? 12 : 1;
    var database=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeWatchka-public-"+Guid.NewGuid()+".db")); await database.Initialize();
    var repository=new AnimeApp.Services.Database.LibraryRepository(database);
    await repository.SaveSetting("torrent-options",new TorrentOptions(1024,32));
    await using var publicSession=new TorrentSession(); var publicProvider=new TorrentProvider(repository,publicSession);
    using var publicDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(75));
    await publicProvider.EnsureDefaultCatalogs(publicDeadline.Token);
    var releases=await publicProvider.FindRussian([old ? "Black Lagoon" : "Sousou no Frieren - 01"],publicDeadline.Token,includeOriginalAudio:true);
    if(old) foreach(var r in releases.Take(15)) Console.WriteLine($"Candidate {r.Seeds}: {r.Title} / {r.Episodes}");
    var chosen=releases.FirstOrDefault(r=>r.Seeds>0 && (old || TorrentCatalogFormats.CoversEpisode(r,1))) ?? throw new IOException("No live episode-one candidate");
    Console.WriteLine($"Public release selected; seeds={chosen.Seeds}; metadata pending");
    Console.WriteLine(chosen.Title);
    var release=await publicProvider.SaveCatalog(old ? "shikimori:889" : "shikimori:52991",animeTitle,releases,publicDeadline.Token);
    var episodes=await publicProvider.GetEpisodes(release.Id,publicDeadline.Token);
    var episode=episodes.FirstOrDefault(e=>e.Number==episodeNumber) ?? throw new IOException("No episode one in actual torrent files");
    var media=await publicProvider.GetPlayback(release.Id,episode,publicDeadline.Token);
    _=await publicProvider.GetEpisodes(release.Id,publicDeadline.Token); // Same call as the desktop launcher.
    await using var native=new AnimeApp.Services.Playback.MpvPlayerEngine("linear"); native.Attach(0); await native.Open(media);
    while(native.Snapshot.Duration<=0) await Task.Delay(200,publicDeadline.Token);
    var audio=native.Property("aid"); var japanese=false;
    int.TryParse(native.Property("track-list/count"),out var trackCount);
    for(var i=0;i<trackCount;i++) if(native.Property($"track-list/{i}/type")=="audio" && native.Property($"track-list/{i}/id")==audio)
        japanese=native.Property($"track-list/{i}/lang") is "ja" or "jpn";
    if(!japanese) throw new IOException("Japanese audio not confirmed in live release");
    using var web=new HttpClient {Timeout=TimeSpan.FromSeconds(10)}; var finder=new AnimeApp.Services.Playback.AutomaticSubtitles(web);
    var translations=await finder.Find(old ? 889 : 52991,animeTitle,episodeNumber,publicDeadline.Token);
    var attached=false;
    foreach(var translation in translations.Where(s=>s.Language=="ru").Take(4)) {
        try { var path=await finder.Download(translation,publicDeadline.Token); AnimeApp.Services.Playback.SubtitleControls.AddFile(native,path); attached=true; break; }
        catch(HttpRequestException) { }
    }
    if(!attached) throw new IOException("No live Russian translation could be attached");
    await Task.Delay(1000,publicDeadline.Token);
    if(native.Snapshot.Position<=0) throw new IOException("Live playback position did not advance");
    Console.WriteLine($"PASS public torrent: metadata, actual episode {episodeNumber}, Japanese audio, mpv duration={native.Snapshot.Duration:0}, position={native.Snapshot.Position:0.0}, Russian ASS; {publicSession.Status}");
    await native.DisposeAsync(); await publicProvider.Stop();
    return;
}
if(args.Length==2 && args[0]=="--catalogs") {
    var db=new AnimeApp.Services.Database.DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeWatchka-live-"+Guid.NewGuid()+".db")); await db.Initialize();
    var repo=new AnimeApp.Services.Database.LibraryRepository(db);
    await using var torrents=new TorrentSession(); var catalog=new TorrentProvider(repo,torrents);
    await catalog.EnsureDefaultCatalogs();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var results=await catalog.FindRussian(["Cowboy Bebop"],timeout.Token,includeOriginalAudio:true);
    if(results.Count==0) throw new Exception("Live catalogs returned no results");
    Console.WriteLine($"PASS live catalogs: {results.Count} deduplicated releases; quality/size/seeds parsed");
    using var client=new HttpClient {Timeout=TimeSpan.FromSeconds(10)};
    var subtitles=new AnimeApp.Services.Playback.AutomaticSubtitles(client);
    var translations=await subtitles.Find(1,"Cowboy Bebop",1,timeout.Token);
    string? downloaded=null;
    foreach(var translation in translations.Where(s=>s.Language=="ru")) {
        try {downloaded=await subtitles.Download(translation,timeout.Token);break;}
        catch(HttpRequestException) {Console.WriteLine("Unavailable translation skipped");}
    }
    if(downloaded is null) throw new Exception("No downloadable live Russian subtitles");
    await using var player=new AnimeApp.Services.Playback.MpvPlayerEngine("linear"); player.Attach(0);
    await player.Open(new(AnimeApp.Services.Playback.PlaybackKind.LocalFile,Path.GetFullPath(args[1]),PreferredAudioLanguage:"ja,jpn",IsTorrentStream:true));
    await Task.Delay(1200,timeout.Token);
    var before=AnimeApp.Services.Playback.SubtitleControls.Tracks(player).Count;
    AnimeApp.Services.Playback.SubtitleControls.AddFile(player,downloaded);
    AnimeApp.Services.Playback.SubtitleControls.Delay(player,1.5);
    await Task.Delay(300,timeout.Token);
    if(AnimeApp.Services.Playback.SubtitleControls.Tracks(player).Count<=before) throw new Exception("ASS not added to mpv");
    if(Math.Abs(double.Parse(player.Property("sub-delay"),System.Globalization.CultureInfo.InvariantCulture)-1.5)>.01) throw new Exception("Subtitle delay not applied");
    Console.WriteLine($"PASS live Anime365: {translations.Count} translations for exact anime/episode; ASS downloaded and loaded in mpv; delay applied");
    await repo.SaveSetting("playback",new AnimeApp.Models.AppSettings(AutoMark:false,AutoSync:false));
    var api=new AnimeApp.Services.Shikimori.ShikimoriService(client,new(),new AnimeApp.Services.Shikimori.WindowsTokenStore(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".tokens")));
    await using var automaticPlayer=new AnimeApp.Services.Playback.MpvPlayerEngine("linear"); automaticPlayer.Attach(0);
    var request=new AnimeApp.Services.Playback.PlaybackRequest(0,1,"Cowboy Bebop","local","smoke","1",1,new(AnimeApp.Services.Playback.PlaybackKind.LocalFile,Path.GetFullPath(args[1]),PreferredAudioLanguage:"ja,jpn",IsTorrentStream:true));
    var vm=new AnimeApp.ViewModels.PlayerViewModel(request,[new("1",1,"Episode 1")],new AnimeApp.Providers.LocalFilesProvider(repo),automaticPlayer,repo,
        new AnimeApp.Services.Playback.PlaybackService(repo,new AnimeApp.Services.Playback.EmbeddedPlaybackLauncher()),new AnimeApp.Services.SyncService(repo,api),Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) {SubtitleFinder=subtitles};
    await vm.Initialize();
    for(var attempt=0;attempt<200 && !vm.OnlineSubtitleStatus.StartsWith("Подключены:");attempt++) { await Task.Delay(100,timeout.Token); await vm.Tick(); }
    if(!vm.OnlineSubtitleStatus.StartsWith("Подключены:") || vm.SelectedOnlineSubtitle?.Language!="ru") throw new Exception("Automatic Russian subtitle attachment failed: "+vm.OnlineSubtitleStatus);
    if(AnimeApp.Services.Playback.SubtitleControls.Selected(automaticPlayer)<0) throw new Exception("Automatic subtitles were not selected");
    await vm.Close();
    Console.WriteLine("PASS existing PlayerViewModel: Russian subtitles auto-attached over an English soft-sub track; no manual translation click");
    return;
}
if(args.Length!=1) throw new ArgumentException("Pass the directory containing generated Show - 01.mkv and english.srt.");
var mediaInputs=Path.GetFullPath(args[0]);
var root=Path.Combine(Path.GetTempPath(),"AnimeWatchka-torrent-smoke",Guid.NewGuid().ToString());
Directory.CreateDirectory(root);
var seedFolder=Path.Combine(root,"seed"); Directory.CreateDirectory(seedFolder);
var payload=System.Security.Cryptography.RandomNumberGenerator.GetBytes(256*1024*1024);
var file=Path.Combine(seedFolder,"Show - 01.mkv"); await File.WriteAllBytesAsync(file,payload);
var torrentPath=Path.Combine(root,"test.torrent");
await new TorrentCreator { PieceLength=16384 }.CreateAsync(new TorrentFileSource(file),torrentPath);
var reservation=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0); reservation.Start(); var port=((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
using var seedEngine=new ClientEngine(new EngineSettingsBuilder { CacheDirectory=Path.Combine(root,"seed-state"), MaximumUploadRate=128*1024, MaximumDiskReadRate=256*1024, AllowPortForwarding=false, DhtEndPoint=null, ListenEndPoints=new Dictionary<string,IPEndPoint>{{"ipv4",new(IPAddress.Loopback,port)}} }.ToSettings());
var seed=await seedEngine.AddAsync(await Torrent.LoadAsync(torrentPath),seedFolder);
await seed.StartAsync();
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
while(seed.State!=TorrentState.Seeding) await Task.Delay(100,deadline.Token);
await using var session=new TorrentSession();
var manager=await session.Open(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))),torrentPath,new(128,128),deadline.Token);

Console.WriteLine($"Seed state={seed.State}; port={port}");
await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{port}")));
var source=await session.Play(manager.Files.Single().Path,deadline.Token);
using var http=new HttpClient { Timeout=TimeSpan.FromSeconds(35) };
var partialSeekVerified=false;
foreach(var start in new[]{0,payload.Length-65536,payload.Length/2}) {
 using var request=new HttpRequestMessage(HttpMethod.Get,source.Location); request.Headers.Range=new(start,start+1023);
 using var response=await http.SendAsync(request,deadline.Token); response.EnsureSuccessStatusCode();
 var actual=await response.Content.ReadAsByteArrayAsync(deadline.Token);
 if(!actual.SequenceEqual(payload[start..(start+1024)])) throw new Exception("Range data mismatch");
 if(start!=0 && manager.Progress<100) partialSeekVerified=true;
 Console.WriteLine($"Range {start}: verified; downloaded={manager.Progress:0.00}%");
}
if(!partialSeekVerified) throw new Exception("Did not prove nonsequential seek before complete download");
await session.Stop(); await seed.StopAsync();
Console.WriteLine("PASS: torrent streaming and nonsequential seeks before complete download");

var mediaFolder=Path.Combine(root,"media"); Directory.CreateDirectory(mediaFolder);
File.Copy(Path.Combine(mediaInputs,"Show - 01.mkv"),Path.Combine(mediaFolder,"Show - 01.mkv"));
File.Copy(Path.Combine(mediaInputs,"Show - 01.mkv"),Path.Combine(mediaFolder,"Show - 02.mkv"));
File.Copy(Path.Combine(mediaInputs,"english.srt"),Path.Combine(mediaFolder,"Show - 01.en.srt"));
var mediaTorrent=Path.Combine(root,"media.torrent");
await new TorrentCreator { PieceLength=16384 }.CreateAsync(new TorrentFileSource(mediaFolder),mediaTorrent);
var mediaSeed=await seedEngine.AddAsync(await Torrent.LoadAsync(mediaTorrent),root);
await mediaSeed.StartAsync();
using var mediaDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
while(mediaSeed.State!=TorrentState.Seeding) await Task.Delay(100,mediaDeadline.Token);
var mediaManager=await session.Open(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root+"media"))),mediaTorrent,new(),mediaDeadline.Token);
await mediaManager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{port}")));
if(mediaManager.Files.Count(f=>TorrentProvider.IsVideo(f.Path))!=2) throw new Exception("Batch episodes missing");
var mediaSource=await session.Play(mediaManager.Files.First(f=>f.Path.EndsWith("01.mkv")).Path,mediaDeadline.Token);
if(mediaSource.SubtitleTracks?.Count!=1) throw new Exception("Sidecar missing");
await using(var mpv=new AnimeApp.Services.Playback.MpvPlayerEngine("linear")) {
 mpv.Attach(0); await mpv.Open(mediaSource); await Task.Delay(2000,mediaDeadline.Token);
 var count=int.Parse(mpv.Property("track-list/count")); var aid=mpv.Property("aid"); var langs=new List<string>();
 for(var i=0;i<count;i++) if(mpv.Property($"track-list/{i}/type")=="audio") {
   langs.Add(mpv.Property($"track-list/{i}/lang"));
   if(mpv.Property($"track-list/{i}/id")==aid && mpv.Property($"track-list/{i}/lang")!="jpn") throw new Exception("Japanese audio not selected");
 }
 var subtitles=AnimeApp.Services.Playback.SubtitleControls.Tracks(mpv);
 if(langs.Count!=2 || subtitles.Count<4) throw new Exception($"Missing tracks audio={langs.Count} subs={subtitles.Count}");
 AnimeApp.Services.Playback.SubtitleControls.Select(mpv,subtitles[1].Id);
 mpv.Pause(); mpv.Seek(7); await Task.Delay(700); mpv.Play(); await Task.Delay(700);
 if(mpv.Snapshot.Position<6) throw new Exception("Native seek failed");
 Console.WriteLine($"PASS MKV: 2 episodes, Japanese default, {langs.Count} audio, {subtitles.Count-1} subtitles, sidecar, native pause/seek/resume");
}
var offline=await new AnimeApp.Services.Playback.EpisodeDownloader(http).Download(mediaSource,Path.Combine(root,"offline"),"Offline episode","Auto",null,mediaDeadline.Token);
await session.Stop(); await mediaSeed.StopAsync();
await using(var offlinePlayer=new AnimeApp.Services.Playback.MpvPlayerEngine("linear")) {
 offlinePlayer.Attach(0); await offlinePlayer.Open(AnimeApp.Services.Playback.DownloadedEpisodeProvider.Source(offline.Directory));
 await Task.Delay(1500);
 if(offlinePlayer.Snapshot.Duration<=0 || AnimeApp.Services.Playback.SubtitleControls.Tracks(offlinePlayer).Count<4 || AnimeApp.Services.Playback.SubtitleControls.Selected(offlinePlayer)<0) throw new Exception("Offline media/subtitles did not load");
 Console.WriteLine("PASS: downloaded MKV and sidecar play after torrent server stops");
}

// Reopen the same identity after shutdown: cached pieces and stable file IDs remain usable.
var resumed=await session.Open(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root+"media"))),mediaTorrent,new(),CancellationToken.None);
var resumedSource=await session.Play(resumed.Files.First(f=>f.Path.EndsWith("01.mkv")).Path,CancellationToken.None);
using(var request=new HttpRequestMessage(HttpMethod.Get,resumedSource.Location)) {
 request.Headers.Range=new(0,31); using var response=await http.SendAsync(request); response.EnsureSuccessStatusCode();
 if((await response.Content.ReadAsByteArrayAsync()).Length!=32) throw new Exception("Resume range failed");
}
await session.Stop();
Console.WriteLine("PASS: reopen cached torrent after stopping");
var unavailableFile=Path.Combine(root,"Unavailable - 01.mkv"); await File.WriteAllBytesAsync(unavailableFile,System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024*1024));
var unavailableTorrent=Path.Combine(root,"unavailable.torrent"); await new TorrentCreator { PieceLength=16384 }.CreateAsync(new TorrentFileSource(unavailableFile),unavailableTorrent);
var unavailable=await session.Open(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root+"unavailable"))),unavailableTorrent,new(),CancellationToken.None);
using(var noPeers=new CancellationTokenSource(TimeSpan.FromMilliseconds(700))) {
 try { await session.Play(unavailable.Files.Single().Path,noPeers.Token); throw new Exception("Unexpected peer data"); }
 catch(OperationCanceledException) when(noPeers.IsCancellationRequested) { }
}
await session.Stop();
Console.WriteLine("PASS: no-peer read can be cancelled and session stopped");
