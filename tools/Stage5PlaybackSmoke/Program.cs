using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Playback;
using AnimeApp.Services.Shikimori;
using AnimeApp.Providers.AnimeVost;
using AnimeApp.UI;
using AnimeApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
class Entry{[STAThread]public static void Main(string[] args)=>AppBuilder.Configure<Smoke>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);}
class NoTokens:ITokenStore{public Task<OAuthToken?> Read(CancellationToken ct=default)=>Task.FromResult<OAuthToken?>(null);public Task Write(OAuthToken t,CancellationToken ct=default)=>Task.CompletedTask;public Task Clear(CancellationToken ct=default)=>Task.CompletedTask;}
class Smoke:Application{
 public override void Initialize()=>Styles.Add(new FluentTheme());
 public override void OnFrameworkInitializationCompleted(){
 var desktop=(IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;
 Avalonia.Threading.Dispatcher.UIThread.Post(async()=>{
 var report=new List<string>();int exit=0;
 try{
 using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(25)};var provider=new AnimeVostProvider(http,new AnimeVostParser());
 const string release="https://animevost.org/tip/tv/3044-sousou-no-frieren.html";
 var episodes=(await provider.GetEpisodes(release)).Take(2).ToArray();var source=await provider.GetPlayback(release,episodes[0]);
 var db=new DatabaseService(Path.Combine(Path.GetTempPath(),"AnimeAppTests",Guid.NewGuid().ToString(),"db"));await db.Initialize();var repo=new LibraryRepository(db);
 await repo.SaveSetting("playback",new AppSettings(AutoMark:false,AutoSync:false));
 for(int cycle=0;cycle<2;cycle++){
 var engine=new MpvPlayerEngine("anime");
 var vm=new PlayerViewModel(new(0,0,"Native mpv lifecycle","animevost",release,episodes[0].Id,1,source,CountsTowardProgress:false),episodes,provider,engine,repo,new PlaybackService(repo,new EmbeddedPlaybackLauncher()),new SyncService(repo,new ShikimoriService(http,new(),new NoTokens())),NullLogger.Instance){Volume=0};
 var window=new PlayerWindow(vm);window.Show();
 try{
 var deadline=DateTimeOffset.UtcNow.AddSeconds(35);while(engine.Snapshot.Position<3&&DateTimeOffset.UtcNow<deadline)await Task.Delay(250);
 if(engine.Snapshot.Position<3||engine.Snapshot.Error is not null)throw new Exception("Video did not start");
 window.ToggleFullscreen();await Task.Delay(3500);
 // Make inactivity deterministic while the user may move the mouse elsewhere.
 window.Fullscreen.Interact(DateTimeOffset.UtcNow.AddSeconds(-10));await Task.Delay(300);
 if(!window.Fullscreen.Active||window.FindControl<Grid>("Header")!.IsVisible||window.FindControl<StackPanel>("Controls")!.IsVisible)throw new Exception($"Fullscreen/HUD failed: active={window.Fullscreen.Active}, header={window.FindControl<Grid>("Header")!.IsVisible}, controls={window.FindControl<StackPanel>("Controls")!.IsVisible}");
 await Task.Delay(12000);report.Add($"cycle={cycle}; fullscreen={window.WindowState}; {engine.VideoInfo}; dropped={engine.Property("frame-drop-count")}; decoder-dropped={engine.Property("decoder-frame-drop-count")}");
 var warmDrops=int.Parse(engine.Property("frame-drop-count"));await Task.Delay(10000);report.Add($"cycle={cycle}; warm 10s dropped delta={int.Parse(engine.Property("frame-drop-count"))-warmDrops}");
 report.Add($"before toggle: playing={engine.Snapshot.Playing}; pause={engine.Property("pause")}; idle={engine.Property("idle-active")}; position={engine.Snapshot.Position}");
 engine.Play();await Task.Delay(400);
 vm.Speed=1.5;vm.ScalingEnabled=false;vm.ScalingEnabled=true;var videoHost=(LibVLCSharp.Avalonia.VideoView)window.FindControl<ContentControl>("Surface")!.Content!;
 var inputRoot=(Avalonia.Input.InputElement)TopLevel.GetTopLevel((Control)videoHost.Content!)!;
 inputRoot.RaiseEvent(new Avalonia.Input.KeyEventArgs{RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent,Key=Avalonia.Input.Key.Space});
 await Task.Delay(400);report.Add($"after toggle: playing={engine.Snapshot.Playing}; pause={engine.Property("pause")}; idle={engine.Property("idle-active")}");if(engine.Snapshot.Playing)throw new Exception("Pause failed");
 var beforeArrow=engine.Snapshot.Position;
 inputRoot.RaiseEvent(new Avalonia.Input.KeyEventArgs{RoutedEvent=Avalonia.Input.InputElement.KeyDownEvent,Key=Avalonia.Input.Key.Right});await Task.Delay(700);
 if(Math.Abs(engine.Snapshot.Position-beforeArrow-10)>2)throw new Exception("Overlay root arrow failed");
 report.Add("Floating overlay root Space/Right PASS");
 vm.SeekPosition=60;vm.SeekCommand.Execute(null);await Task.Delay(700);if(Math.Abs(engine.Snapshot.Position-60)>2)throw new Exception("Seek failed");
 window.ToggleFullscreen();if(window.Fullscreen.Active||!window.FindControl<Grid>("Header")!.IsVisible)throw new Exception("Window restore failed");
 await vm.NextCommand.ExecuteAsync(null);await Task.Delay(3000);if(vm.Request.Episode!=2||engine.Snapshot.Error is not null)throw new Exception("Next episode failed");
 report.Add($"cycle={cycle}; pause/seek/next/window restore PASS");
 }finally{await window.Shutdown();}
 }
 if((await repo.Pending(0)).Count!=0)throw new Exception("Unmatched playback created progress");
 report.Add("Two open/close cycles; no remote writes; no automatic marking; PASS");
 }catch(Exception ex){exit=1;report.Add("FAIL "+ex.GetType().Name+": "+ex.Message);}
 Directory.CreateDirectory("artifacts");await File.WriteAllLinesAsync("artifacts/stage5-player-lifecycle.txt",report);desktop.Shutdown(exit);
 });base.OnFrameworkInitializationCompleted();}
}
