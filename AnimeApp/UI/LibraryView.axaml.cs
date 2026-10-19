using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AnimeApp.UI;
public sealed partial class LibraryView : UserControl
{
    public LibraryView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, e) =>
        {
            if (DataContext is AnimeApp.ViewModels.MainViewModel vm)
                vm.SetColumns((int)(e.NewSize.Width / 228));
        };
    }
}
