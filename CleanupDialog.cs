using System.Windows;
using System.Windows.Controls;

namespace GradleDoctor;

/// Lets the user tick exactly which old Gradle items should be deleted.
public class CleanupDialog : Window
{
    readonly List<(CleanItem Item, CheckBox Box)> _rows = new();
    readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

    public List<CleanItem> Selected =>
        _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Item).ToList();

    public CleanupDialog(List<CleanItem> items)
    {
        Title = "Choose what to delete";
        Width = 860;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new StackPanel();
        foreach (var item in items)
        {
            var cb = new CheckBox
            {
                IsChecked = true,
                Content = item.Display,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Margin = new Thickness(2, 3, 2, 3)
            };
            cb.Checked += (_, _) => UpdateSummary();
            cb.Unchecked += (_, _) => UpdateSummary();
            _rows.Add((item, cb));
            panel.Children.Add(cb);
        }
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(bar, 1);

        Button Mk(string text, RoutedEventHandler h)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += h;
            return b;
        }

        bar.Children.Add(_summary);
        bar.Children.Add(Mk("Select all", (_, _) => SetAll(true)));
        bar.Children.Add(Mk("Select none", (_, _) => SetAll(false)));
        var del = Mk("Delete selected", (_, _) => { DialogResult = true; });
        del.IsDefault = true;
        bar.Children.Add(del);
        var cancel = Mk("Cancel", (_, _) => { DialogResult = false; });
        cancel.IsCancel = true;
        bar.Children.Add(cancel);
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
        _summary.Text = $"{sel.Count} of {_rows.Count} selected, {Format.Size(sel.Sum(i => i.Size))}";
    }
}
