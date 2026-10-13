using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports.Material;
using Newtonsoft.Json;
using FModelMac;
using FModelMac.Rendering;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Objects.Engine.Animation;
using FModelMac.Services;
using FModelMac.ViewModels;
using FModelMac.Views;

// Drives the real windows headlessly (Skia rendering, no screen) against the CUE4Parse test
// fixtures and saves screenshots, so the UI can be checked without a display session.
// "--export <paks dir> <EGame> <aes> <outdir> [count]" instead exercises Export › Model/Animation
// on a real game, sampling packages by their usual name prefixes.

// Exporter logs feed the Export Session's per-asset logs, like in the app
Serilog.Log.Logger = new Serilog.LoggerConfiguration().WithExportLogs(Serilog.Events.LogEventLevel.Warning).CreateLogger();

if (args is ["--export", var paks, var gameVersion, var aesKey, var exportOut, ..])
{
    var count = args.Length > 5 ? int.Parse(args[5]) : 3;
    exportOut = Path.GetFullPath(exportOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(exportOut, "data"));
    new AppSettings { ArchiveDir = paks, Game = gameVersion, AesKey = aesKey, OutputDir = exportOut }.Save();
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), () => ExportTestAsync(count), TimeSpan.FromMinutes(10));
    return;
}

// "--audio <outdir> <paks dir> <EGame> <aes|-> <package|file>..." extracts and decodes sounds, saves them as WAV and
// plays a second of each (muted) through the macOS audio output
if (args is ["--audio", var audioOut, var audioPaks, var audioGame, var audioAes, ..])
{
    audioOut = Path.GetFullPath(audioOut);
    Directory.CreateDirectory(audioOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(audioOut, "data"));
    new AppSettings { ArchiveDir = audioPaks, Game = audioGame, AesKey = audioAes == "-" ? null : audioAes, UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP") }.Save();
    var avm = new MainViewModel();
    avm.InitializeGameAsync().GetAwaiter().GetResult();
    var extractor = new FModelMac.Services.Audio.SoundExtractor(avm.Archive!.Provider, audioPaks);
    Console.WriteLine($"vgmstream: {FModelMac.Services.Audio.Vgmstream.Locate() ?? "missing"}");
    Console.WriteLine($"devices: {string.Join(", ", FModelMac.Services.Audio.AudioOutput.OutputDevices().Select(d => d.Name))}; " +
                      $"default {FModelMac.Services.Audio.AudioOutput.DefaultOutputUid()}");
    using var output = new FModelMac.Services.Audio.AudioOutput { Volume = 0 };
    foreach (var path in args[5..])
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!avm.Archive.Provider.TryGetGameFile(path, out var gameFile)) { Console.WriteLine($"missing {path}"); continue; }
        var sounds = gameFile.IsUePackage
            ? extractor.FromExports(gameFile, avm.Archive.Provider.LoadPackage(gameFile).GetExports().ToArray())
            : extractor.FromFile(gameFile, out _);
        foreach (var warning in extractor.Warnings) Console.WriteLine($"  warning: {warning}");
        foreach (var sound in sounds)
        {
            try
            {
                var pcm = FModelMac.Services.Audio.AudioDecoder.Decode(sound.Data, sound.Extension);
                var peak = pcm.Samples.Length == 0 ? 0 : pcm.Samples.Max(MathF.Abs);
                var wav = Path.Combine(audioOut, sound.Name + ".wav");
                File.WriteAllBytes(wav, FModelMac.Services.Audio.AudioDecoder.ToWav(pcm));
                Console.WriteLine($"{sound.Path} [{sound.Extension}, {sound.Data.Length / 1024} KB] -> {pcm.Encoding}, {pcm.Channels} ch, " +
                                  $"{pcm.SampleRate} Hz, {pcm.Duration.TotalSeconds:0.00} s, peak {peak:0.00}, decoded in {watch.ElapsedMilliseconds} ms");
                var ended = false;
                output.Ended += () => ended = true;
                output.Load(pcm);
                output.Play();
                Thread.Sleep(400);
                output.Pause();
                var paused = output.Position;
                Thread.Sleep(200);
                Console.WriteLine($"    played to {paused.TotalSeconds:0.00} s, still {output.Position.TotalSeconds:0.00} s after pausing");
                output.Seek(pcm.Duration - TimeSpan.FromSeconds(0.3));
                output.Play();
                Thread.Sleep(700);
                Console.WriteLine($"    seek near the end -> ended={ended}, position {output.Position.TotalSeconds:0.00}/{pcm.Duration.TotalSeconds:0.00} s");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{sound.Path} [{sound.Extension}]: {e.GetType().Name}: {e.Message}");
            }
            watch.Restart();
        }
    }
    return;
}

// "--endpoints <outdir>" exercises the AES/mapping endpoints (uedb.dev presets), the mapping cache and fallback,
// and renders the endpoint editor headless after a Test
if (args is ["--endpoints", var endpointsOut])
{
    endpointsOut = Path.GetFullPath(endpointsOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(endpointsOut, "data"));
    foreach (var preset in Endpoints.Presets)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var aes = Endpoints.GetAesAsync(preset.Aes).GetAwaiter().GetResult();
        Console.WriteLine($"{preset.Name} AES: valid {aes.IsValid}, main {(aes.MainKey.Length > 10 ? aes.MainKey[..10] + "…" : "(none)")}, " +
                          $"{aes.DynamicKeys.Count} dynamic ({string.Join(", ", aes.DynamicKeys.Take(2).Select(k => $"{k.Name ?? "?"} {k.Guid[..8]}…"))}) in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var (path, source) = Endpoints.ResolveMappingAsync(preset.Mapping).GetAwaiter().GetResult();
        Console.WriteLine($"{preset.Name} mapping: {source} in {watch.ElapsedMilliseconds} ms");
        if (path is not null)
        {
            var mappings = Endpoints.OpenMappings(path);
            Console.WriteLine($"    parsed: {mappings.MappingsForGame?.Types.Count:N0} types, {mappings.MappingsForGame?.Enums.Count:N0} enums");
        }
        watch.Restart();
        (_, source) = Endpoints.ResolveMappingAsync(preset.Mapping).GetAwaiter().GetResult();
        Console.WriteLine($"    again: {source} in {watch.ElapsedMilliseconds} ms");
    }
    var broken = new EndpointConfig { Url = "https://uedb.dev/svc/api/v1/doesnotexist/mappings", Path = "$.mappings.ZStandard", IsValid = true };
    Console.WriteLine($"broken endpoint: {Endpoints.ResolveMappingAsync(broken).GetAwaiter().GetResult().Source}");
    var badAes = Endpoints.ReadAes(Newtonsoft.Json.Linq.JToken.Parse("{\"mainKey\":\"not a key\"}"), "$.['mainKey','dynamicKeys']");
    Console.WriteLine($"invalid key -> valid {badAes.IsValid}; FixKey(abc) = '{Endpoints.FixKey("abc")}'");

    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        var editor = new EndpointEditorWindow(null, EndpointType.Aes) { Width = 1100, Height = 680 };
        editor.Show();
        editor.FindControl<ComboBox>("PresetBox")!.SelectedIndex = 1;
        editor.FindControl<Button>("TestButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && editor.FindControl<Button>("OkButton")!.IsEnabled == false; i++) await Task.Delay(100);
        Console.WriteLine($"editor: OK enabled {editor.FindControl<Button>("OkButton")!.IsEnabled}, validity '{editor.FindControl<TextBlock>("ValidityText")!.Text}'");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        editor.CaptureRenderedFrame()?.Save(Path.Combine(endpointsOut, "endpoint-editor.png"));
        editor.Close();
        var settingsWindow = new SettingsWindow(new AppSettings { AesEndpoint = Endpoints.Presets[0].Aes, MappingEndpoint = Endpoints.Presets[0].Mapping });
        settingsWindow.Show();
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        settingsWindow.CaptureRenderedFrame()?.Save(Path.Combine(endpointsOut, "settings-endpoints.png"));
        settingsWindow.Close();
    }, TimeSpan.FromMinutes(2));
    return;
}

