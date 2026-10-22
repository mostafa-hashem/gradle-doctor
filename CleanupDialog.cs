using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GradleDoctor;

/// Lets the user tick exactly which old Gradle items should be deleted with modern UI and badge indicators.
public class CleanupDialog : Window
{
    readonly List<(CleanItem Item, CheckBox Box)> _rows = new();
    readonly TextBlock _summary = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
        Margin = new Thickness(0, 0, 16, 0)
    };

    public List<CleanItem> Selected =>
        _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Item).ToList();

    public CleanupDialog(List<CleanItem> items)
    {
        Title = "Clean Gradle Cache & Distributions";
        Width = 880;
        Height = 560;
        MinHeight = 400;
        MinWidth = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)); // Slate-50

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Items Card
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

        // Header
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0)
        };
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 6 19 C 6 20.1 6.9 21 8 21 L 16 21 C 17.1 21 18 20.1 18 19 L 18 7 L 6 7 Z M 19 4 L 15.5 4 L 14.5 3 L 9.5 3 L 8.5 4 L 5 4 L 5 6 L 19 6 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = iconPath;
        Grid.SetColumn(iconBorder, 0);
        header.Children.Add(iconBorder);

        var headerTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerTextStack.Children.Add(new TextBlock
        {
            Text = "Select Unused Gradle Versions & Caches to Delete",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        });
        headerTextStack.Children.Add(new TextBlock
        {
            Text = "Items unreferenced by your scanned projects. Deleting them recovers disk space safely.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(headerTextStack, 1);
        header.Children.Add(headerTextStack);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Center card with scroll viewer
        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10)
        };
        Grid.SetRow(card, 1);

        var panel = new StackPanel();
        foreach (var item in items)
        {
            var itemRow = new Grid { Margin = new Thickness(4, 4, 4, 4) };
            itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Checkbox
            itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Path & Version
            itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Size badge

            var cb = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            cb.Checked += (_, _) => UpdateSummary();
            cb.Unchecked += (_, _) => UpdateSummary();
            _rows.Add((item, cb));
            Grid.SetColumn(cb, 0);
            itemRow.Children.Add(cb);

            var infoStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var kindBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Background = item.Kind == "dist" ? new SolidColorBrush(Color.FromRgb(224, 242, 254)) : new SolidColorBrush(Color.FromRgb(254, 243, 199))
            };
            kindBadge.Child = new TextBlock
            {
                Text = item.Kind.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = item.Kind == "dist" ? new SolidColorBrush(Color.FromRgb(3, 105, 161)) : new SolidColorBrush(Color.FromRgb(180, 83, 9))
            };
            infoStack.Children.Add(kindBadge);

            infoStack.Children.Add(new TextBlock
            {
                Text = item.Version,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            infoStack.Children.Add(new TextBlock
            {
                Text = item.Path,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetColumn(infoStack, 1);
            itemRow.Children.Add(infoStack);

            var sizeBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                VerticalAlignment = VerticalAlignment.Center
            };
            sizeBadge.Child = new TextBlock
            {
                Text = Format.Size(item.Size),
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };
            Grid.SetColumn(sizeBadge, 2);
            itemRow.Children.Add(sizeBadge);

            panel.Children.Add(itemRow);
        }

        card.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(card);

        // Footer bar
        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(bar, 2);

        var summaryBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        summaryBorder.Child = _summary;
        Grid.SetColumn(summaryBorder, 0);
        bar.Children.Add(summaryBorder);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        Button Mk(string text, RoutedEventHandler h, Style? style = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(6, 0, 0, 0) };
            if (style != null) b.Style = style;
            b.Click += h;
            return b;
        }

        btnStack.Children.Add(Mk("Select all", (_, _) => SetAll(true)));
        btnStack.Children.Add(Mk("Select none", (_, _) => SetAll(false)));

        var dangerStyle = TryFindResource("DangerButtonStyle") as Style;
        var del = Mk("Delete selected", (_, _) => { DialogResult = true; }, dangerStyle);
        del.IsDefault = true;
        btnStack.Children.Add(del);

        var cancel = Mk("Cancel", (_, _) => { DialogResult = false; });
        cancel.IsCancel = true;
        btnStack.Children.Add(cancel);

        Grid.SetColumn(btnStack, 1);
        bar.Children.Add(btnStack);

        root.Children.Add(bar);
        Content = root;
        UpdateSummary();
    }

    void SetAll(bool value)
    {
        foreach (var r in _rows) r.Box.IsChecked = value;
    }

    void UpdateSummary()
    {
        var sel = Selected;
        _summary.Text = $"{sel.Count} of {_rows.Count} selected  •  {Format.Size(sel.Sum(i => i.Size))} to reclaim";
    }
}
