using System.Windows;

namespace GradleDoctor;

public static class DialogService
{
    static Window? OwnerWindow()
    {
        var app = Application.Current;
        if (app == null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
               ?? app.MainWindow;
    }

    static bool? Show(string title, string message, DialogKind kind, DialogButtons buttons,
        string okText, string cancelText)
    {
        var dlg = new AppDialog(title, message, kind, buttons, okText, cancelText);
        var owner = OwnerWindow();
        if (owner != null && owner.IsVisible && !ReferenceEquals(owner, dlg)) dlg.Owner = owner;
        return dlg.ShowDialog();
    }

    public static void Info(string message, string title = "Gradle Doctor") =>
        Show(title, message, DialogKind.Info, DialogButtons.Ok, "OK", "Cancel");

    public static void Success(string message, string title = "Gradle Doctor") =>
        Show(title, message, DialogKind.Success, DialogButtons.Ok, "OK", "Cancel");

    public static void Warning(string message, string title = "Gradle Doctor") =>
        Show(title, message, DialogKind.Warning, DialogButtons.Ok, "OK", "Cancel");

    public static void Error(string message, string title = "Gradle Doctor") =>
        Show(title, message, DialogKind.Error, DialogButtons.Ok, "OK", "Cancel");

    public static void Error(Exception ex, string title = "Gradle Doctor") =>
        Show(title, ex.Message, DialogKind.Error, DialogButtons.Ok, "OK", "Cancel");

    public static bool Confirm(string message, string title = "Gradle Doctor",
        string yes = "Yes", string no = "Cancel") =>
        Show(title, message, DialogKind.Question, DialogButtons.YesNo, yes, no) == true;

    public static bool ConfirmDanger(string message, string title = "Gradle Doctor",
        string yes = "Delete", string no = "Cancel") =>
        Show(title, message, DialogKind.Danger, DialogButtons.YesNo, yes, no) == true;

    public static bool ShowApply(IReadOnlyList<ApplyPlanItem> items, bool verify, string verifyTask,
        int warnings, int agpCount, string? extraNote = null)
    {
        var dlg = new ApplyDialog(items, verify, verifyTask, warnings, agpCount, extraNote);
        var owner = OwnerWindow();
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }
}
