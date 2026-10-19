using AnimeApp.UI;
using AnimeApp.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;
namespace AnimeApp.Tests;
public class LumiArtworkTests
{
    [AvaloniaFact]
    public async Task EverySectionUsesSuppliedPoseAndLeavesNavigationScrollable()
    {
        var (vm,_)=await Stage4UiTests.Create();
        vm.Settings.WizardVisible=false;
        var appearance=AppearanceViewModel.Current; appearance.Persist=false;
        var window=new MainWindow {DataContext=vm,Width=1000,Height=600};
        try
        {
            appearance.SelectThemeCommand.Execute("lumi"); window.Show();
            var images=new HashSet<Bitmap>();
            foreach(var page in new[]{"Главная","Поиск","Онгоинги","Смотрю","Запланировано","История","Настройки"})
            {
                vm.Page=page;
                Dispatcher.UIThread.RunJobs();window.UpdateLayout();
                var image=window.FindControl<Image>("LumiMascot")!;
                Assert.Same(vm.LumiImage,image.Source);
                images.Add(vm.LumiImage);
                Assert.True(window.FindControl<ScrollViewer>("SidebarNavigation")!.Bounds.Height>100);
            }
            Assert.Equal(7,images.Count);
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"screenshots"));
            using var bitmap=new RenderTargetBitmap(new PixelSize(1000,600));
            bitmap.Render(window);bitmap.Save(Path.Combine(AppContext.BaseDirectory,"screenshots","lumi-settings-small.png"));
        }
        finally {window.Close();appearance.SelectThemeCommand.Execute("dark");appearance.Persist=true;}
    }
}
