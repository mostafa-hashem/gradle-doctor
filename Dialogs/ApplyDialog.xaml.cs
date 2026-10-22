using System.Windows;
using System.Windows.Input;

namespace GradleDoctor;

public record ApplyPlanItem(ProjectInfo Project, string GradleChange, string AgpChange, string Warning);

public partial class ApplyDialog : Window
{
    public ApplyDialog(IReadOnlyList<ApplyPlanItem> items, bool verify, string verifyTask,
        int warnings, int agpCount, string? extraNote)
    {
        InitializeComponent();

        ChangesGrid.ItemsSource = items;
        ProjectsCount.Text = items.Count.ToString();
        GradleCount.Text = items.Count(i => i.GradleChange != "—").ToString();
        AgpCount.Text = agpCount.ToString();
        WarningsCount.Text = warnings.ToString();
        SubtitleText.Text = $"{items.Count} project{(items.Count == 1 ? "" : "s")} selected for update";

        if (warnings > 0)
        {
            WarningBanner.Visibility = Visibility.Visible;
            WarningText.Text = $"{warnings} project{(warnings == 1 ? " has" : "s have")} compatibility warnings. Review the Warning column before applying.";
        }

        VerifyText.Text = verify
            ? $"After each update, 'gradlew {verifyTask}' runs. If it fails, the project is rolled back automatically. The new Gradle distribution is downloaded first."
            : "Build verification is off. Changes are written directly; enable verification in Settings to build and roll back on failure.";
        BackupText.Text = "Backups are created next to each modified wrapper/Gradle file and kept for manual rollback.";

        if (!string.IsNullOrWhiteSpace(extraNote))
        {
            ExtraText.Text = extraNote;
            ExtraText.Visibility = Visibility.Visible;
        }

        ApplyBtn.IsDefault = true;
        Loaded += (_, _) => ApplyBtn.Focus();
    }

    void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
