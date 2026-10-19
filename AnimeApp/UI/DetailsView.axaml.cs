using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace AnimeApp.UI;
public sealed partial class DetailsView : UserControl
{
    public DetailsView() => AvaloniaXamlLoader.Load(this);
}
