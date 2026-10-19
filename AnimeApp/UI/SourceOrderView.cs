using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AnimeApp.ViewModels;

namespace AnimeApp.UI;

public sealed class SourceOrderView : UserControl
{
    private readonly StackPanel rows = new() { Spacing = 8 };
    private ProviderViewModel? dragging;
    private SettingsViewModel? observed;
    public SourceOrderView()
    {
        Content = rows;
        DataContextChanged += (_, _) =>
        {
            if (observed is not null) observed.Providers.CollectionChanged -= Changed;
            observed = DataContext as SettingsViewModel;
            if (observed is not null) observed.Providers.CollectionChanged += Changed;
            Build();
        };
    }
    private void Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Build();
    private void Build()
    {
        rows.Children.Clear();
        if (DataContext is not SettingsViewModel settings) return;
        foreach (var source in settings.Providers)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), Tag = source, Margin = new(0, 4) };
            var handle = new Border { Background = Brushes.Transparent, Child = new TextBlock { Text = "⠿", FontSize = 24 }, Padding = new(12), Cursor = new Cursor(StandardCursorType.SizeAll) };
            ToolTip.SetTip(handle, "Перетащите источник вверх или вниз");
            handle.PointerPressed += (_, e) => { dragging = source; e.Pointer.Capture(handle); e.Handled = true; };
            handle.PointerReleased += async (_, e) =>
            {
                e.Pointer.Capture(null);
                if (dragging is null) return;
                var point = e.GetPosition(rows);
                var target = rows.Children.OfType<Grid>().FirstOrDefault(r => point.Y >= r.Bounds.Y && point.Y <= r.Bounds.Bottom)?.Tag as ProviderViewModel;
                dragging = null;
                if (target is not null) { await settings.MoveProvider(source, settings.Providers.IndexOf(target)); Build(); }
            };
            row.Children.Add(handle);
            var name = new TextBlock { Text = source.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var toggle = new ToggleSwitch { IsChecked = source.Enabled, VerticalAlignment = VerticalAlignment.Center, OnContent = "", OffContent = "" };
            toggle.Bind(ToggleSwitch.IsCheckedProperty, new Avalonia.Data.Binding(nameof(source.Enabled)) { Source = source, Mode = Avalonia.Data.BindingMode.TwoWay });
            toggle.Click += async (_, _) => { source.Enabled = toggle.IsChecked == true; await settings.PersistProviders(); };
            Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
            // Keyboard-accessible alternatives to dragging.
            foreach (var (label, delta, column) in new[] { ("↑", -1, 3), ("↓", 1, 4) })
            {
                var button = new Button { Content = label, Padding = new(8), Margin = new(3, 0), VerticalAlignment = VerticalAlignment.Center };
                button.Click += async (_, _) => { await settings.MoveProvider(source, settings.Providers.IndexOf(source) + delta); Build(); };
                ToolTip.SetTip(button, delta < 0 ? "Выше" : "Ниже");
                Grid.SetColumn(button, column); row.Children.Add(button);
            }
            rows.Children.Add(row);
        }
    }
}

