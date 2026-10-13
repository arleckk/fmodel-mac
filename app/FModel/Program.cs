using Avalonia;
using Avalonia.Media;
using FModel.Services;
using Serilog;
using Serilog.Events;

namespace FModel;

internal static class Program
{
    // Inside a .app bundle stdout goes nowhere, so also log to the standard macOS logs folder
    public static string LogDirectory { get; } = AppSettings.MovedFromOldName(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "FModel"));

    [STAThread]
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WithExportLogs()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File(Path.Combine(LogDirectory, "FModel-.log"), restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        try
        {
            Log.Information("FModel {Version} starting from {Dir}",
                typeof(Program).Assembly.GetName().Version, AppContext.BaseDirectory);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Also used by the visual designer
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = "Helvetica Neue" })
        .LogToTrace();
}