// "--versioning <outdir> <paks dir> <EGame> <aes|->" mounts with versioning overrides and a packages language,
// checks they reach CUE4Parse, switches language, and renders Settings › General and the Options editor
if (args is ["--versioning", var verOut, var verPaks, var verGame, var verAes])
{
    verOut = Path.GetFullPath(verOut);
    Directory.CreateDirectory(verOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(verOut, "data"));
    var verSettings = new AppSettings { ArchiveDir = verPaks, Game = verGame, AesKey = verAes == "-" ? null : verAes, AssetLanguage = "Spanish" };
    verSettings.Versioning[verPaks] = new VersioningSettings
    {
        Platform = "NintendoSwitch",
        Options = new Dictionary<string, bool> { ["MorphTarget"] = false },
        CustomVersions = [new CUE4Parse.UE4.Objects.Core.Serialization.FCustomVersion { Key = new CUE4Parse.UE4.Objects.Core.Misc.FGuid(1, 2, 3, 4), Version = 7 }]
    };
    verSettings.Save();
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        void Shot(TopLevel top, string name)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            top.CaptureRenderedFrame()?.Save(Path.Combine(verOut, name));
            Console.WriteLine($"Saved {name}");
        }
        var vm = new MainViewModel();
        Console.WriteLine($"settings reloaded: language {vm.Settings.AssetLanguage}, platform {vm.Settings.CurrentVersioning.Platform}, " +
                          $"custom versions {vm.Settings.CurrentVersioning.CustomVersions?.Count}");
        await vm.InitializeGameAsync();
        var versions = vm.Archive!.Provider.Versions;
        Console.WriteLine($"provider: {versions.Game}, platform {versions.Platform}, MorphTarget {versions["MorphTarget"]}, " +
                          $"custom versions {versions.CustomVersions?.Versions.Length}, files {vm.Archive.Provider.Files.Count:N0}");
        vm.Settings.AssetLanguage = "English";
        await vm.LoadLocalizationAsync();
        foreach (var log in vm.Logs) Console.WriteLine($"  log: {log.Kind} {log.Message}");
        // the fallback loader, on the only .locres this game has (Engine's, which are normally left out)
        var engine = vm.Archive.LoadLocalization(CUE4Parse.UE4.Versions.ELanguage.English, includeEngine: true);
        var sample = vm.Archive.Provider.Internationalization.SelectMany(n => n.Value.Select(e => $"{n.Key}/{e.Key} = {e.Value}")).FirstOrDefault();
        Console.WriteLine($"engine locres: {engine.Culture} {engine.Count:N0} entries from [{string.Join(", ", engine.Available)}], e.g. {sample}");
        var unchanged = vm.Archive.LoadLocalization(CUE4Parse.UE4.Versions.ELanguage.Japanese, includeEngine: true);
        Console.WriteLine($"japanese: {unchanged.Culture} loaded {unchanged.Loaded} count {unchanged.Count}");

        var settings = new SettingsWindow(vm.Settings);
        settings.Show();
        await Task.Delay(300);
        Shot(settings, "settings-general.png");
        var editor = new VersioningEditorWindow(VersioningPart.Options, versions.Game, vm.Settings.CurrentVersioning.Clone());
        editor.Show();
        await Task.Delay(300);
        Shot(editor, "versioning-options.png");
        editor.Close();
        settings.Close();
    }, TimeSpan.FromMinutes(3));
    return;
}

// "--export-options <outdir> <paks dir> <EGame> <aes|-> <package...>" exports models and textures with several
// combinations of FModel's export options, lists what each wrote, and renders Settings › Export
if (args is ["--export-options", var eoOut, var eoPaks, var eoGame, var eoAes, ..])
{
    eoOut = Path.GetFullPath(eoOut);
    if (Directory.Exists(eoOut)) Directory.Delete(eoOut, true);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(eoOut, "data"));
    new AppSettings
    {
        ArchiveDir = eoPaks, Game = eoGame, AesKey = eoAes == "-" ? null : eoAes,
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP")
    }.Save();
    var packages = args[5..];
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        var vm = new MainViewModel();
        await vm.InitializeGameAsync();
        var items = packages.Select(p => new AssetItem(vm.Archive!.Provider[p])).ToList();
        var defaults = JsonConvert.SerializeObject(vm.Settings);
        var replace = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
        var variants = new (string Name, Action<AppSettings> Apply)[]
        {
            ("defaults", _ => { }),
            ("ueformat-zstd-all-lods-sockets", s => { s.MeshExportFormat = EMeshFormat.UEFormat; s.MeshQuality = EMeshQuality.All; s.SocketExportFormat = ESocketFormat.Socket; }),
            ("actorx-no-materials", s => { s.MeshExportFormat = EMeshFormat.ActorX; s.SaveEmbeddedMaterials = false; }),
            ("jpeg-q40-all-mips", s => { s.TextureExportFormat = ETextureFormat.Jpeg; s.TextureQuality = 40; s.ExportAllTextureMips = true; }),
            ("tga-all-layers", s => { s.TextureExportFormat = ETextureFormat.Tga; s.MaterialExportFormat = EMaterialDepth.AllLayers; }),
            ("usd-forces-png", s => { s.MeshExportFormat = EMeshFormat.USD; s.TextureExportFormat = ETextureFormat.Jpeg; }),
            ("no-nanite", s => s.NaniteMeshExportFormat = ENaniteMeshFormat.NoNanite),
            ("nanite-last", s => s.NaniteMeshExportFormat = ENaniteMeshFormat.NaniteLast),
        };
        foreach (var (name, apply) in Environment.GetEnvironmentVariable("FMODELMAC_UI_ONLY") is null ? variants : [])
        {
            JsonConvert.PopulateObject(defaults, vm.Settings, replace);
            vm.Settings.OutputDir = Path.Combine(eoOut, name);
            vm.Settings.ExportImmediately = true;
            apply(vm.Settings);
            var logs = vm.Logs.Count;
            await vm.ExportModelCommand.ExecuteAsync(items);
            await vm.ExportTextureCommand.ExecuteAsync(items);
            var errors = vm.Logs.Skip(logs).Where(l => l.Kind != LogKind.Information).Select(l => l.Message).ToArray();
            var written = Directory.Exists(vm.Settings.ExportsDir)
                ? Directory.EnumerateFiles(vm.Settings.ExportsDir, "*", SearchOption.AllDirectories)
                    .Select(f => $"{Path.GetRelativePath(vm.Settings.ExportsDir, f).Split('/')[^1]} ({new FileInfo(f).Length / 1024.0:0.#} KB)").ToArray()
                : [];
            Console.WriteLine($"{name}: {written.Length} files | {string.Join(", ", written.Order().Take(14))}{(written.Length > 14 ? ", …" : "")}");
            Console.WriteLine($"  session: {string.Join(", ", vm.ExportSession.Groups.Select(g => $"{g.Name} {g.Items.Count}"))}");
            foreach (var error in errors) Console.WriteLine($"  ! {error}");
        }

        JsonConvert.PopulateObject(defaults, vm.Settings, replace);
        vm.Settings.NaniteMeshExportFormat = ENaniteMeshFormat.NaniteOnly;
        var settings = new SettingsWindow(vm.Settings);
        settings.Show();
        settings.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        settings.CaptureRenderedFrame()?.Save(Path.Combine(eoOut, "settings-export.png"));
        Console.WriteLine("Saved settings-export.png");
        settings.Close();

        // Second view: JPEG enables the quality slider, scrolled to the textures and materials
        vm.Settings.TextureExportFormat = ETextureFormat.Jpeg;
        vm.Settings.TextureQuality = 85;
        settings = new SettingsWindow(vm.Settings);
        settings.Show();
        settings.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        await Task.Delay(300);
        settings.GetVisualDescendants().OfType<ScrollViewer>().First(v => v.IsEffectivelyVisible).ScrollToEnd();
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        settings.CaptureRenderedFrame()?.Save(Path.Combine(eoOut, "settings-export-2.png"));
        Console.WriteLine("Saved settings-export-2.png");
        settings.Close();
    }, TimeSpan.FromMinutes(5));
    return;
}

// "--decode <file> <format> <out.f32>" decodes one sound with AudioDecoder and writes raw float samples
if (args is ["--decode", var decodeIn, var decodeFormat, var decodeOut])
{
    var pcm = FModelMac.Services.Audio.AudioDecoder.Decode(File.ReadAllBytes(decodeIn), decodeFormat);
    var raw = new byte[pcm.Samples.Length * 4];
    Buffer.BlockCopy(pcm.Samples, 0, raw, 0, raw.Length);
    File.WriteAllBytes(decodeOut, raw);
    Console.WriteLine($"{pcm.Encoding}, {pcm.Channels} ch, {pcm.SampleRate} Hz, {pcm.Frames} frames");
    return;
}

