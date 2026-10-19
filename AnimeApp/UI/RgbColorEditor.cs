using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Data;

namespace AnimeApp.UI;
public sealed class RgbColorEditor : UserControl
{
    public static readonly StyledProperty<string> ValueProperty = AvaloniaProperty.Register<RgbColorEditor, string>(nameof(Value), "#FFFFFF", defaultBindingMode: BindingMode.TwoWay);
    public string Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    private readonly Slider[] channels = new Slider[3];
    private readonly TextBlock[] numbers = new TextBlock[3];
    private readonly Border preview = new() { Height = 14, CornerRadius = new(7), Margin = new(0, 0, 0, 6) };
    private bool updating;
    public RgbColorEditor()
    {
        var stack = new StackPanel { Spacing = 2, Margin = new(0, 0, 12, 0) }; stack.Children.Add(preview);
        for (int i = 0; i < 3; i++)
        {
            var row = new Grid { ColumnDefinitions = new("24,*,40") };
            row.Children.Add(new TextBlock { Text = new[] { "R", "G", "B" }[i], VerticalAlignment = VerticalAlignment.Center });
            var slider = channels[i] = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true, MinHeight = 28, Foreground = new SolidColorBrush(new[] { Color.Parse("#E88091"), Color.Parse("#81C89A"), Color.Parse("#82ABEB") }[i]) };
            Grid.SetColumn(slider, 1); row.Children.Add(slider);
            numbers[i] = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(numbers[i], 2); row.Children.Add(numbers[i]); stack.Children.Add(row);
            slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !updating) SetCurrentValue(ValueProperty, $"#{(int)channels[0].Value:X2}{(int)channels[1].Value:X2}{(int)channels[2].Value:X2}"); };
        }
        Content = stack; Refresh();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == ValueProperty && channels[2] is not null) Refresh(); }
    private void Refresh()
    {
        if (!Color.TryParse(Value, out var color)) return;
        updating = true;
        var values = new[] { color.R, color.G, color.B };
        for (var i = 0; i < 3; i++) { channels[i].Value = values[i]; numbers[i].Text = values[i].ToString(); }
        preview.Background = new SolidColorBrush(color); updating = false;
    }
}
