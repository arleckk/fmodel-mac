using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FModel.Services;

/// <summary>
/// FModel's ImGuiSink for exporter logs: CUE4Parse's exporters log with ObjectPath and ClassName in their context,
/// so the Export Session can show what happened to each asset.
/// </summary>
public sealed class ExportLogSink : ILogEventSink
{
    public static event Action<LogEvent>? Received;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.ContainsKey("ExporterV2")) Received?.Invoke(logEvent);
    }

    public static string? Property(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: { } scalar } ? scalar.ToString() : null;
}

public static class ExportLogSinkExtensions
{
    /// <summary>
    /// Exporter events down to Debug go to the sink; everything else keeps <paramref name="minimum"/> (Debug events
    /// of the rest of CUE4Parse are dropped before any sink).
    /// </summary>
    public static LoggerConfiguration WithExportLogs(this LoggerConfiguration configuration, LogEventLevel minimum = LogEventLevel.Information) =>
        configuration.MinimumLevel.Debug()
            .Filter.ByExcluding(e => e.Level < minimum && !e.Properties.ContainsKey("ExporterV2"))
            .WriteTo.Sink(new ExportLogSink());
}
