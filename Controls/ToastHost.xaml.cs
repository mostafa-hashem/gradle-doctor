using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GradleDoctor;

public enum ToastKind { Info, Success, Warning, Error }

public record ToastItem(Guid Id, string Message, ToastKind Kind);

public static class ToastService
{
    public static event Action<string, ToastKind>? Requested;

    public static void Show(string message, ToastKind kind = ToastKind.Info) =>
        Requested?.Invoke(message, kind);
}

public partial class ToastHost : UserControl
{
    readonly ObservableCollection<ToastItem> _items = new();

    public ToastHost()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        Loaded += (_, _) => ToastService.Requested += OnRequested;
        Unloaded += (_, _) => ToastService.Requested -= OnRequested;
    }

    void OnRequested(string message, ToastKind kind) =>
        Dispatcher.Invoke(() => Show(message, kind));

    public void Show(string message, ToastKind kind = ToastKind.Info, int seconds = 4)
    {
        if (_items.Count >= 4) _items.RemoveAt(0);
        var item = new ToastItem(Guid.NewGuid(), message, kind);
        _items.Add(item);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, seconds)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _items.Remove(item);
        };
        timer.Start();
    }

    void Toast_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToastItem item }) _items.Remove(item);
    }
}
