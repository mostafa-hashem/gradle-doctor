using System.Windows;
using System.Windows.Threading;

namespace GradleDoctor;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settings = AppSettings.Load();
            ThemeService.Apply(ThemeService.Parse(settings.ThemeMode));

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrash(ex);
            throw;
        }
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        try
        {
            DialogService.Error(e.Exception, "Unexpected error");
        }
        catch
        {
            MessageBox.Show(e.Exception.Message, "Gradle Doctor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    static void WriteCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GradleDoctor-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
