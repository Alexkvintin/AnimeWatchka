using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.UI;
using AnimeApp.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using System.Text.Json;
using Xunit;

namespace AnimeApp.Tests;
public class ThemeAndRecoveryTests
{
    [Fact]
    public async Task LegacyUnidentifiedProgressIsPreservedAndCanBeRestoredOnce()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize(); var repo = new LibraryRepository(db);
        await repo.Cache(7, [new Anime { Name = "Lost title", ShikimoriId = 0, EpisodesWatched = 3 }]);
        var old = new PendingChange(7, 0, "legacy", 3, "watching", 0, "planned");
        await db.Execute(c => { DatabaseService.Run(c, "INSERT INTO pending VALUES(7,0,'legacy',$j)", ("$j", JsonSerializer.Serialize(old))); return true; });
        var entry = Assert.Single(await repo.Unlinked(7));
        Assert.Empty(await repo.Pending(7)); Assert.Equal("Lost title", entry.Title);
        Assert.Single(await repo.Unlinked(7));
        await repo.ReplaceRemote(7, []); Assert.Single(await repo.Library(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RestoreUnlinked(8, entry, new Anime { ShikimoriId = 5 }));
        await repo.RestoreUnlinked(7, entry, new Anime { ShikimoriId = 5, Name = "Correct title", EpisodesTotal = 12 });
        Assert.Empty(await repo.Unlinked(7));
        Assert.Equal(3, Assert.Single(await repo.Pending(7)).Episodes);
        await repo.RestoreUnlinked(7, entry, new Anime { ShikimoriId = 5 });
        Assert.Single(await repo.Pending(7));
    }
    [AvaloniaFact]
    public void RgbAndTransparencyUpdateImmediatelyInEveryTheme()
    {
        var appearance = AppearanceViewModel.Current; appearance.Persist = false;
        try
        {
            foreach (var theme in new[] { "light", "dark", "lumi" })
            {
                appearance.SelectThemeCommand.Execute(theme); appearance.PanelOpacity = .5;
                Assert.InRange(((ISolidColorBrush)Application.Current!.Resources["PanelBrush"]!).Color.A, 126, 128);
                appearance.Accent = "#127ABC";
                Assert.Equal(Color.Parse("#127ABC"), ((ISolidColorBrush)Application.Current.Resources["AccentBrush"]!).Color);
            }
            var editor = new RgbColorEditor { Value = "#127ABC" };
            var window = new Window { Content = editor }; window.Show(); window.UpdateLayout();
            var sliders = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(editor).OfType<Slider>().ToArray();
            Assert.Equal(3, sliders.Length); sliders[0].Value = 200;
            Assert.Equal("#C87ABC", editor.Value);
            window.Close();
        }
        finally { appearance.SelectThemeCommand.Execute("dark"); appearance.Persist = true; }
    }
    [AvaloniaFact]
    public void ListRowsStayTransparentOnHoverAndSelection()
    {
        var list = new ListBox { ItemsSource = new[] { "Row" } };
        var window = new Window { Content = list, Width = 600, Height = 300 }; window.Show(); window.UpdateLayout();
        window.MouseMove(new Point(100, 16)); Dispatcher.UIThread.RunJobs();
        list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
        var presenter = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(list).OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter" && p.Content as string == "Row");
        Assert.Equal(0, ((ISolidColorBrush)presenter.Background!).Color.A);
        window.Close();
    }
}
