using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace GradleDoctor;

public class CleanupRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public CleanItem Item { get; }
    public string Kind => Item.Kind;
    public string KindLabel => Item.Kind == "dist" ? "DISTRIBUTION" : "CACHE";
    public string Version => Item.Version;
    public string Path => Item.Path;
    public string SizeText => Format.Size(Item.Size);

    bool _selected = true;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public CleanupRow(CleanItem item) => Item = item;
}

public partial class CleanupDialog : Window
{
    readonly ObservableCollection<CleanupRow> _rows;

    public List<CleanItem> Selected => _rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
    public bool UseRecycleBin => RecycleCheck.IsChecked == true;

    public CleanupDialog(List<CleanItem> items, bool useRecycleBin = true)
    {
        InitializeComponent();
        _rows = new ObservableCollection<CleanupRow>(items.Select(i => new CleanupRow(i)));
        ItemsGrid.ItemsSource = _rows;
        foreach (var row in _rows) row.PropertyChanged += (_, _) => UpdateSummary();
        RecycleCheck.IsChecked = useRecycleBin;
        UpdateSummary();
        UpdateHint();
    }

    void UpdateSummary()
    {
        var selected = _rows.Where(r => r.IsSelected).ToList();
        SelectedCount.Text = $"{selected.Count} / {_rows.Count}";
        SelectedSize.Text = Format.Size(selected.Sum(r => r.Item.Size));
        TotalCount.Text = $"{_rows.Count} item{(_rows.Count == 1 ? "" : "s")}";
        DeleteBtn.IsEnabled = selected.Count > 0;
    }

    void UpdateHint()
    {
        if (HintText is null) return;
        HintText.Text = UseRecycleBin
            ? "Send to Recycle Bin (safe, but slower for large caches)."
            : "Permanent delete: deletes immediately without Recycle Bin (much faster).";
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = true;
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
    }

    void Recycle_Changed(object sender, RoutedEventArgs e) => UpdateHint();

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected;
        if (selected.Count == 0)
        {
            DialogService.Warning("Select at least one item to delete.", "Nothing selected");
            return;
        }

        var size = Format.Size(selected.Sum(i => i.Size));
        var mode = UseRecycleBin ? "move them to the Recycle Bin" : "permanently delete them";
        if (!DialogService.ConfirmDanger(
                $"Delete {selected.Count} item(s) and {mode}?\n\nSpace to reclaim: {size}",
                "Confirm cleanup", "Delete", "Cancel"))
            return;

        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
