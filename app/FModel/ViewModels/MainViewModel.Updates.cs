using CommunityToolkit.Mvvm.ComponentModel;
using FModel.Services;

namespace FModel.ViewModels;

// FModel's update check (FModelApiEndpoint.CheckForUpdates, UpdateViewModel), against this port's GitHub releases
public sealed partial class MainViewModel
{
    /// <summary>The newest release above the running version, once a check found one (the status bar's "Update Available").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private ReleaseInfo? _availableUpdate;

    public bool IsUpdateAvailable => AvailableUpdate is not null;

    /// <summary>The releases from the last check, newest first (the Releases window's history).</summary>
    [ObservableProperty] private IReadOnlyList<ReleaseInfo> _releases = [];

    [ObservableProperty] private string _nextRefreshText = "";

    /// <summary>Raised on the UI thread when the launch check finds a newer release, so the window shows it.</summary>
    public event Action<ReleaseInfo>? UpdateFound;

    /// <summary>Asks GitHub for the releases; throws when it can't be reached.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> RefreshReleasesAsync(CancellationToken ct = default)
    {
        var releases = await UpdateChecker.GetReleasesAsync(ct);
        Releases = releases;
        AvailableUpdate = UpdateChecker.Newer(releases, UpdateChecker.Current);
        Settings.LastUpdateCheck = DateTime.Now;
        SaveSettings();
        return releases;
    }

    /// <summary>
    /// FModel's CheckForUpdates(launch: true): unless Remind Me silenced it, look for a newer release and show it; on the
    /// first launch after an update, open its release notes in a tab (FModel's ShowChangelog).
    /// </summary>
    public async Task CheckForUpdatesAtLaunchAsync()
    {
        NextRefreshText = UpdateChecker.NextRefreshText(Settings.NextUpdateCheck);
        var current = UpdateChecker.Current.ToString();
        // Settings from an earlier version without LastRunVersion: someone updating from 0.1.0
        var updated = Settings.LastRunVersion is { } last ? last != current : Settings.Profiles.Count > 0;
        if (Settings.LastRunVersion != current)
        {
            Settings.LastRunVersion = current;
            SaveSettings();
        }

        if (UpdateChecker.LaunchCheckDisabled) return;
        if (DateTime.Now < Settings.NextUpdateCheck)
        {
            Serilog.Log.Information("Update checks are silenced until {Date}", Settings.NextUpdateCheck);
            return;
        }
        try
        {
            var releases = await RefreshReleasesAsync();
            if (updated && releases.FirstOrDefault(r => r.Version == UpdateChecker.Current) is { Notes.Length: > 0 } installed)
                ShowReleaseNotes(installed);
            if (AvailableUpdate is { } update)
            {
                Log(LogKind.Warning, $"A new update is available! Version {update.Version} (Help › Releases)");
                UpdateFound?.Invoke(update);
            }
        }
        catch (Exception e)
        {
            // Offline is common: the log file is enough, FModel's error box would come up at every launch
            Serilog.Log.Warning(e, "Could not check for updates");
        }
    }

    /// <summary>FModel's RemindMeCommand.</summary>
    public void RemindMe(string? when)
    {
        Settings.NextUpdateCheck = UpdateChecker.RemindAt(when, DateTime.Now);
        NextRefreshText = UpdateChecker.NextRefreshText(Settings.NextUpdateCheck);
        SaveSettings();
    }

    /// <summary>A release's notes in a tab, like FModel's "Release Notes: x.y.z".</summary>
    public void ShowReleaseNotes(ReleaseInfo release)
    {
        IsExplorerVisible = false;
        var tab = TargetTab(newTab: SelectedTab?.Entry is not null || SelectedTab?.Document.TextLength > 0);
        tab.Reset(null);
        tab.Header = $"Release Notes: {release.Version}";
        tab.ApplyText($"# {release.Name}\n\n{release.Notes}\n", "Markdown");
    }
}