// "--audio-ui <outdir> <paks dir> <EGame> <aes|-> <package|file>..." renders the Audio Player (headless) while the
// first sound plays muted, plus the Settings window with its AUDIO section
if (args is ["--audio-ui", var uiOut, var uiPaks, var uiGame, var uiAes, ..])
{
    uiOut = Path.GetFullPath(uiOut);
    Directory.CreateDirectory(uiOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(uiOut, "data"));
    new AppSettings { ArchiveDir = uiPaks, Game = uiGame, AesKey = uiAes == "-" ? null : uiAes, OutputDir = uiOut,
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP"), AudioVolume = 0 }.Save();
    var uiPackages = args[5..];
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        void Shot(TopLevel top, string name)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            top.CaptureRenderedFrame()?.Save(Path.Combine(uiOut, name));
            Console.WriteLine($"Saved {name}");
        }

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 850 };
        window.Show(); // OnOpened mounts the game
        for (var i = 0; i < 600 && !(vm.Archives.Count > 0 && vm.IsReady); i++) await Task.Delay(100);
        foreach (var package in uiPackages)
        {
            if (!vm.Archive!.Provider.TryGetGameFile(package, out var file)) { Console.WriteLine($"missing {package}"); continue; }
            await vm.ExtractAsync(file);
        }
        var player = window.ShowAudioPlayer();
        Console.WriteLine($"playlist: {player.Tracks.Count} ({string.Join(", ", player.Tracks.Select(t => t.Name))}); " +
                          $"devices: {player.Devices.Count}; playing {player.Current?.Name} = {player.IsPlaying}");
        foreach (var log in vm.Logs.TakeLast(6)) Console.WriteLine($"  log: {log.Kind} {log.Message}");
        await Task.Delay(1500);
        var audioWindow = window.AudioPlayerView;
        if (audioWindow is not null)
        {
            audioWindow.Width = 1100;
            audioWindow.Height = 580;
            Shot(audioWindow, "audio-player.png");
            player.Seek(0.5);
            await Task.Delay(800);
            Console.WriteLine($"after seek to 50%: {player.Position.TotalSeconds:0.00}/{player.Audio?.Duration.TotalSeconds:0.00} s, playing {player.IsPlaying}");
            player.Filter = "Mega";
            Console.WriteLine($"filter 'Mega' -> {player.VisibleTracks.Count} visible");
            Shot(audioWindow, "audio-player-filtered.png");
            player.Filter = "";
            await player.SavePlaylistCommand.ExecuteAsync(null);
            Console.WriteLine($"saved playlist, last: {player.LastSavedPath}");
        }
        var settings = new SettingsWindow(vm.Settings);
        settings.Show();
        await Task.Delay(300);
        Shot(settings, "settings-audio.png");
        settings.Close();
        window.Close();
    }, TimeSpan.FromMinutes(3));
    return;
}

// "--json <paks dir> <EGame> <aes|-> <package>..." prints the exports as FModel's JSON (for inspecting assets)
if (args is ["--json", var jsonPaks, var jsonGame, var jsonAes, ..])
{
    var jsonOut = Path.Combine(Path.GetTempPath(), "fmodelmac-json");
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", jsonOut);
    new AppSettings
    {
        ArchiveDir = jsonPaks, Game = jsonGame, AesKey = jsonAes == "-" ? null : jsonAes,
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP")
    }.Save();
    var vm = new MainViewModel();
    vm.InitializeGameAsync().GetAwaiter().GetResult();
    var jsonProvider = vm.Archive!.Provider;
    if (args[4..] is ["--list-maps"])
    {
        foreach (var map in jsonProvider.Files.Values.Where(f => f.Extension == "umap").OrderBy(f => f.Path))
            Console.WriteLine($"{map.Path} {map.Size / 1024} KB");
        return;
    }

    // "--json ... --list-files <regex>" lists matching files plus a count per extension
    if (args[4..] is ["--list-files", var pattern])
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = jsonProvider.Files.Values.Where(f => regex.IsMatch(f.Path)).DistinctBy(f => f.Path).OrderBy(f => f.Path).ToArray();
        foreach (var group in matches.GroupBy(f => f.Extension).OrderByDescending(g => g.Count()))
            Console.WriteLine($"EXT {group.Key}: {group.Count()}");
        foreach (var file in matches.Take(400)) Console.WriteLine($"{file.Path} {file.Size / 1024} KB");
        return;
    }

    // "--json ... --refs <package...>" prints Find References (and times the pak import index) plus the metadata size
    if (args[4..] is ["--refs", .. var refTargets])
    {
        foreach (var target in refTargets)
        {
            var file = jsonProvider[target];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var refs = vm.Archive.FindReferences(file, new Progress<double>(p => Console.WriteLine($"  index {p:P0}")));
            Console.WriteLine($"{file.Path}: {refs.Count} references in {sw.Elapsed.TotalSeconds:F2}s | {vm.Archive.ReferenceIndexSummary}");
            foreach (var r in refs.Take(15)) Console.WriteLine($"  <- {r.Path}");
            var metadata = vm.Archive.PackageMetadata(file);
            Console.WriteLine($"  metadata: {metadata.Length:N0} chars, {metadata.Split('\n').Length:N0} lines");
        }
        return;
    }

    // "--json ... --decompile <package...>" prints Decompile Blueprint (first lines) and its timing
    if (args[4..] is ["--decompile", .. var bpTargets])
    {
        foreach (var target in bpTargets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cpp = vm.Archive.DecompileBlueprint(jsonProvider[target]);
            if (cpp is null) { Console.WriteLine($"{target}: no class"); continue; }
            var lines = cpp.Split('\n');
            Console.WriteLine($"{target}: {lines.Length:N0} lines in {sw.Elapsed.TotalSeconds:F2}s, " +
                              $"{lines.Count(l => l.TrimStart().StartsWith("// ") && l.Contains("Exception"))} failed class(es)");
            foreach (var line in lines.Take(int.TryParse(Environment.GetEnvironmentVariable("FMODELMAC_LINES"), out var n) ? n : 25))
                Console.WriteLine($"  | {line}");
        }
        return;
    }

    // "--json ... --backup <out dir>" creates a backup, reads it back and compares it with the game, timed
    if (args[4..] is ["--backup", var backupDir])
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (backupFile, written) = GameBackup.Create(jsonProvider, vm.Archive.ProjectName, backupDir);
        var created = sw.Elapsed.TotalSeconds;
        var read = GameBackup.Read(backupFile);
        var compared = GameBackup.Compare(jsonProvider, read);
        Console.WriteLine($"{Path.GetFileName(backupFile)}: {written:N0} files, {new FileInfo(backupFile).Length / 1024.0:N0} KB, created in {created:F2}s, " +
                          $"read+compared in {sw.Elapsed.TotalSeconds - created:F2}s: {compared.New.Count} new, {compared.Modified.Count} modified, {compared.Removed} removed");
        return;
    }

    // "--json ... --online-backup <dir>" lists FModel's backups, downloads the smallest and streams it against the game
    if (args[4..] is ["--online-backup", var onlineDir])
    {
        var list = OnlineBackups.ListAsync().GetAwaiter().GetResult();
        foreach (var b in list) Console.WriteLine($"  {b.Display} | {b.Meta?.Version} | {b.Meta?.Entries:N0} entries");
        var smallest = list.OrderBy(b => b.Size).First();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cached = Path.Combine(onlineDir, smallest.FileName);
        var downloaded = File.Exists(cached) && new FileInfo(cached).Length == smallest.Size
            ? cached
            : OnlineBackups.DownloadAsync(smallest, onlineDir).GetAwaiter().GetResult();
        Console.WriteLine($"downloaded {Path.GetFileName(downloaded)}: {new FileInfo(downloaded).Length:N0} bytes (listed {smallest.Size:N0}) in {sw.Elapsed.TotalSeconds:F1}s");
        sw.Restart();
        var all = GameBackup.ReadEntries(downloaded);
        var index = 0;
        int slashed = 0, projectStores = 0;
        foreach (var e in all)
        {
            if (e.Path.StartsWith('/')) slashed++;
            if (e.Path.EndsWith("ue.projectstore")) projectStores++;
            if (index is 3 or 50_000 or 2_000_000 or 6_000_000) Console.WriteLine($"  #{index}: {e.Path} {e.Size} {(e.IsEncrypted ? "encrypted" : "")}");
            index++;
        }
        Console.WriteLine($"  {slashed:N0} paths start with '/', {projectStores:N0} are ue.projectstore");
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        var compared = GameBackup.Compare(jsonProvider, downloaded);
        Console.WriteLine($"compared {compared.BackupCount:N0} entries (listed {smallest.Meta?.Entries:N0}) in {sw.Elapsed.TotalSeconds:F1}s: " +
                          $"{compared.New.Count} new, {compared.Modified.Count} modified, {compared.Removed:N0} removed | " +
                          $"peak working set {Environment.WorkingSet / 1048576:N0} MB, managed {(GC.GetTotalMemory(false) - before) / 1048576:N0} MB");
        return;
    }

    // "--json ... --scan-morphs" lists skeletal meshes that have morph targets
    if (args[4..] is ["--scan-morphs"])
    {
        var candidates = jsonProvider.Files.Values.Where(f => f.Extension == "uasset" && f.Path.StartsWith("Game/") &&
            !f.Name.StartsWith("T_") && !f.Name.StartsWith("MI_") && !f.Name.StartsWith("M_") &&
            !f.Path.Contains("/Sound/") && !f.Path.Contains("/2dAsset/") && !f.Path.Contains("/Luac/") && !f.Path.Contains("/Effect/")).ToArray();
        Console.WriteLine($"Scanning {candidates.Length} packages");
        int skeletal = 0, failed = 0;
        foreach (var file in candidates)
        {
            try
            {
                foreach (var mesh in jsonProvider.LoadPackage(file).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>())
                {
                    skeletal++;
                    if (mesh.MorphTargets is { Length: > 0 } morphs) Console.WriteLine($"{file.Path}: {morphs.Length} morph targets");
                }
            }
            catch (Exception e)
            {
                if (failed++ < 3) Console.WriteLine($"{file.Path}: {e.GetType().Name} {e.Message}");
            }
        }
        Console.WriteLine($"{skeletal} skeletal meshes, {failed} packages failed");
        return;
    }

    foreach (var package in args[4..])
    {
        var file = jsonProvider.Files.Values.FirstOrDefault(f => f.Path.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (file is null) { Console.WriteLine($"Not found: {package}"); continue; }
        var jsonExports = AssetPreview.LoadExports(jsonProvider, file);
        Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(jsonExports, Newtonsoft.Json.Formatting.Indented));
        foreach (var morph in jsonExports.OfType<CUE4Parse.UE4.Assets.Exports.Animation.UMorphTarget>())
            Console.WriteLine($"MORPH {morph.Name}: lods {morph.MorphLODModels.Length}, cpu deltas {morph.MorphLODModels.FirstOrDefault()?.Vertices.Length}, " +
                              $"compressed {(morph.TryGetCompressedLODModel(0, out var c) ? $"{c.PackedDeltaHeaders.Length} batches, {c.PackedDeltaData.Length} words" : "none")}");
    }
    return;
}

