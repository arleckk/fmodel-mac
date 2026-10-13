using Avalonia;
using Avalonia.Media;
using Serilog;

namespace FModelMac;

internal static class Program
{
    // Inside a .app bundle stdout goes nowhere, so also log to the standard macOS logs folder
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "FModelMac");

    [STAThread]
    public static void Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(LogDirectory, "FModelMac-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        try
        {
            Log.Information("FModelMac {Version} starting from {Dir}",
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
