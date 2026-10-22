using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GradleDoctor;

public enum DialogKind { Info, Warning, Error, Success, Question, Danger }

public enum DialogButtons { Ok, OkCancel, YesNo }

public partial class AppDialog : Window
{
    public bool? Result { get; private set; }

    public AppDialog(string title, string message, DialogKind kind = DialogKind.Info,
        DialogButtons buttons = DialogButtons.Ok, string okText = "OK", string cancelText = "Cancel")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;

        SecondaryBtn.Visibility = buttons == DialogButtons.Ok ? Visibility.Collapsed : Visibility.Visible;
        if (buttons == DialogButtons.YesNo)
        {
            okText = okText == "OK" ? "Yes" : okText;
            cancelText = cancelText == "Cancel" ? "No" : cancelText;
        }
        PrimaryBtn.Content = okText;
        SecondaryBtn.Content = cancelText;
        PrimaryBtn.IsDefault = true;
        SecondaryBtn.IsCancel = true;

        if (kind == DialogKind.Danger)
            PrimaryBtn.Style = (Style)FindResource("DangerButtonStyle");

        ConfigureIcon(kind);
        Loaded += (_, _) => PrimaryBtn.Focus();
    }

    void ConfigureIcon(DialogKind kind)
    {
        Brush accent;
        (string fill, string stroke) data;

        switch (kind)
        {
            case DialogKind.Success:
                IconBg.Background = (Brush)FindResource("SuccessLightBrush");
                accent = (Brush)FindResource("SuccessBrush");
                data = ("", "M 4 12 L 9 17 L 20 6");
                break;
            case DialogKind.Warning:
                IconBg.Background = (Brush)FindResource("WarningLightBrush");
                accent = (Brush)FindResource("WarningBrush");
                data = ("M 12 3 L 22 21 L 2 21 Z", "");
                break;
            case DialogKind.Error:
                IconBg.Background = (Brush)FindResource("DangerLightBrush");
                accent = (Brush)FindResource("DangerBrush");
                data = ("", "M 6 6 L 18 18 M 18 6 L 6 18");
                break;
            case DialogKind.Danger:
                IconBg.Background = (Brush)FindResource("DangerLightBrush");
                accent = (Brush)FindResource("DangerBrush");
                data = ("M 6 19 C 6 20.1 6.9 21 8 21 L 16 21 C 17.1 21 18 20.1 18 19 L 18 7 L 6 7 Z M 19 4 L 15.5 4 L 14.5 3 L 9.5 3 L 8.5 4 L 5 4 L 5 6 L 19 6 Z", "");
                break;
            case DialogKind.Question:
                IconBg.Background = (Brush)FindResource("InfoLightBrush");
                accent = (Brush)FindResource("InfoBrush");
                data = ("", "M 9 9 A 3 3 0 1 1 15 9 C 15 12 12 12 12 15 M 12 18 L 12 19");
                break;
            default:
                IconBg.Background = (Brush)FindResource("PrimaryLightBrush");
                accent = (Brush)FindResource("PrimaryBrush");
                data = ("M 11 6 L 13 6 L 13 8 L 11 8 Z M 11 10 L 13 10 L 13 18 L 11 18 Z", "");
                break;
        }

        if (data.fill.Length > 0)
        {
            IconPath.Data = Geometry.Parse(data.fill);
            IconPath.Fill = accent;
            IconPath.Stroke = Brushes.Transparent;
        }
        else
        {
            IconPath.Data = Geometry.Parse(data.stroke);
            IconPath.Fill = Brushes.Transparent;
            IconPath.Stroke = accent;
            IconPath.StrokeThickness = 2;
            IconPath.StrokeStartLineCap = PenLineCap.Round;
            IconPath.StrokeEndLineCap = PenLineCap.Round;
            IconPath.StrokeLineJoin = PenLineJoin.Round;
        }
    }

    void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
    }

    void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
    }

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