// "--viewer <outdir> <paks dir> <EGame> <aes|-> <usmap|-> <package>..." opens the real OpenGL 3D viewer
// (needs a logged-in macOS session) and saves a capture of each mesh
if (args is ["--viewer", var viewerOut, var viewerPaks, var viewerGame, var viewerAes, var viewerUsmap, ..])
{
    viewerOut = Path.GetFullPath(viewerOut);
    Directory.CreateDirectory(viewerOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(viewerOut, "data"));
    new AppSettings
    {
        ArchiveDir = viewerPaks, Game = viewerGame,
        AesKey = viewerAes == "-" ? null : viewerAes,
        UsmapPath = viewerUsmap == "-" ? null : viewerUsmap,
        OutputDir = viewerOut
    }.Save();
    var packages = args[6..];
    var desktop = AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = "Helvetica Neue" });
    RunOnUiThread(desktop, () => ViewerTestAsync(viewerOut, packages), TimeSpan.FromMinutes(5));
    return;
}

var fixtures = Path.GetFullPath(args.Length > 0 ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "../../../../../upstream/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8"));
var outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "snapshots");
Directory.CreateDirectory(outDir);

// Isolated settings pointing at the encrypted, unversioned Oodle fixture
var dataDir = Path.Combine(outDir, "data");
Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", dataDir);
Directory.CreateDirectory(dataDir);
new AppSettings
{
    ArchiveDir = Path.Combine(fixtures, "IoStore/Unversioned"),
    Game = "GAME_UE5_8",
    AesKey = "0x435545345061727365466978747572654145534B657930313233343536373839",
    UsmapPath = Path.Combine(fixtures, "Mappings/CUE4ParseFixtures-Oodle.usmap"),
    OutputDir = Path.Combine(outDir, "Output")
}.Save();

RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), RunAsync, TimeSpan.FromMinutes(2));
return;

// Run Avalonia on this thread and pump its dispatcher until the scripted flow finishes
void RunOnUiThread(AppBuilder builder, Func<Task> flow, TimeSpan timeout)
{
    builder.SetupWithoutStarting();
    SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
    var done = new CancellationTokenSource(timeout);
    var run = flow();
    run.ContinueWith(_ => done.Cancel(), TaskScheduler.Default);
    Dispatcher.UIThread.MainLoop(done.Token);
    if (!run.IsCompleted)
    {
        Console.WriteLine("Timed out waiting for the UI flow");
        return;
    }
    run.GetAwaiter().GetResult();
}

async Task ViewerTestAsync(string outputDir, string[] packages)
{
    var vm = new MainViewModel();
    await vm.InitializeGameAsync();
    if (vm.Archive is null)
    {
        foreach (var log in vm.Logs) Console.WriteLine($"[{log.Kind}] {log.Message}");
        return;
    }
    var provider = vm.Archive.Provider;
    Console.WriteLine($"Init: {vm.StatusText} | {vm.StatusExtra}");

    var viewer = new ModelViewerWindow { Width = 1280, Height = 800 };
    var ready = new TaskCompletionSource();
    viewer.ModelViewport.RendererReady += (_, _) => ready.TrySetResult();
    viewer.Show();
    await Task.WhenAny(ready.Task, Task.Delay(10000));
    Console.WriteLine($"Renderer: {viewer.ModelViewport.RendererInfo ?? "never initialized"}");

    async Task Capture(string name)
    {
        await Task.Delay(150);
        var shot = viewer.ModelViewport.CaptureAsync();
        if (await Task.WhenAny(shot, Task.Delay(5000)) != shot) { Console.WriteLine("    capture timed out"); return; }
        var path = Path.Combine(outputDir, $"{name}.png");
        await File.WriteAllBytesAsync(path, shot.Result);
        Console.WriteLine($"    saved {path}");
    }

    foreach (var package in packages)
    {
        // "morph:<name or *>=<weight>" moves morph target sliders on the current model
        if (package.StartsWith("morph:"))
        {
            var (name, weight) = (package[6..].Split('=')[0], float.Parse(package.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture));
            var target = viewer.Scene.Items.LastOrDefault()?.Single;
            foreach (var morph in target?.Model.Morphs ?? [])
                if (name == "*" || morph.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) morph.Weight = weight;
            await Capture($"morph-{(name == "*" ? "all" : name)}-{weight:0.##}");
            if (target is not null)
                Console.WriteLine($"    applied weights: [{string.Join(", ", viewer.ModelViewport.AppliedMorphWeights(target).Select(w => w.ToString("0.##")))}]");
            continue;
        }

        // "clear" empties the scene; "capture:<name>" saves the current view
        if (package == "clear")
        {
            viewer.ClearScene();
            continue;
        }
        if (package.StartsWith("capture:"))
        {
            await Capture(package[8..]);
            continue;
        }

        var file = provider.Files.Values.FirstOrDefault(f => f.Path.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            Console.WriteLine($"Not found: {package}");
            continue;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var exports = AssetPreview.LoadExports(provider, file);
        if (exports.OfType<UAnimationAsset>().FirstOrDefault(a => a is UAnimSequence or UAnimMontage or UAnimComposite or UBlendSpaceBase) is { } asset)
        {
            IViewerMotion motion = await Task.Run<IViewerMotion>(() => asset is UBlendSpaceBase bs ? ViewerBlendSpace.FromExport(bs) : ViewerAnimation.FromExport(asset));
            var error = viewer.PlayAnimation(motion);
            viewer.Pause();
            Console.WriteLine($"{motion.Name}: {motion.Clips.Count} clip(s) [{string.Join(", ", motion.Clips.Select(c => $"{c.Name} {c.Duration:0.00}s"))}], " +
                              $"skeleton {motion.SkeletonName}, curves {motion.Clips.Sum(c => c.Segments.Sum(sg => sg.Curves.Length))}, decoded in {watch.ElapsedMilliseconds} ms" +
                              (error is null ? "" : $" -> {error}"));
            if (error is not null) continue;

            if (motion is ViewerBlendSpace blendSpace)
            {
                Console.WriteLine($"    axes: {string.Join(", ", blendSpace.Axes.Select(x => $"{x.Name} {x.Min}..{x.Max} grid {x.GridNum}"))}; " +
                                  $"samples at {string.Join(" ", blendSpace.SampleValues.Select(v => $"({v.X},{v.Y})"))}; grid points {blendSpace.Grid.Length}");
                var ax = blendSpace.Axes[0];
                var ay = blendSpace.Axes.Length > 1 ? blendSpace.Axes[1] : null;
                var points = ay is null
                    ? new[] { 0f, 0.25f, 0.5f, 1f }.Select(f => (ax.Min + (ax.Max - ax.Min) * f, 0f)).ToArray()
                    : [(ax.Min, ay.Min), (90f, ay.Min), (-45f, ay.Max), (135f, (ay.Min + ay.Max) / 2), (ax.Max, ay.Max)];
                var step = 0;
                foreach (var (x, y) in points)
                {
                    var f = step++ / 4f;
                    viewer.SetBlendParameters(x, y);
                    viewer.Seek(viewer.AnimationTime); // keep phase
                    var weights = blendSpace.Weights(x, y);
                    Console.WriteLine($"    params ({x:0.#},{y:0.#}) -> " + string.Join(", ", weights.Select((w, i) => (w, i)).Where(p => p.w > 0.005f)
                        .Select(p => $"{blendSpace.Samples[p.i].Name}@({blendSpace.SampleValues[p.i].X:0},{blendSpace.SampleValues[p.i].Y:0}) {p.w:0.00}")));
                    await Capture($"bs-{blendSpace.Name}-{f:0.00}");
                }
            }
            else
            {
                var animation = (ViewerAnimation) motion;
                viewer.ModelViewport.ShowBones = true;
                for (var step = 0; step < 4; step++)
                {
                    viewer.Seek(animation.Duration * step / 4);
                    Console.WriteLine($"    t={viewer.AnimationTime:0.00}s");
                    await Capture($"anim-{animation.Name}-{step}");
                }
                viewer.ModelViewport.ShowBones = false;
            }

            // Real-time playback driven by the window's clock
            viewer.PlayAnimation(motion);
            await Task.Delay(600);
            Console.WriteLine($"    playing 0.6 s of wall time -> t={viewer.AnimationTime:0.00}s");
            continue;
        }

        if (exports.OfType<CUE4Parse.UE4.Objects.Engine.UWorld>().FirstOrDefault() is { } world)
        {
            var items = await Task.Run(() => MapLoader.Load(world, new Progress<string>(m => { if (m.StartsWith("Reading")) Console.WriteLine($"    {m}"); })));
            viewer.ShowMap(items);
            var map = items[0];
            foreach (var extra in items.Skip(1))
                Console.WriteLine($"    {(extra.IsVisible ? "shown" : "hidden")}: {extra.Name}: {extra.Details}");
            Console.WriteLine($"{map.Name}: {map.Details}, built in {watch.ElapsedMilliseconds} ms; " +
                              $"bounds {(map.Max - map.Min) / 100} m; textures {map.Objects.SelectMany(o => o.Model.Sections).Select(x => x.Texture).OfType<ViewerTexture>().Distinct().Count()}");
            foreach (var o in map.Objects.OrderByDescending(o => o.Instances.Length).Take(5))
                Console.WriteLine($"    {o.Model.Name} ({o.Model.Kind}) × {o.Instances.Length}");
            await Capture($"map-{map.Name}");
            continue;
        }

        // A lone skeleton opens as bones only, like "Handle Skeletons as Empty Meshes"
        var mesh = exports.FirstOrDefault(ViewerModel.CanView) ?? exports.OfType<USkeleton>().FirstOrDefault();
        if (mesh is null)
        {
            Console.WriteLine($"No mesh in {package}");
            continue;
        }
        var model = await Task.Run(() => ViewerModel.FromExport(mesh));
        viewer.AddModel(model);
        Console.WriteLine($"{model.Name}: {model.Kind}, {model.VertexCount:N0} verts, {model.TriangleCount:N0} tris, " +
                          $"radius {model.Radius:0.#}, {model.Bones?.Length ?? 0} bones, built in {watch.ElapsedMilliseconds} ms" +
                          $"{(model.VertexCount == 0 ? $", bones shown: {viewer.ModelViewport.ShowBones}" : "")}");
        foreach (var section in model.Sections) Console.WriteLine($"    {section.MaterialName}: {section.Details}");
        foreach (var morph in model.Morphs)
            Console.WriteLine($"    morph {morph.Name}: {morph.Details} " + string.Join(" ", morph.VertexIndices.Select((v, i) =>
                $"v{v}=({morph.Deltas[i * 6]:0.##},{morph.Deltas[i * 6 + 1]:0.##},{morph.Deltas[i * 6 + 2]:0.##})")));

        await Task.Delay(300);
        var capture = viewer.ModelViewport.CaptureAsync();
        if (await Task.WhenAny(capture, Task.Delay(5000)) != capture)
        {
            Console.WriteLine("    capture timed out (no frame rendered)");
            continue;
        }
        var path = Path.Combine(outputDir, $"viewer-{model.Name}.png");
        await File.WriteAllBytesAsync(path, capture.Result);
        Console.WriteLine($"    saved {path}");
    }
    // The GL surface doesn't show up in a RenderTargetBitmap, but the rest of the window layout does
    var scale = viewer.RenderScaling;
    using var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(
        new PixelSize((int) (viewer.Bounds.Width * scale), (int) (viewer.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
    frame.Render(viewer);
    frame.Save(Path.Combine(outputDir, "viewer-window.png"));
    viewer.Close();
}

async Task ExportTestAsync(int count)
{
    var vm = new MainViewModel();
    vm.Settings.ExportImmediately = true;
    await vm.InitializeGameAsync();
    Console.WriteLine($"Init: {vm.StatusText} | {vm.StatusExtra} | archives {vm.Archives.Count}");
    await vm.LoadCommand.ExecuteAsync(null);
    var files = vm.AllFiles().Where(f => f.Extension == "uasset").ToArray();
    Console.WriteLine($"Packages: {files.Length}");
    files = files.OrderBy(f => f.Path.StartsWith("Engine/") ? 1 : 0).ToArray(); // the game's own content first
    var prefixes = files.Where(f => !f.Path.StartsWith("Engine/")).GroupBy(f => f.Name.Split('_')[0] + "_")
        .OrderByDescending(g => g.Count()).Take(30).Select(g => $"{g.Key}{g.Count()}");
    Console.WriteLine($"Prefixes: {string.Join(" ", prefixes)}");
    File.WriteAllLines(Path.Combine(AppSettings.DataDirectory, "packages.txt"), files.Select(f => f.Path));

    var skeletons = files.Where(f => f.NameWithoutExtension.EndsWith("_Skeleton"))
        .Select(f => f.PathWithoutExtension[..^"_Skeleton".Length]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    async Task Run(string label, IAsyncRelayCommand<System.Collections.IList?> command, Func<GameFile, bool> pick)
    {
        var sample = files.Where(pick).Take(count).Select(f => new AssetItem(f)).ToList();
        Console.WriteLine($"--- {label}: {string.Join(", ", sample.Select(i => i.File.Path))}");
        var before = vm.Logs.Count;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await command.ExecuteAsync(sample);
        Console.WriteLine($"    {vm.StatusText} in {watch.Elapsed.TotalSeconds:F1}s");
        foreach (var log in vm.Logs.Skip(before)) Console.WriteLine($"    [{log.Kind}] {log.Message}");
    }

    // Name conventions vary per game: SM_ for static meshes, "<mesh>_Skeleton" next to skeletal meshes
    await Run("static meshes", vm.ExportModelCommand, f => f.Name.StartsWith("SM_"));
    await Run("skeletal meshes", vm.ExportModelCommand, f => f.Name.StartsWith("SK_") || skeletons.Contains(f.PathWithoutExtension));
    await Run("animations", vm.ExportAnimationCommand,
        f => f.Name.StartsWith("A_") || f.Name.StartsWith("AS_") || f.Path.Contains("/Motion/") || f.Path.Contains("/Animations/"));
}

async Task RunAsync()
{
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1600, Height = 950 };
    window.Show(); // OnOpened mounts the configured game, like the real app

    await WaitFor(() => vm.Archives.Count > 0 && vm.IsReady, "archives");
    Console.WriteLine($"Init: {vm.StatusText} | {vm.StatusExtra} | archives {vm.Archives.Count} | title '{vm.Title}'");
    vm.SelectedArchive = vm.Archives.First(a => a.IsEnabled);
    Save(window, "1-archives.png");

    await vm.LoadCommand.ExecuteAsync(null);
    var textures = vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Textures");
    Console.WriteLine($"Load: {vm.StatusText} | folders tab {vm.SelectedLeftTab} | {vm.FolderSummary}");
    Save(window, "2-folders.png");
    await InputTestAsync(window, vm);

    vm.SelectedLeftTab = 2;
    vm.SelectedPackage = vm.Packages.First(p => p.Name == "T_Streaming.uasset");
    await vm.ExtractAsync(vm.SelectedPackage.File);
    Console.WriteLine($"Extract: {vm.StatusText} | tab '{vm.SelectedTab?.Header}' image {vm.SelectedTab?.HasImage} | {vm.PackagesHeader}");
    Save(window, "3-packages-texture.png");

    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Meshes");
    vm.SelectedPackage = vm.Packages.First(p => p.Name == "SK_Fixture.uasset");
    await vm.ExtractAsync(vm.SelectedPackage.File, newTab: true);
    await vm.ExportPropertiesCommand.ExecuteAsync(null);
    vm.Settings.ExportImmediately = true; // the Export Session queue is tested at the end
    await vm.ExportModelCommand.ExecuteAsync(null);
    vm.SelectedPackage = vm.Packages.First(p => p.Name == "SM_Fixture.uasset");
    await vm.ExportModelCommand.ExecuteAsync(null);
    Console.WriteLine($"New tab: {vm.Tabs.Count} tabs | logs {vm.Logs.Count} | last export {vm.LastExportPath}");
    Save(window, "4-json-tab.png");

    var aes = new AesManagerWindow(vm.Settings, vm.Archives);
    aes.Show();
    Save(aes, "5-aes.png");
    aes.Close();

    var selector = new DirectorySelectorWindow(vm.Settings);
    selector.Show();
    Save(selector, "6-directory-selector.png");
    selector.Close();

    var search = new SearchWindow(vm);
    search.Show();
    await Task.Delay(400);
    Save(search, "7-search.png");
    search.Close();

    // Show Metadata in a new tab, then Find References into the search window's References tab
    var skeleton = vm.AllFiles().FirstOrDefault(f => f.Name == "SKEL_Fixture.uasset") ?? vm.AllFiles().First(f => f.IsUePackage);
    await vm.ShowMetadataAsync(skeleton);
    Console.WriteLine($"Metadata: tab '{vm.SelectedTab?.Header}' {vm.SelectedTab?.Document.LineCount} lines | {vm.StatusText}");
    Save(window, "8-metadata.png");
    var references = new SearchWindow(vm);
    vm.ReferencesReady += refs => references.ShowReferences(refs);
    references.Show();
    await vm.FindReferencesAsync(skeleton);
    references.FocusTab(SearchTab.References);
    await Task.Delay(400);
    Console.WriteLine($"References: {vm.References?.Files.Length} files of '{vm.References?.Package.Name}' | {vm.StatusText}");
    Save(references, "9-references.png");
    references.Close();

    // Decompile Blueprint in a new tab
    var blueprint = vm.AllFiles().FirstOrDefault(f => f.Name == "BP_Components.uasset");
    if (blueprint is not null)
    {
        await vm.DecompileAsync(blueprint);
        Console.WriteLine($"Decompile: tab '{vm.SelectedTab?.Header}' {vm.SelectedTab?.Document.LineCount} lines | {vm.StatusText}");
        Save(window, "10-decompiled.png");
    }

    // Favorite Directories: add, go to (found and missing), persisted, menu built, dialog
    var presetsBefore = vm.Favorites.Count;
    vm.AddFavorite(new FavoriteDirectory("Textures", "CUE4ParseFixtures/Content/Fixtures/Textures/"));
    vm.AddFavorite(new FavoriteDirectory("Missing", "CUE4ParseFixtures/Content/Nope"));
    vm.SelectedLeftTab = 0;
    var found = await vm.GoToFolderAsync(vm.Favorites[^2].Path);
    var foundTab = vm.SelectedLeftTab;
    var missing = await vm.GoToFolderAsync(vm.Favorites[^1].Path);
    var saved = AppSettings.Load().FavoriteDirectories.Values.Sum(l => l.Count);
    var menu = window.FindControl<MenuItem>("FavoritesMenu")!.ItemsSource!.Cast<object>().Count();
    Console.WriteLine($"Favorites: {presetsBefore} preset(s) for this game, Fortnite {FavoriteDirectory.Presets("FortniteGame").Count}, " +
                      $"VALORANT {FavoriteDirectory.Presets("ShooterGame").Count} | go to '{vm.Favorites[^2].Path}': {found}, tab {foundTab}, " +
                      $"folder '{vm.SelectedFolder?.FullPath}' | missing: {missing} ({vm.Logs[^1].Message}) | saved {saved} | menu items {menu}");
    var favoriteDialog = new FavoriteDirectoryWindow(new FavoriteDirectory("Meshes", "CUE4ParseFixtures/Content/Fixtures/Meshes"), vm.FolderExists);
    favoriteDialog.Show();
    await Task.Delay(200);
    Save(favoriteDialog, "14-favorite-directory.png");
    favoriteDialog.Close();
    vm.RemoveFavorite(vm.Favorites[^1]);
    Console.WriteLine($"After delete: {vm.Favorites.Count} favorite(s), menu items {window.FindControl<MenuItem>("FavoritesMenu")!.ItemsSource!.Cast<object>().Count()}");

    // Backup: create one, compare with it (nothing changed), then with an "older" one missing 5 files and with 3
    // sizes changed, through Loading Mode "All (New)"/"All (Modified)" and the Backup window
    var backupPath = await vm.CreateBackupAsync();
    var backupEntries = GameBackup.Read(backupPath!);
    var same = GameBackup.Compare(vm.Archive!.Provider, backupEntries);
    var packagesInBackup = backupEntries.Where(b => b.Path.EndsWith(".uasset")).ToArray();
    var dropped = packagesInBackup.Take(5).Select(b => b.Path).ToHashSet();
    var resized = packagesInBackup.Skip(5).Take(3).Select(b => b.Path).ToHashSet();
    var olderPath = Path.Combine(Path.GetDirectoryName(backupPath)!, "CUE4ParseFixtures_older.fbkp");
    GameBackup.Write(backupEntries.Where(b => !dropped.Contains(b.Path))
        .Select(b => resized.Contains(b.Path) ? b with { Size = b.Size + 1 } : b)
        .Append(new BackupEntry("CUE4ParseFixtures/Content/Removed/Gone.uasset", 10, false)).ToArray(), olderPath);
    vm.PickBackupFile = () => Task.FromResult<string?>(olderPath);
    vm.LoadingMode = MainViewModel.LoadNew;
    await vm.LoadCommand.ExecuteAsync(null);
    var newLog = vm.Logs[^1].Message;
    var newCount = vm.Folders.VisibleFolders.Count;
    var modified = await vm.LoadBackupChangesAsync(olderPath, newFiles: false);
    Console.WriteLine($"Backup: {Path.GetFileName(backupPath)} {backupEntries.Count} entries, {new FileInfo(backupPath!).Length} bytes | " +
                      $"same game: {same.New.Count} new, {same.Modified.Count} modified, {same.Removed} removed");
    Console.WriteLine($"  All (New): {newLog} | folders shown {newCount}");
    Console.WriteLine($"  All (Modified): {vm.Logs[^1].Message} | match {modified?.New.SetEquals(dropped)} {modified?.Modified.SetEquals(resized)}");
    vm.LoadingMode = "All";
    await vm.LoadCommand.ExecuteAsync(null);
    // FMODELMAC_ONLINE=1 also lists FModel's backups (a request to its service)
    var online = Environment.GetEnvironmentVariable("FMODELMAC_ONLINE") == "1";
    var backupWindow = new BackupWindow(vm, loadOnline: online);
    backupWindow.Show();
    await Task.Delay(online ? 3000 : 200);
    Save(backupWindow, "15-backup.png");
    backupWindow.Close();

    // Image Merger: textures added from their tabs, a TIFF converted by sips, the layout, the window
    foreach (var name in new[] { "T_BC1.uasset", "T_BC7.uasset", "T_Mips.uasset", "T_BGRA8.uasset", "T_G8.uasset" })
    {
        await vm.ExtractAsync(vm.AllFiles().First(f => f.Name == name));
        vm.AddImageToMergerCommand.Execute(null);
    }
    var tiffSource = Path.Combine(Path.GetTempPath(), "fmodelmac-merger.png");
    File.WriteAllBytes(tiffSource, vm.MergerImages[0].Data);
    var tiffPath = Path.ChangeExtension(tiffSource, ".tiff");
    System.Diagnostics.Process.Start("/usr/bin/sips", ["-s", "format", "tiff", tiffSource, "--out", tiffPath])!.WaitForExit();
    var tiff = ImageMerger.Load(tiffPath);
    File.Delete(tiffSource);
    File.Delete(tiffPath);
    if (tiff is not null) vm.MergerImages.Add(tiff with { Name = "From TIFF" });
    var sizes = string.Join(", ", vm.MergerImages.Select(i => $"{i.Name} {i.Details}"));
    var (_, mergedW, mergedH) = ImageMerger.Merge(vm.MergerImages.ToArray(), 3, 5);
    Console.WriteLine($"Image Merger: {vm.MergerImages.Count} images ({sizes}) | TIFF read: {tiff is not null} | 3 per row, margin 5: {mergedW}×{mergedH}");
    var merger = new ImageMergerWindow(vm);
    merger.Show();
    await Task.Delay(800);
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine($"  window: {merger.FindControl<TextBlock>("ResultText")!.Text}");
    Save(merger, "16-image-merger.png");
    var mergerList = merger.FindControl<ListBox>("ImagesList")!;
    mergerList.SelectedItems!.Add(vm.MergerImages[2]);
    merger.FindControl<Button>("UpButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var afterUp = string.Join(",", vm.MergerImages.Select(i => i.Name));
    merger.FindControl<Button>("RemoveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Console.WriteLine($"  move up T_Mips: {afterUp} | remove it: {string.Join(",", vm.MergerImages.Select(i => i.Name))}");
    merger.Close();

    // Export Session: queue meshes, textures, an animation and a world, then export them together
    vm.Settings.ExportImmediately = false;
    async Task Queue(IAsyncRelayCommand<System.Collections.IList?> command, params string[] names) =>
        await command.ExecuteAsync(vm.AllFiles().Where(f => names.Contains(f.Name)).Select(f => new AssetItem(f)).ToList());
    await Queue(vm.ExportModelCommand, "SK_Fixture.uasset", "SM_Fixture.uasset", "SM_Nanite.uasset");
    await Queue(vm.ExportTextureCommand, "T_Mips.uasset", "T_Array.uasset", "T_FloatRGBA.uasset");
    await Queue(vm.ExportAnimationCommand, "AS_Fixture.uasset");
    await Queue(vm.ExportWorldCommand, "Empty.umap");
    Console.WriteLine($"Queued: {vm.ExportSession.QueuedCount} in {vm.ExportSession.Groups.Count} classes | {vm.Logs[^1].Message}");
    var session = new ExportSessionWindow(vm);
    session.Show();
    await Task.Delay(300);
    Save(session, "11-export-session-queue.png");
    await vm.RunExportSessionAsync();
    foreach (var item in vm.ExportSession.Groups.SelectMany(g => g.Items).Where(i => i.Logs.Count > 0 && !i.IsDependency).Take(2))
        item.IsExpanded = true;
    await Task.Delay(300);
    var es = vm.ExportSession;
    Console.WriteLine($"Export Session: {es.SucceededCount} ok, {es.FailedCount} failed, {es.Groups.Sum(g => g.Items.Count)} items " +
                      $"({string.Join(", ", es.Groups.Select(g => $"{g.Name} {g.Items.Count}"))}) | {es.ElapsedText} | {vm.Logs[^1].Message}");
    foreach (var item in es.Groups.SelectMany(g => g.Items))
        Console.WriteLine($"  {item.ClassName}/{item.Name}: {item.StateText}{(item.Error is null ? "" : $" ({item.Error})")}, {item.Logs.Count} log(s)");
    Save(session, "12-export-session-done.png");
    vm.ExportSession.OverrideOptions = true;
    session.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
    await Task.Delay(300);
    Save(session, "13-export-session-options.png");
    session.Close();

    await MinorSettingsTestAsync(window, vm);

    window.Close();
}

// Settings › Preview/Hotkeys/Themes, Keep Directory Structure, Export › Raw Data and Decompiled, skeletons as meshes
async Task MinorSettingsTestAsync(MainWindow window, MainViewModel vm)
{
    GameFile File(string name) => vm.AllFiles().First(f => f.Name == name);
    List<AssetItem> Items(params string[] names) => names.Select(n => new AssetItem(File(n))).ToList();

    // Old settings had one "PreviewMeshes" switch; new ones round-trip hotkeys and the theme
    var migrated = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>("{\"PreviewMeshes\": false}")!;
    var roundTrip = new AppSettings { JsonHighlightTheme = JsonHighlightTheme.Nord };
    roundTrip.Hotkeys.ExportProperties = new Hotkey(Key.P, KeyModifiers.Meta | KeyModifiers.Shift);
    var json = Newtonsoft.Json.JsonConvert.SerializeObject(roundTrip);
    var back = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json)!;
    Console.WriteLine($"Settings: old PreviewMeshes=false -> static {migrated.PreviewStaticMeshes}, skeletal {migrated.PreviewSkeletalMeshes}, " +
                      $"animations {migrated.PreviewAnimations}, worlds {migrated.PreviewWorlds} | round trip: theme {back.JsonHighlightTheme}, " +
                      $"export properties '{back.Hotkeys.ExportProperties}', add tab '{back.Hotkeys.AssetAddTab}', none '{back.Hotkeys.ExportData}' | " +
                      $"has PreviewMeshes: {json.Contains("PreviewMeshes")} | saved as {json[json.IndexOf("\"ExportProperties\"")..].Split(',')[0]}");

    // Per-kind previews: skeletal meshes off, static meshes on
    var opened = 0;
    void OnModel(FModelMac.Rendering.ViewerModel _) => opened++;
    vm.ModelReady += OnModel;
    vm.Settings.PreviewSkeletalMeshes = false;
    await vm.ExtractAsync(File("SK_Fixture.uasset"));
    var skeletalOpened = opened;
    await vm.ExtractAsync(File("SM_Fixture.uasset"));
    var staticOpened = opened - skeletalOpened;
    vm.Settings.PreviewSkeletalMeshes = true;

    // Skeletons as empty meshes: in the viewer (bones only) and in Export › Model (USD, glTF can't)
    await vm.ExtractAsync(File("SKEL_Fixture.uasset"));
    var skeletonOff = opened - skeletalOpened - staticOpened;
    vm.Settings.SaveSkeletonAsMesh = true;
    await vm.ExtractAsync(File("SKEL_Fixture.uasset"));
    var skeletonOn = opened - skeletalOpened - staticOpened - skeletonOff;
    vm.ModelReady -= OnModel;
    Console.WriteLine($"Preview: skeletal off -> {skeletalOpened} opened, static on -> {staticOpened} | skeleton: off {skeletonOff}, " +
                      $"as mesh {skeletonOn} ({vm.Logs.LastOrDefault(l => l.Message.Contains("skeleton"))?.Message})");

    // Export › Raw Data and Model (with the skeleton) through the Export Session
    vm.ExportSession.Clear();
    await vm.ExportRawDataCommand.ExecuteAsync(Items("SK_Fixture.uasset", "T_Mips.uasset"));
    await vm.ExportModelCommand.ExecuteAsync(Items("SKEL_Fixture.uasset"));
    await vm.RunExportSessionAsync();
    foreach (var item in vm.ExportSession.Groups.SelectMany(g => g.Items).Where(i => !i.IsDependency))
        Console.WriteLine($"  {item.ClassName}/{item.Name}: {item.StateText}{(item.Error is null ? "" : $" ({item.Error})")} -> " +
                          $"{string.Join(", ", vm.ExportSession.Groups.SelectMany(g => g.Items).Where(i => i == item).Select(i => i.FirstFile is null ? "-" : Path.GetRelativePath(vm.Settings.OutputDir, i.FirstFile)))}");
    var rawDir = Path.Combine(vm.Settings.ExportsDir, Path.GetDirectoryName(File("SK_Fixture.uasset").Path)!);
    Console.WriteLine($"Raw data: {string.Join(", ", Directory.Exists(rawDir) ? Directory.GetFiles(rawDir, "SK_Fixture.*").Select(Path.GetFileName) : [])}");
    vm.Settings.SaveSkeletonAsMesh = false;

    // Keep Directory Structure off: properties and decompiled blueprints land straight in Exports
    vm.Settings.KeepDirectoryStructure = false;
    await vm.ExportPropertiesCommand.ExecuteAsync(Items("T_Mips.uasset"));
    var flatJson = Path.Combine(vm.Settings.ExportsDir, "T_Mips.json");
    await vm.ExportCodeCommand.ExecuteAsync(Items("BP_Components.uasset"));
    var flatCpp = Path.Combine(vm.Settings.ExportsDir, "BP_Components.cpp");
    Console.WriteLine($"Keep structure off: {Path.GetFileName(flatJson)} {System.IO.File.Exists(flatJson)}, {Path.GetFileName(flatCpp)} {System.IO.File.Exists(flatCpp)} " +
                      $"({(System.IO.File.Exists(flatCpp) ? System.IO.File.ReadLines(flatCpp).Count() : 0)} lines)");
    vm.Settings.KeepDirectoryStructure = true;
    System.IO.File.Delete(flatJson);
    System.IO.File.Delete(flatCpp);

    // Hotkeys, pressed for real on the main window
    void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers);
        window.KeyRelease(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
    var packages = window.FindControl<ListBox>("PackagesList")!;
    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Textures");
    vm.SelectedLeftTab = 2;
    Dispatcher.UIThread.RunJobs();
    packages.Focus();
    var tabs = vm.Tabs.Count;
    Press(Key.T, RawInputModifiers.Meta);
    var added = vm.Tabs.Count - tabs;
    var index = vm.Tabs.IndexOf(vm.SelectedTab!);
    Press(Key.Q);
    var left = vm.Tabs.IndexOf(vm.SelectedTab!);
    Press(Key.E);
    var right = vm.Tabs.IndexOf(vm.SelectedTab!);
    Press(Key.W, RawInputModifiers.Meta);
    var removed = tabs + added - vm.Tabs.Count;
    Press(Key.A);
    var dirLeft = vm.SelectedLeftTab;
    Press(Key.D);
    var dirRight = vm.SelectedLeftTab;
    Press(Key.A, RawInputModifiers.Meta); // ⌘A is not A
    var cmdA = vm.SelectedLeftTab;
    var searchBox = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && !t.IsReadOnly);
    searchBox.Focus();
    Press(Key.A);
    var typing = vm.SelectedLeftTab;
    Console.WriteLine($"Hotkeys: ⌘T +{added} tab | Q {index}->{left} | E ->{right} | ⌘W -{removed} tab | A ->{dirLeft} | D ->{dirRight} | " +
                      $"⌘A ->{cmdA} | A while typing ->{typing}");
    vm.Settings.Hotkeys.ExportProperties = new Hotkey(Key.P, KeyModifiers.Meta | KeyModifiers.Shift);
    vm.SelectedLeftTab = 2;
    Dispatcher.UIThread.RunJobs();
    packages.SelectedItems!.Clear();
    var g8 = vm.Packages.First(p => p.Name == "T_G8.uasset");
    packages.SelectedItems.Add(g8);
    packages.ScrollIntoView(g8);
    Dispatcher.UIThread.RunJobs();
    (packages.ContainerFromItem(g8) as InputElement)?.Focus();
    var hotkeyJson = Path.Combine(vm.Settings.ExportsDir, File("T_G8.uasset").PathWithoutExtension + ".json");
    if (System.IO.File.Exists(hotkeyJson)) System.IO.File.Delete(hotkeyJson);
    Press(Key.P, RawInputModifiers.Meta | RawInputModifiers.Shift);
    await WaitFor(() => System.IO.File.Exists(hotkeyJson), "the export hotkey");
    Console.WriteLine($"  ⌘⇧P (Export Properties): {System.IO.File.Exists(hotkeyJson)} | {vm.Logs[^1].Message}");

    // JSON theme on the open tab, and the Settings tabs
    await vm.ExtractAsync(File("SM_Fixture.uasset"));
    AppResources.SetJsonTheme(JsonHighlightTheme.Monokai);
    var field = AppResources.Highlighting("Json")!.GetNamedColor("FieldName");
    var escape = AppResources.Highlighting("Json")!.GetNamedColor("Escape");
    Console.WriteLine($"JSON theme Monokai: field name {field.Foreground}, escape {escape.Foreground} bold {escape.FontWeight}");
    await Task.Delay(200);
    Save(window, "17-json-theme.png");
    AppResources.SetJsonTheme(JsonHighlightTheme.Default);

    vm.Settings.Hotkeys.ExportData = new Hotkey(Key.F5);
    var settings = new SettingsWindow(vm.Settings);
    settings.Show();
    var settingsTabs = settings.FindControl<TabControl>("Tabs")!;
    settingsTabs.SelectedIndex = 2;
    await Task.Delay(200);
    Save(settings, "18-settings-preview.png");
    settingsTabs.SelectedIndex = 3;
    await Task.Delay(200);
    var hotkeyBox = settings.GetVisualDescendants().OfType<FModelMac.Controls.HotkeyBox>().First();
    hotkeyBox.Focus();
    settings.KeyPress(Key.Left, RawInputModifiers.Shift);
    Dispatcher.UIThread.RunJobs();
    var captured = hotkeyBox.Hotkey.ToString();
    settings.KeyPress(Key.Back, RawInputModifiers.None);
    Dispatcher.UIThread.RunJobs();
    var cleared = hotkeyBox.Hotkey.IsAssigned;
    settings.KeyPress(Key.A, RawInputModifiers.None);
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine($"Hotkey box: ⇧← -> '{captured}' | Backspace clears: {!cleared} | A -> '{hotkeyBox.Hotkey}' | settings still open: {settings.IsVisible}");
    Save(settings, "19-settings-hotkeys.png");
    settingsTabs.SelectedIndex = 6;
    settings.FindControl<ComboBox>("JsonThemeBox")!.SelectedIndex = (int) JsonHighlightTheme.TokyoNight;
    await Task.Delay(300);
    Save(settings, "20-settings-themes.png");
    settingsTabs.SelectedIndex = 5;
    await Task.Delay(200);
    Save(settings, "21-settings-endpoints.png");
    settings.Close();
    vm.Settings.Hotkeys = new HotkeySettings();
}

// Real pointer/keyboard input on the folder tree: arrow button, item click, Up/Down/Left/Right
async Task InputTestAsync(Window window, MainViewModel vm)
{
    vm.Folders.CollapseAll();
    vm.SelectedLeftTab = 1;
    Dispatcher.UIThread.RunJobs();
    await Task.Delay(100);

    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "FolderList");
    ListBoxItem Item(int i) => (ListBoxItem) list.ContainerFromIndex(i)!;
    Point Center(Visual v, double dx = 0) => v.TranslatePoint(new Point(v.Bounds.Width / 2 + dx, v.Bounds.Height / 2), window)!.Value;
    void Click(Point p) { window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
    void Key(Key k) { window.KeyPress(k, RawInputModifiers.None); window.KeyRelease(k, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }

    var root = vm.Folders.VisibleFolders[0];
    var arrow = Item(0).GetVisualDescendants().OfType<Button>().First();
    Click(Center(arrow));
    Console.WriteLine($"[input] arrow click on '{root.Name}': expanded={root.IsExpanded}, visible={vm.Folders.VisibleFolders.Count}");

    var nameText = Item(0).GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == root.Name);
    Click(Center(nameText));
    Console.WriteLine($"[input] click name: selected='{vm.Folders.SelectedFolder?.Name}', focus={TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement()?.GetType().Name}");

    Key(Avalonia.Input.Key.Down);
    Console.WriteLine($"[input] Down: selected='{vm.Folders.SelectedFolder?.Name}'");
    Key(Avalonia.Input.Key.Up);
    Console.WriteLine($"[input] Up: selected='{vm.Folders.SelectedFolder?.Name}'");
    Key(Avalonia.Input.Key.Left);
    Console.WriteLine($"[input] Left: '{root.Name}' expanded={root.IsExpanded}");
    Key(Avalonia.Input.Key.Right);
    Console.WriteLine($"[input] Right: '{root.Name}' expanded={root.IsExpanded}");
    Key(Avalonia.Input.Key.Right);
    Console.WriteLine($"[input] Right again: selected='{vm.Folders.SelectedFolder?.Name}'");
    Key(Avalonia.Input.Key.Left);
    Console.WriteLine($"[input] Left on child: selected='{vm.Folders.SelectedFolder?.Name}'");
    Key(Avalonia.Input.Key.Left);
    Console.WriteLine($"[input] Left on root: '{root.Name}' expanded={root.IsExpanded}");
    Click(Center(arrow));
    Console.WriteLine($"[input] arrow click again: expanded={root.IsExpanded}");

    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Textures");
}

async Task WaitFor(Func<bool> condition, string what)
{
    for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(50);
    if (!condition()) Console.WriteLine($"Timed out waiting for {what}");
}

void Save(TopLevel top, string name)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    var frame = top.CaptureRenderedFrame();
    var path = Path.Combine(outDir, name);
    frame?.Save(path);
    Console.WriteLine(frame is null ? $"No frame for {name}" : $"Saved {name}");
}

internal sealed class SnapshotApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .With(new FontManagerOptions { DefaultFamilyName = "Helvetica Neue" })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
