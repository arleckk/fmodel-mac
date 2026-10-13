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

// "--profiles <outdir> <paks A> <EGame A> <aes A|-> <paks B> <EGame B> <aes B|->" starts from pre-profile settings for game A,
// creates game B's profile through the Directory Selector, switches back and forth, restarts, and takes screenshots
if (args is ["--profiles", var profOut, var paksA, var gameA, var aesA, var paksB, var gameB, var aesB])
{
    profOut = Path.GetFullPath(profOut);
    Directory.CreateDirectory(profOut);
    var profData = Path.Combine(profOut, "data");
    if (Directory.Exists(profData)) Directory.Delete(profData, true);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", profData);
    // Written the way earlier versions did: no Profiles yet
    File.WriteAllText(Path.Combine(Directory.CreateDirectory(profData).FullName, "settings.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new
    {
        ArchiveDir = paksA, Game = gameA, AesKey = aesA == "-" ? null : aesA, AssetLanguage = "Japanese",
        DynamicKeys = new Dictionary<string, string> { ["0123456789ABCDEF0123456789ABCDEF"] = "0x" + new string('1', 64) },
        OutputDir = Path.Combine(profOut, "Output")
    }));
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), () => ProfilesTestAsync(profOut, paksB, gameB, aesB == "-" ? null : aesB), TimeSpan.FromMinutes(8));
    return;
}

// "--streaming <outdir> <paks> <EGame> <aes|-> <map>" exports a world that streams other levels through the Export
// Session: the Streaming Levels dialog appears (screenshot), keeps the first two levels, then Skip on a second run
if (args is ["--streaming", var strOut, var strPaks, var strGame, var strAes, var strMap])
{
    strOut = Path.GetFullPath(strOut);
    Directory.CreateDirectory(strOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(strOut, "data"));
    new AppSettings
    {
        ArchiveDir = strPaks, Game = strGame, AesKey = strAes == "-" ? null : strAes, OutputDir = Path.Combine(strOut, "Output"),
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP")
    }.Save();
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), () => StreamingTestAsync(strOut, strMap), TimeSpan.FromMinutes(10));
    return;
}

// "--explorer <outdir> <paks> <EGame> <aes|-> <folder>..." opens folders of a real game in the asset explorer: how long
// the tiles on screen take to get their type and thumbnail, a screenshot of each, and the category filter on the first
if (args is ["--explorer", var expOut, var expPaks, var expGame, var expAes, .. var expFolders])
{
    expOut = Path.GetFullPath(expOut);
    Directory.CreateDirectory(expOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(expOut, "data"));
    new AppSettings
    {
        ArchiveDir = expPaks, Game = expGame, AesKey = expAes == "-" ? null : expAes, OutputDir = Path.Combine(expOut, "Output"),
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP")
    }.Save();
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 950 };
        window.Show();
        await WaitFor(() => vm.Archives.Count > 0 && vm.IsReady, "the game");
        var load = System.Diagnostics.Stopwatch.StartNew();
        await vm.LoadCommand.ExecuteAsync(null);
        Console.WriteLine($"Init: {vm.StatusExtra} | loaded in {load.Elapsed.TotalSeconds:F1}s");
        foreach (var folder in expFolders)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (vm.NavigateToFolder(folder) is null)
            {
                Console.WriteLine($"Not found: {folder}");
                continue;
            }
            await Task.Delay(100);
            // What's on screen: the first two rows (the third is cut off at this window size)
            var shown = vm.Explorer.Rows.Take(2).SelectMany(r => r.Entries).OfType<ExplorerFile>().ToList();
            for (var i = 0; i < 600 && shown.Any(f => f.TypeName == f.File.Extension); i++) await Task.Delay(50);
            var typed = watch.Elapsed.TotalSeconds;
            for (var i = 0; i < 200 && shown.Any(f => f.Category == AssetCategory.Texture && !f.HasThumbnail); i++) await Task.Delay(50);
            Console.WriteLine($"{folder}: {vm.Explorer.Status} | {shown.Count} on screen typed in {typed:F2}s, thumbnails " +
                              $"{shown.Count(f => f.HasThumbnail)} in {watch.Elapsed.TotalSeconds:F2}s | " +
                              string.Join(", ", shown.GroupBy(f => f.TypeName).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key}×{g.Count()}")));
            SaveTo(window, expOut, $"explorer-{folder.Replace('/', '_')}.png");
        }
        if (expFolders.Length > 0 && vm.NavigateToFolder(expFolders[0]) is not null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            vm.Explorer.SelectedCategory = AssetCategory.Materials;
            await Task.Delay(100);
            await WaitFor(() => !vm.Explorer.Status.StartsWith("Reading"), "category filter");
            Console.WriteLine($"Category Materials on {expFolders[0]}: {vm.Explorer.Status} in {watch.Elapsed.TotalSeconds:F2}s");
        }
        window.Close();
    }, TimeSpan.FromMinutes(10));
    return;
}

// "--export-folder <outdir> <paks> <EGame> <aes|-> <kind[@seconds]> <folder>..." runs Export Folder on a real game, at
// once (no queue); "@seconds" presses Esc after that long. Prints the time, the files written and the console
if (args is ["--export-folder", var efOut, var efPaks, var efGame, var efAes, var efKind, .. var efFolders])
{
    efOut = Path.GetFullPath(efOut);
    Directory.CreateDirectory(efOut);
    Environment.SetEnvironmentVariable("FMODELMAC_DATA_DIR", Path.Combine(efOut, "data"));
    new AppSettings
    {
        ArchiveDir = efPaks, Game = efGame, AesKey = efAes == "-" ? null : efAes, OutputDir = Path.Combine(efOut, "Output"),
        UsmapPath = Environment.GetEnvironmentVariable("FMODELMAC_USMAP"), ExportImmediately = true
    }.Save();
    RunOnUiThread(SnapshotApp.BuildAvaloniaApp(), async () =>
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 950 };
        window.Show();
        await WaitFor(() => vm.Archives.Count > 0 && vm.IsReady, "the game");
        await vm.LoadCommand.ExecuteAsync(null);
        var parts = efKind.Split('@');
        var kind = Enum.Parse<FolderExport>(parts[0]);
        foreach (var folder in efFolders)
        {
            if (vm.NavigateToFolder(folder) is not { } node)
            {
                Console.WriteLine($"Not found: {folder}");
                continue;
            }
            var before = vm.Logs.Count;
            var exportsDir = vm.Settings.ExportsDir;
            var existing = Directory.Exists(exportsDir) ? Directory.GetFiles(exportsDir, "*", SearchOption.AllDirectories).Length : 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task = vm.ExportFolderAsync(node, kind);
            if (parts.Length > 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
                Console.WriteLine($"  Esc after {watch.Elapsed.TotalSeconds:F1}s ({vm.StatusText})");
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            }
            await task;
            var written = (Directory.Exists(exportsDir) ? Directory.GetFiles(exportsDir, "*", SearchOption.AllDirectories).Length : 0) - existing;
            Console.WriteLine($"{kind} {folder}: {MainViewModel.FolderFiles(node).Count():N0} files -> {written:N0} written in {watch.Elapsed.TotalSeconds:F1}s | {vm.StatusText}");
            foreach (var log in vm.Logs.Skip(before)) Console.WriteLine($"    [{log.Kind}] {log.Message}");
        }
        window.Close();
    }, TimeSpan.FromMinutes(30));
    return;
}

// "--unluac <chunk>..." decompiles compiled Lua files from disk with the bundled libunluac (FMODELMAC_LUA_MODE=
// disassemble for the other mode); prints each result's status and its first FMODELMAC_LINES lines (default 15)
if (args is ["--unluac", .. var chunkFiles])
{
    var disassemble = Environment.GetEnvironmentVariable("FMODELMAC_LUA_MODE") == "disassemble";
    var options = LuaOptions.From(new AppSettings { LuaMode = disassemble ? LuaMode.Disassemble : LuaMode.Decompile });
    Console.WriteLine($"unluac: {(LuaDecompiler.IsAvailable ? "loaded" : LuaDecompiler.LoadError)} | flags {options.Flags}");
    var lines = int.TryParse(Environment.GetEnvironmentVariable("FMODELMAC_LINES"), out var n) ? n : 15;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var all = chunkFiles.AsParallel().AsOrdered().Select(f => (Path: f, Result: LuaDecompiler.Decompile(File.ReadAllBytes(f), options))).ToArray();
    Console.WriteLine($"{all.Length} chunk(s) in {sw.Elapsed.TotalSeconds:F2}s: {all.Count(r => r.Result.Text is not null && !r.Result.Partial)} ok, " +
                      $"{all.Count(r => r.Result.Partial)} partial, {all.Count(r => r.Result.Text is null)} failed");
    // The extraction path too: the first file as a game file, with Decompile Lua on and off
    var gameFile = new CUE4Parse.FileProvider.Objects.StreamedGameFile("Game/Content/Lua/" + Path.GetFileName(chunkFiles[0]),
        new MemoryStream(File.ReadAllBytes(chunkFiles[0])), new CUE4Parse.UE4.Versions.VersionContainer());
    var on = AssetPreview.Build(null!, gameFile, CancellationToken.None, null, options);
    var off = AssetPreview.Build(null!, gameFile, CancellationToken.None, null, null);
    Console.WriteLine($"Preview on: highlight {on.Highlight}, {on.Text.Split('\n').Length} lines, warning '{on.Warning}' | " +
                      $"off: highlight {off.Highlight ?? "none"}, starts '{off.Text.Split('\n')[0]}', warning '{off.Warning}'");
    foreach (var (path, result) in all.Take(lines > 0 ? 3 : 0).Concat(all.Where(r => r.Result.Text is null || r.Result.Partial).Take(5)))
    {
        Console.WriteLine($"{Path.GetFileName(path)}: {(result.Text is null ? "FAILED" : result.Partial ? "partial" : "ok")}{(result.Error is null ? "" : $" ({result.Error})")}");
        foreach (var line in (result.Text ?? "").Split('\n').Take(lines)) Console.WriteLine($"  | {line}");
    }
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

    // "--json ... --streaming-levels" lists the maps that stream other levels (what the Streaming Levels dialog shows)
    if (args[4..] is ["--streaming-levels"])
    {
        static int Count(CUE4Parse_Conversion.Dto.ActorDto actor) => (actor.StreamingLevels?.Count ?? 0) + CountComponent(actor.RootComponent);
        static int CountComponent(CUE4Parse_Conversion.Dto.SceneComponentDto? c) => c is null ? 0 : c.AttachedActors.Sum(Count) + c.Children.Sum(CountComponent);
        var found = new List<(string Path, int World, int Actors)>();
        foreach (var map in jsonProvider.Files.Values.Where(f => f.Extension == "umap").DistinctBy(f => f.Path))
        {
            try
            {
                if (jsonProvider.LoadPackage(map).GetExports().OfType<CUE4Parse.UE4.Objects.Engine.UWorld>().FirstOrDefault() is not { } world) continue;
                using var dto = new CUE4Parse_Conversion.Dto.WorldDto(world);
                var fromActors = dto.Actors.Sum(Count);
                if (dto.StreamingLevels.Count + fromActors > 0) found.Add((map.Path, dto.StreamingLevels.Count, fromActors));
            }
            catch (Exception e) { Console.WriteLine($"  {map.Path}: {e.GetType().Name} {e.Message}"); }
        }
        foreach (var (path, world, actors) in found.OrderByDescending(f => f.World + f.Actors).Take(15))
            Console.WriteLine($"{path}: {world} world level(s), {actors} from actors");
        Console.WriteLine($"{found.Count} map(s) stream other levels");
        return;
    }

    // "--json ... --landscape <map>" describes the landscape of a map: components, layers, materials and their textures
    if (args[4..] is ["--landscape", var landscapeMap])
    {
        var world = jsonProvider.LoadPackage(jsonProvider[landscapeMap]).GetExports().OfType<CUE4Parse.UE4.Objects.Engine.UWorld>().First();
        var components = new List<CUE4Parse.UE4.Assets.Exports.Component.Landscape.ULandscapeComponent>();
        void Collect(CUE4Parse.UE4.Objects.Engine.UWorld w, int depth)
        {
            using var dto = new CUE4Parse_Conversion.Dto.WorldDto(w);
            void Visit(CUE4Parse_Conversion.Dto.SceneComponentDto? c)
            {
                if (c is null) return;
                if (c is CUE4Parse_Conversion.Dto.LandscapeMeshComponentDto l) components.Add(l.Component);
                foreach (var child in c.Children) Visit(child);
                foreach (var a in c.AttachedActors) Visit(a.RootComponent);
            }
            foreach (var actor in dto.Actors) Visit(actor.RootComponent);
            if (depth < 2)
                foreach (var level in dto.StreamingLevels) Collect(level.World, depth + 1);
        }
        Collect(world, 0);
        Console.WriteLine($"{components.Count} landscape component(s)");
        var landscapeContext = new FModelMac.Rendering.ViewerLoadContext(512);
        foreach (var component in components)
        {
            try
            {
                var model = FModelMac.Rendering.ViewerModel.FromLandscape(component, landscapeContext);
                if (components.IndexOf(component) < 3) Console.WriteLine($"  model {component.Name}: {model.VertexCount} verts, bounds {model.Min} – {model.Max}, " +
                                  $"texture {model.Sections.FirstOrDefault()?.Texture?.Name}, color {model.Sections.FirstOrDefault()?.Color}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"  model {component.Name} failed: {e}");
            }
        }
        foreach (var group in landscapeContext.LandscapeLayerColors.GroupBy(l => (Layer: l.Key.Split('|')[1], l.Texture, l.Tiling)).OrderBy(g => g.Key.Layer))
            Console.WriteLine($"  layer {group.Key.Layer}: texture {group.Key.Texture ?? "-"}, repeats every {group.Key.Tiling:0.##} quads, color {group.First().Color} " +
                              $"({group.Count()} material(s), e.g. {group.First().Key.Split('|')[0].Split('.').Last()})");
        foreach (var component in components.Take(2))
        {
            var instances = component.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>("MaterialInstances", []);
            var proxy = component.Outer?.Load<CUE4Parse.UE4.Assets.Exports.Actor.ALandscapeProxy>();
            Console.WriteLine($"{component.GetPathName()}: size {component.ComponentSizeQuads} quads, override {component.OverrideMaterial?.Name}, " +
                              $"instances [{string.Join(", ", instances.Select(i => i.Name))}], proxy material {proxy?.LandscapeMaterial?.Name}");
            Console.WriteLine($"  layers: {string.Join(", ", component.WeightmapLayerAllocations.Select(a => $"{a.GetLayerName()}(tex {a.WeightmapTextureIndex} ch {a.WeightmapTextureChannel})"))}");
            foreach (var ptr in instances.Take(1).Append(proxy?.LandscapeMaterial).OfType<CUE4Parse.UE4.Objects.UObject.FPackageIndex>())
            {
                if (!ptr.TryLoad<CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface>(out var material)) continue;
                var p2 = new CUE4Parse.UE4.Assets.Exports.Material.CMaterialParams2();
                material.GetParams(p2, CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth.TopLayerOnly);
                Console.WriteLine($"  material {material.GetPathName()} ({material.ExportType})");
                foreach (var (name, tex) in p2.Textures.Take(40)) Console.WriteLine($"    tex {name} = {tex}");
                foreach (var (name, value) in p2.Scalars.Take(60)) Console.WriteLine($"    scalar {name} = {value}");
                var parent = material;
                for (var i = 0; i < 4 && parent is CUE4Parse.UE4.Assets.Exports.Material.UMaterialInstance mi && mi.Parent?.Load<CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface>() is { } next; i++)
                {
                    parent = next;
                    Console.WriteLine($"    parent {next.GetPathName()} ({next.ExportType})");
                }
                if (parent is CUE4Parse.UE4.Assets.Exports.Material.UMaterial root)
                    Console.WriteLine($"    referenced textures: {string.Join(", ", root.ReferencedTextures.Select(t => t?.Name).Take(40))}");
            }
        }
        return;
    }

    // "--json ... --material <package>" lists what CMaterialParams2 finds in a material: textures, colors, scalars
    if (args[4..] is ["--material", var materialPath])
    {
        foreach (var material in jsonProvider.LoadPackage(jsonProvider[materialPath]).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface>())
        {
            var p2 = new CUE4Parse.UE4.Assets.Exports.Material.CMaterialParams2();
            material.GetParams(p2, CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth.TopLayerOnly);
            Console.WriteLine($"{material.Name} ({material.ExportType})");
            foreach (var (name, texture) in p2.Textures)
            {
                // Average and spread per channel, to tell how a mask texture is packed
                var stats = "";
                if (texture.TryLoad<CUE4Parse.UE4.Assets.Exports.Texture.UTexture>(out var t) && CUE4Parse_Conversion.Textures.TextureDecoder.Decode(t, 64) is { } decoded)
                {
                    using var bitmap = CUE4Parse_Conversion.Textures.TextureEncoder.ToSkBitmap(decoded);
                    var pixels = bitmap.Pixels;
                    stats = string.Join(" ", new Func<SkiaSharp.SKColor, byte>[] { c => c.Red, c => c.Green, c => c.Blue, c => c.Alpha }.Select((f, i) =>
                    {
                        var v = pixels.Select(c => (double) f(c)).ToArray();
                        var mean = v.Average();
                        return $"{"RGBA"[i]}={mean:0}±{Math.Sqrt(v.Average(x => (x - mean) * (x - mean))):0}";
                    })) + $" {t.Format} sRGB={t.SRGB}";
                }
                Console.WriteLine($"  texture {name} = {texture} {stats}");
            }
            foreach (var (name, color) in p2.Colors) Console.WriteLine($"  color {name} = {color}");
            foreach (var (name, value) in p2.Scalars.Take(30)) Console.WriteLine($"  scalar {name} = {value}");
        }
        return;
    }

    // "--json ... --animations <mesh>" lists the animations found for a skeletal mesh, with their notifies,
    // and decodes the first notify sound
    if (args[4..] is ["--animations", var meshPath])
    {
        var meshExport = AssetPreview.LoadExports(jsonProvider, jsonProvider[meshPath]).First(FModelMac.Rendering.ViewerModel.CanView);
        var meshModel = FModelMac.Rendering.ViewerModel.FromExport(meshExport);
        Console.WriteLine($"{meshModel.Name}: skeleton {meshModel.SkeletonPath}, compatible [{string.Join(", ", meshModel.CompatibleSkeletons)}], " +
                          $"{meshModel.Sockets.Length} socket(s) [{string.Join(", ", meshModel.Sockets.Take(12).Select(x => x.Name))}]");
        var animWatch = System.Diagnostics.Stopwatch.StartNew();
        var entries = await vm.FindAnimationsAsync(meshModel,
            partial => Console.WriteLine($"  partial ({animWatch.Elapsed.TotalSeconds:F1}s): {partial.Count} animation(s)"),
            new Progress<double>(p => { }), CancellationToken.None);
        Console.WriteLine($"  {entries.Count} animation(s) in {animWatch.Elapsed.TotalSeconds:F1}s");
        string? firstSound = null;
        foreach (var entry in entries.Take(int.Parse(Environment.GetEnvironmentVariable("FMODELMAC_ANIMS") ?? "40")))
        {
            try
            {
                var motion = await vm.LoadMotionAsync(entry.File);
                var notifies = motion is FModelMac.Rendering.ViewerAnimation a ? a.Notifies : [];
                Console.WriteLine($"  {entry.Kind,-11} {entry.Name}: {notifies.Length} notify(s)");
                foreach (var n in notifies.Take(8))
                {
                    var attach = n.Socket is { } socket ? meshModel.FindAttachPoint(socket) is { } point ? $" -> bone {meshModel.Bones![point.Bone].Name}" : " -> (not found)" : "";
                    Console.WriteLine($"      [{n.Kind}] {n.Description}{attach} | {n.AssetPath}");
                }
                firstSound ??= notifies.FirstOrDefault(n => n.Kind == FModelMac.Rendering.NotifyKind.Sound)?.AssetPath;
                if (Environment.GetEnvironmentVariable("FMODELMAC_EFFECTS") is not null)
                    foreach (var path in notifies.Where(n => n.Kind == FModelMac.Rendering.NotifyKind.Effect).Select(n => n.AssetPath).OfType<string>().Distinct().Take(6))
                    {
                        var effect = await vm.LoadEffectAsync(path);
                        Console.WriteLine($"      effect {path.Split('.')[^1]}: {(effect is null ? "not read" : $"{effect.Layers.Length} layer(s)")}");
                        foreach (var layer in effect?.Layers ?? [])
                        {
                            var channels = "";
                            if (layer.Texture is { } tx)
                            {
                                var mins = new int[] { 255, 255, 255, 255 }; var maxs = new int[4];
                                for (var i = 0; i < tx.Rgba.Length; i++) { mins[i % 4] = Math.Min(mins[i % 4], tx.Rgba[i]); maxs[i % 4] = Math.Max(maxs[i % 4], tx.Rgba[i]); }
                                channels = $" rgba min {string.Join("/", mins)} max {string.Join("/", maxs)}";
                            }
                            Console.WriteLine($"        {layer.Name}: {(layer.Texture is { } t ? $"{t.Name} {t.Width}x{t.Height}" : "glow")} tint {layer.Tint} flipbook {layer.Columns}x{layer.Rows} size {layer.Size:0}{channels}");
                        }
                    }
            }
            catch (Exception e)
            {
                Console.WriteLine($"  {entry.Kind,-11} {entry.Name}: failed: {e.Message}");
            }
        }
        if (firstSound is not null)
        {
            var pcm = await vm.DecodeNotifySoundAsync(firstSound);
            Console.WriteLine($"  sound {firstSound}: {(pcm is null ? "not decoded" : $"{pcm.Duration.TotalSeconds:0.00}s, {pcm.Channels} ch, {pcm.SampleRate} Hz ({pcm.Encoding})")}");
        }
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

    // "--json ... --lua [path]" decompiles every compiled Lua chunk (timed, with ok/partial/failed counts), in parallel
    // like several tabs would; with a path, prints that file (FMODELMAC_LINES lines, default 40)
    if (args[4..] is ["--lua", .. var luaTargets])
    {
        var options = LuaOptions.From(vm.Settings);
        Console.WriteLine($"unluac: {(LuaDecompiler.IsAvailable ? "loaded" : LuaDecompiler.LoadError)} | flags {options.Flags}");
        var files = luaTargets.Length > 0 ? luaTargets.Select(t => jsonProvider[t]).ToArray()
            : jsonProvider.Files.Values.Where(f => f.Extension is "luac" or "lua").DistinctBy(f => f.Path).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = files.AsParallel().Select(f =>
        {
            var data = f.Read();
            return (File: f, Chunk: LuaDecompiler.IsChunk(data), Result: LuaDecompiler.IsChunk(data) ? LuaDecompiler.Decompile(data, options) : null);
        }).ToArray();
        var chunks = results.Where(r => r.Chunk).ToArray();
        Console.WriteLine($"{files.Length} Lua files ({chunks.Length} compiled) in {sw.Elapsed.TotalSeconds:F2}s: " +
                          $"{chunks.Count(r => r.Result!.Text is not null && !r.Result.Partial)} ok, {chunks.Count(r => r.Result!.Partial)} partial, " +
                          $"{chunks.Count(r => r.Result!.Text is null)} failed");
        foreach (var other in results.Where(r => !r.Chunk).Take(3))
        {
            var head = other.File.Read().Take(48).ToArray();
            Console.WriteLine($"  not a chunk: {other.File.Path} {Convert.ToHexString(head)} '{new string(head.Select(b => b is >= 32 and < 127 ? (char) b : '.').ToArray())}'");
        }
        foreach (var failed in chunks.Where(r => r.Result!.Text is null || r.Result.Partial).Take(5))
            Console.WriteLine($"  {failed.File.Path}: {failed.Result!.Error}");
        if (luaTargets.Length > 0)
            foreach (var line in (results[0].Result?.Text ?? "").Split('\n').Take(int.TryParse(Environment.GetEnvironmentVariable("FMODELMAC_LINES"), out var n) ? n : 40))
                Console.WriteLine($"  | {line}");
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

    var viewer = new ModelViewerWindow { Width = 1280, Height = 800, AnimationSource = vm };
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

        // "anims" waits for the ANIMATIONS list of the selected mesh; "anim:<name>" plays one of them (paused);
        // "seek:<seconds>" jumps there and captures (effect markers) ; "play:<seconds>" plays in real time and counts sounds
        if (package == "anims")
        {
            var listed = new TaskCompletionSource();
            viewer.AnimationsListed += () => listed.TrySetResult();
            var listWatch = System.Diagnostics.Stopwatch.StartNew();
            if (viewer.AnimationEntries.Count == 0) await Task.WhenAny(listed.Task, Task.Delay(TimeSpan.FromMinutes(3)));
            var list = viewer.AnimationEntries;
            Console.WriteLine($"animations: {list.Count} in {listWatch.Elapsed.TotalSeconds:F1}s ({string.Join(", ", list.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}"))}): " +
                              string.Join(", ", list.Take(8).Select(e => e.Name)));
                        continue;
        }
        if (package.StartsWith("anim:"))
        {
            var entry = viewer.AnimationEntries.FirstOrDefault(e => e.Name.Equals(package[5..], StringComparison.OrdinalIgnoreCase));
            if (entry is null) { Console.WriteLine($"no animation {package[5..]} in the list"); continue; }
            var error = await viewer.PlayEntryAsync(entry);
            viewer.Pause();
            var notifies = viewer.Motion is ViewerAnimation va ? va.Notifies : [];
            Console.WriteLine($"{entry.Kind} {entry.Name}: {error ?? "playing"} · {notifies.Length} notify(s)");
            foreach (var n in notifies) Console.WriteLine($"  [{n.Kind}] {n.Description}");
            // Wait for the notifies' effects to be read, so the captures show them
            foreach (var effectPath in notifies.Where(n => n.Kind == NotifyKind.Effect).Select(n => n.AssetPath).OfType<string>().Distinct())
            {
                var effect = await viewer.AnimationSource!.LoadEffectAsync(effectPath);
                Console.WriteLine($"  effect {effectPath.Split('/')[^1]}: {(effect is null ? "unreadable" : string.Join(", ", effect.Layers.Select(l => $"{l.Name}{(l.Texture is null ? " (glow)" : $" {l.Columns}x{l.Rows}")}")))}");
            }
            continue;
        }
        if (package.StartsWith("seek:"))
        {
            var seconds = float.Parse(package[5..], System.Globalization.CultureInfo.InvariantCulture);
            viewer.Seek(seconds);
            await Capture($"seek-{seconds:0.00}");
            await Task.Delay(150);
            Console.WriteLine($"t={viewer.AnimationTime:0.00}s {viewer.EffectLayersShown} effect layer(s), markers: {string.Join("; ", viewer.MarkerLabelPositions.Select(m => $"{m.Label} @ {(m.Position is { } p ? $"{p.X:0},{p.Y:0}" : "off-screen")}"))}");
            continue;
        }
        // "video[:<name>]" saves one cycle of the playing animation as <out>/<name>.mp4 (default "video")
        if (package == "video" || package.StartsWith("video:"))
        {
            var videoPath = Path.Combine(outputDir, $"{(package.Length > 6 ? package[6..] : "video")}{(package.EndsWith(".mov") ? "" : ".mp4")}");
            var videoWatch = System.Diagnostics.Stopwatch.StartNew();
            var videoError = await viewer.RecordVideoAsync(videoPath);
            Console.WriteLine(videoError is null
                ? $"video {Path.GetFileName(videoPath)}: {new FileInfo(videoPath).Length / 1024} KB in {videoWatch.Elapsed.TotalSeconds:F1}s"
                : $"video failed: {videoError}");
            continue;
        }
        if (package.StartsWith("play:"))
        {
            var seconds = double.Parse(package[5..], System.Globalization.CultureInfo.InvariantCulture);
            var before = viewer.NotifySoundsPlayed;
            viewer.Seek(0);
            viewer.Resume();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            viewer.Pause();
            Console.WriteLine($"played {seconds:0.#}s -> t={viewer.AnimationTime:0.00}s, {viewer.NotifySoundsPlayed - before} notify sound(s) played");
            continue;
        }

        // "close" presses Esc in the viewer, which should close it
        if (package == "close")
        {
            viewer.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, Source = viewer });
            await Task.Delay(200);
            Console.WriteLine($"Esc: viewer {(viewer.IsVisible ? "still open" : "closed")}");
            continue;
        }

        // "hittest" prints which control gets a click in the middle of the 3D view
        if (package == "hittest")
        {
            var vp = viewer.ModelViewport;
            var center = vp.TranslatePoint(new Avalonia.Point(vp.Bounds.Width / 2, vp.Bounds.Height / 2), viewer) ?? default;
            var hit = viewer.InputHitTest(center);
            Console.WriteLine($"hit test at {center}: {hit?.GetType().Name ?? "nothing"} (viewport {vp.Bounds.Size}, focused {vp.IsFocused})");
            continue;
        }

        // "mode:orbit|fly" switches the camera; "key:<Key>[+Shift]=<seconds>" holds a key through the window's key
        // routing and prints where the camera went; "drag:<dx>,<dy>" turns the camera like a left-drag
        if (package.StartsWith("mode:"))
        {
            viewer.FindControl<ComboBox>("CameraModeBox")!.SelectedIndex = package.EndsWith("fly") ? 1 : 0;
            Console.WriteLine($"camera mode {viewer.ModelViewport.CameraMode}, fly speed {viewer.ModelViewport.FlySpeed:0.##} m/s");
            continue;
        }
        if (package.StartsWith("key:"))
        {
            var spec = package[4..].Split('=');
            var keyName = spec[0].Split('+');
            var key = Enum.Parse<Avalonia.Input.Key>(keyName[0]);
            var mods = keyName.Length > 1 ? Enum.Parse<Avalonia.Input.KeyModifiers>(keyName[1]) : Avalonia.Input.KeyModifiers.None;
            var before = viewer.ModelViewport.CameraPose;
            viewer.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = viewer });
            await Task.Delay(TimeSpan.FromSeconds(double.Parse(spec[1], System.Globalization.CultureInfo.InvariantCulture)));
            viewer.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = key, Source = viewer });
            await Task.Delay(100);
            var after = viewer.ModelViewport.CameraPose;
            Console.WriteLine($"key {package[4..]}: eye moved {after.Eye - before.Eye} ({(after.Eye - before.Eye).Length():0.#}), " +
                              $"target moved {(after.Target - before.Target).Length():0.#}, lens {viewer.ModelViewport.FieldOfViewDegrees:0.#}°");
            await Capture($"key-{spec[0]}");
            continue;
        }
        // "slider:<name>=<multiplier>" sets a camera speed slider (SpeedX, SpeedY, SpeedZ, Rotation)
        if (package.StartsWith("slider:"))
        {
            var (sliderName, multiplier) = (package[7..].Split('=')[0], double.Parse(package.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture));
            viewer.FindControl<Slider>($"{sliderName}Slider")!.Value = Math.Log10(multiplier);
            var vp = viewer.ModelViewport;
            Console.WriteLine($"speeds X {vp.SpeedX} Y {vp.SpeedY} Z {vp.SpeedZ} rotation {vp.RotationSpeed}");
            continue;
        }
        if (package.StartsWith("drag:"))
        {
            var d = package[5..].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var before = viewer.ModelViewport.CameraPose;
            viewer.ModelViewport.Rotate(d[0], d[1]);
            var after = viewer.ModelViewport.CameraPose;
            Console.WriteLine($"drag {d[0]},{d[1]} ({viewer.ModelViewport.CameraMode}): eye moved {(after.Eye - before.Eye).Length():0.#}, target moved {(after.Target - before.Target).Length():0.#}");
            await Capture($"drag-{viewer.ModelViewport.CameraMode}");
            continue;
        }

        // "normalmaps:on|off" switches normal maps and masks, then captures
        if (package.StartsWith("normalmaps:"))
        {
            viewer.ModelViewport.ShowNormalMaps = package.EndsWith("on");
            await Capture($"normalmaps-{package[11..]}");
            continue;
        }

        // "swap:<material>" first turns on the overwrite button of the first section in the scene
        var swap = package.StartsWith("swap:");
        var packagePath = swap ? package[5..] : package;
        var file = provider.Files.Values.FirstOrDefault(f => f.Path.Equals(packagePath, StringComparison.OrdinalIgnoreCase));
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

            // Which packed masks the materials got, and how they're read
            foreach (var group in map.Objects.SelectMany(o => o.Model.Sections).Where(x => x.MaskTexture is not null)
                         .GroupBy(x => (x.MaskTexture!.Name, x.MaskLayout)).OrderByDescending(g => g.Count()).Take(int.Parse(Environment.GetEnvironmentVariable("FMODELMAC_MASKS") ?? "0")))
                Console.WriteLine($"    mask {group.Key.Name}: {group.Key.MaskLayout} × {group.Count()} ({string.Join(", ", group.Select(x => x.MaterialName).Distinct().Take(3))})");

            // Spline meshes, and the landscape close up (maps' bounds include far-away backdrops)
            var splines = map.Objects.Where(o => o.Model.Kind == "Spline Mesh").ToArray();
            (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) BoxOf(IEnumerable<SceneObject> objects)
            {
                var corners = objects.SelectMany(o => o.Instances.SelectMany(m => new[]
                {
                    System.Numerics.Vector3.Transform(o.Model.Min, m), System.Numerics.Vector3.Transform(o.Model.Max, m)
                })).ToArray();
                return (corners.Aggregate(System.Numerics.Vector3.Min), corners.Aggregate(System.Numerics.Vector3.Max));
            }
            if (splines.Length > 0)
            {
                Console.WriteLine($"    spline meshes: {splines.Length} ({string.Join(", ", splines.Select(o => o.Model.Name).Distinct().Take(6))})");
                var longest = splines.OrderByDescending(o => (o.Model.Max - o.Model.Min).Length()).First();
                var (smin, smax) = BoxOf([longest]);
                Console.WriteLine($"    longest: {longest.Model.Name}, {(longest.Model.Max - longest.Model.Min) / 100} m bent");
                viewer.ModelViewport.FrameBox(smin, smax, 0.5f);
                await Capture($"map-{map.Name}-spline");
            }
            var terrain = map.Objects.Where(o => o.Model.Kind == "Landscape").ToArray();
            if (terrain.Length > 0)
            {
                var (tmin, tmax) = BoxOf(terrain);
                Console.WriteLine($"    landscape: {terrain.Length} component(s), {(tmax - tmin) / 100} m");
                viewer.ModelViewport.FrameBox(tmin, tmax, 0.6f);
                await Capture($"map-{map.Name}-landscape");
                // Close-up of the component with the most textured layers, nearest the middle
                var center = (tmin + tmax) / 2;
                var close = terrain.OrderByDescending(o => o.Model.Sections.FirstOrDefault()?.Landscape?.Textures.Count(t => t is not null) ?? 0)
                    .ThenBy(o => (BoxOf([o]).Item1 + BoxOf([o]).Item2 - 2 * center).Length()).First();
                var (cmin, cmax) = BoxOf([close]);
                Console.WriteLine($"    close-up: {close.Model.Name}, {close.Model.Sections[0].Details}");
                viewer.ModelViewport.FrameBox(cmin, cmax, 0.45f);
                await Capture($"map-{map.Name}-landscape-close");
                viewer.ModelViewport.ShowTextures = false;
                await Capture($"map-{map.Name}-landscape-close-untextured");
                viewer.ModelViewport.ShowTextures = true;
            }
            continue;
        }

        // Materials: on a cube, or into the section waiting for one
        if (!exports.Any(ViewerModel.CanView) && exports.OfType<UMaterialInterface>().FirstOrDefault() is { } material)
        {
            var context = new ViewerLoadContext(1024);
            var resolved = ViewerModel.ResolveMaterial(material, context);
            var cube = ViewerModel.FromMaterial(material, context);
            ViewerSection? target = null;
            if (swap)
            {
                target = viewer.Scene.Items.SelectMany(i => i.Objects).SelectMany(o => o.Model.Sections).FirstOrDefault();
                if (target is not null) target.IsAwaitingMaterial = true;
            }
            var before = target?.MaterialName;
            viewer.ShowMaterial(resolved, cube);
            Console.WriteLine($"{material.Name} ({material.ExportType}): texture {resolved.Texture?.Name ?? "none"} " +
                              $"{resolved.Texture?.Width}×{resolved.Texture?.Height}, color {resolved.Color}" +
                              (target is null
                                  ? $" | on a cube; scene items: {string.Join(", ", viewer.Scene.Items.Select(i => $"{i.Name} ({i.Kind})"))}"
                                  : $" | swapped section '{before}' -> '{target.MaterialName}', waiting {target.IsAwaitingMaterial}"));
            await Capture($"material-{(swap ? "swap-" : "")}{material.Name}");
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
        Console.WriteLine($"    tangent frame: {TangentFrameCheck(model)}");
        foreach (var section in model.Sections.DistinctBy(x => x.MaterialName).Take(6))
            Console.WriteLine($"    material {section.MaterialName}: {section.Details}");
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
    await ExplorerTestAsync(window, vm);
    await FolderExportTestAsync(window, vm);

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
    await ImagesAndSearchTestAsync(window, vm);

    window.Close();
}

// Every image of a package (texture array layers, cubemap panorama) with the pager, and ⌘F / ⌘G in the text
async Task ImagesAndSearchTestAsync(MainWindow window, MainViewModel vm)
{
    void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers);
        window.KeyRelease(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
    (int W, int H) Size(byte[]? png) => png is null ? (0, 0) : SkiaSharp.SKBitmap.DecodeBounds(png) is var info ? (info.Width, info.Height) : (0, 0);

    await vm.ExtractAsync(vm.AllFiles().First(f => f.Name == "T_Array.uasset"));
    var tab = vm.SelectedTab!;
    var names = string.Join(", ", tab.Images.Select(i => i.Name));
    var first = tab.ImagePage;
    tab.StepImage(1);
    var afterNext = $"{tab.ImagePage} {tab.ImageName}";
    window.FindControl<Panel>("ImagePanel")!.Focus();
    Press(Key.Left);
    Press(Key.Left);
    var wrapped = $"{tab.ImagePage} {tab.ImageName}";
    Console.WriteLine($"Images: T_Array -> {tab.Images.Count} ({names}) | {first} | next: {afterNext} | ← ← wraps: {wrapped} | " +
                      $"pager shown {tab.HasMultipleImages}");
    await Task.Delay(200);
    Save(window, "23-images.png");

    foreach (var name in new[] { "T_Cube.uasset", "T_CubeArray.uasset", "T_Volume.uasset", "T_Mips.uasset" })
    {
        if (vm.AllFiles().FirstOrDefault(f => f.Name == name) is not { } file) continue;
        await vm.ExtractAsync(file);
        var (w, h) = Size(vm.SelectedTab!.ImagePng);
        Console.WriteLine($"  {name}: {vm.SelectedTab.Images.Count} image(s), first {w}×{h}{(vm.Logs[^1].Kind == LogKind.Warning ? $" | {vm.Logs[^1].Message}" : "")}");
    }

    // ⌘F from the packages list opens the editor's search; typing, ⌘G and ⇧⌘G move through the matches
    await vm.ExtractAsync(vm.AllFiles().First(f => f.Name == "SM_Fixture.uasset"));
    var editor = window.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
    window.FindControl<ListBox>("PackagesList")!.Focus();
    Press(Key.F, RawInputModifiers.Meta);
    var panel = editor.SearchPanel;
    var opened = !panel.IsClosed;
    panel.SearchPattern = "StaticMesh";
    Dispatcher.UIThread.RunJobs();
    Press(Key.G, RawInputModifiers.Meta);
    var firstHit = editor.CaretOffset;
    Press(Key.G, RawInputModifiers.Meta);
    var secondHit = editor.CaretOffset;
    Press(Key.G, RawInputModifiers.Meta | RawInputModifiers.Shift);
    var back = editor.CaretOffset;
    Press(Key.F3);
    var f3 = editor.CaretOffset;
    var line = editor.Document.GetLineByOffset(Math.Max(0, secondHit - 1));
    Console.WriteLine($"Text search: ⌘F opened {opened} | 'StaticMesh' ⌘G at {firstHit}, again {secondHit}, ⇧⌘G back {back}, F3 {f3} | " +
                      $"selected '{editor.SelectedText}' on line {line.LineNumber}");
    await Task.Delay(200);
    Save(window, "24-text-search.png");
    panel.Close();
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
    settingsTabs.SelectedIndex = 4;
    await Task.Delay(200);
    Console.WriteLine($"Lua: unluac {(LuaDecompiler.IsAvailable ? "loaded" : LuaDecompiler.LoadError)} | {settings.FindControl<TextBlock>("LuaStatusText")!.Text}");
    Save(settings, "22-settings-lua.png");
    settingsTabs.SelectedIndex = 7;
    settings.FindControl<ComboBox>("JsonThemeBox")!.SelectedIndex = (int) JsonHighlightTheme.TokyoNight;
    await Task.Delay(300);
    Save(settings, "20-settings-themes.png");
    settingsTabs.SelectedIndex = 6;
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

async Task ProfilesTestAsync(string outputDir, string paksB, string gameB, string? aesB)
{
    string Describe(AppSettings s) => $"profile '{s.CurrentProfile}', {s.Game}, AES {(s.AesKey is null ? "none" : s.AesKey[..10] + "…")}, " +
                                      $"{s.DynamicKeys.Count} dynamic, usmap {(s.UsmapPath is null ? "none" : Path.GetFileName(s.UsmapPath))}, language {s.AssetLanguage}, dir …/{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(s.ArchiveDir)))}";
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1600, Height = 950 };
    window.Show();
    await WaitFor(() => vm.Archives.Count > 0 && vm.IsReady, "game A");
    var menu = window.FindControl<MenuItem>("ProfilesMenu")!;
    string MenuText() => string.Join(" | ", menu.ItemsSource!.Cast<object>().Select(i => i is MenuItem m ? $"{m.Header}{(m.IsChecked ? " ✓" : "")}" : "—"));
    Console.WriteLine($"Migrated: {vm.Settings.Profiles.Count} profile(s): {Describe(vm.Settings)}");
    Console.WriteLine($"  mounted {vm.StatusExtra} | title '{vm.Title}' | menu: {MenuText()}");

    // Directory › Profiles › New Profile…: the Directory Selector on "New Profile…", with game B's folder
    var selector = new DirectorySelectorWindow(vm.Settings, newProfile: true);
    selector.Show();
    await Task.Delay(200);
    selector.FindControl<TextBox>("DirectoryBox")!.Text = paksB;
    selector.FindControl<ComboBox>("Versions")!.SelectedItem = gameB;
    SaveTo(selector, outputDir, "profiles-1-new.png");
    var ok = selector.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "OK");
    var closed = false;
    selector.Closed += (_, _) => closed = true;
    ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Console.WriteLine($"New profile: closed {closed} | {Describe(vm.Settings)} | profiles {string.Join(", ", vm.Settings.Profiles.Select(p => p.Name))}");
    vm.SaveSettings();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await vm.InitializeGameAsync();
    Console.WriteLine($"  mounted {vm.StatusExtra} in {watch.Elapsed.TotalSeconds:F1}s | {vm.Archives.Count(a => !a.IsEnabled)} locked | title '{vm.Title}'");
    foreach (var log in vm.Logs.TakeLast(4)) Console.WriteLine($"    [{log.Kind}] {log.Message}");
    if (aesB is not null)
    {
        // What Directory › AES does: the key, then mount again
        vm.Settings.AesKey = aesB;
        // ...and Settings › Endpoints › Mapping File, when given
        if (Environment.GetEnvironmentVariable("FMODELMAC_USMAP_B") is { Length: > 0 } usmapB) vm.Settings.UsmapPath = usmapB;
        vm.SaveSettings();
        watch.Restart();
        await vm.InitializeGameAsync();
        Console.WriteLine($"  with its AES key: mounted {vm.StatusExtra} in {watch.Elapsed.TotalSeconds:F1}s | {vm.Archives.Count(a => !a.IsEnabled)} locked | " +
                          $"files {vm.Archive?.Provider.Files.Count:N0}");
        foreach (var log in vm.Logs.TakeLast(3)) Console.WriteLine($"    [{log.Kind}] {log.Message}");
        if (vm.Archive?.Provider.Files.Values.FirstOrDefault(f => f.Extension == "uasset" && f.Name.StartsWith("DT_")) is { } sample)
        {
            await vm.ExtractAsync(sample);
            Console.WriteLine($"  extract {sample.Name}: {vm.StatusText} | {vm.SelectedTab?.Document.LineCount} lines");
        }
    }

    // Game B's own settings: language, a versioning option (kept per directory), a mapping file
    vm.Settings.AssetLanguage = "Spanish";
    vm.Settings.CurrentVersioning.Options = new Dictionary<string, bool> { ["SkipObjectVersions"] = true };
    vm.Settings.CurrentLuaOpCodeMap = ".op 0 move";
    vm.SaveSettings();
    window.GetType().GetMethod("BuildProfilesMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
    Console.WriteLine($"  menu: {MenuText()}");

    // Switch to A and back
    var profileA = vm.Settings.Profiles.First(p => p.Name != vm.Settings.CurrentProfile);
    var profileB = vm.Settings.ActiveProfile!;
    watch.Restart();
    await vm.SwitchProfileAsync(profileA);
    Console.WriteLine($"Switch to A: {Describe(vm.Settings)} | versioning options {vm.Settings.CurrentVersioning.Options?.Count ?? 0} | " +
                      $"opcode map {(vm.Settings.CurrentLuaOpCodeMap is null ? "none" : "set")} | {vm.StatusExtra} in {watch.Elapsed.TotalSeconds:F1}s | title '{vm.Title}'");
    watch.Restart();
    await vm.SwitchProfileAsync(profileB);
    if (vm.Archive?.Provider.Files.Values.FirstOrDefault(f => f.Extension == "uasset" && f.Name.StartsWith("DT_")) is { } sampleB)
    {
        await vm.ExtractAsync(sampleB);
        Console.WriteLine($"  B extract after switching back: {vm.StatusText} | {vm.SelectedTab?.Document.LineCount} lines");
    }
    Console.WriteLine($"Switch to B: {Describe(vm.Settings)} | versioning options {vm.Settings.CurrentVersioning.Options?.Count ?? 0} | " +
                      $"opcode map {(vm.Settings.CurrentLuaOpCodeMap is null ? "none" : "set")} | {vm.StatusExtra} in {watch.Elapsed.TotalSeconds:F1}s");

    // Edit Profiles…: rename B, and a profile whose folder is gone
    vm.Settings.Profiles.Add(new GameProfile { Name = "Old game", ArchiveDir = "/nonexistent/Paks", Game = "GAME_UE4_27" });
    await vm.SwitchProfileAsync(vm.Settings.FindProfile("Old game")!);
    Console.WriteLine($"Missing folder: still '{vm.Settings.CurrentProfile}' | {vm.Logs[^1].Message}");
    var editor = new DirectorySelectorWindow(vm.Settings);
    editor.Show();
    await Task.Delay(200);
    var profileBox = editor.FindControl<ComboBox>("ProfileBox")!;
    Console.WriteLine($"Editor: {string.Join(" | ", profileBox.ItemsSource!.Cast<string>())} | selected '{profileBox.SelectedItem}'");
    SaveTo(editor, outputDir, "profiles-2-edit.png");
    profileBox.SelectedIndex = vm.Settings.Profiles.IndexOf(vm.Settings.FindProfile("Old game")!);
    editor.FindControl<Button>("DeleteButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    editor.FindControl<TextBox>("NameBox")!.Text = profileB.Name + " (PC)";
    editor.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "OK").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    vm.SaveSettings();
    Console.WriteLine($"After edit: {string.Join(", ", vm.Settings.Profiles.Select(p => p.Name))} | current '{vm.Settings.CurrentProfile}'");

    // A restart keeps everything
    window.Close();
    var again = AppSettings.Load();
    Console.WriteLine($"Restart: {again.Profiles.Count} profiles | {Describe(again)} | A: {again.Profiles.First(p => p.Name == profileA.Name).Summary}, " +
                      $"language {again.Profiles.First(p => p.Name == profileA.Name).AssetLanguage}");
    var title = new MainViewModel();
    Console.WriteLine($"  new window title before mounting: '{title.Title}'");
}

async Task StreamingTestAsync(string outputDir, string mapPath)
{
    var vm = new MainViewModel();
    var window = new MainWindow { DataContext = vm, Width = 1600, Height = 950 };
    window.Show();
    await WaitFor(() => vm.Archives.Count > 0 && vm.IsReady, "the game");
    Console.WriteLine($"Init: {vm.StatusExtra}");
    var map = vm.Archive!.Provider[mapPath];
    var asked = 0;
    var skip = false;
    // What MainWindow does, plus a screenshot and a choice
    vm.ExportSession.ChooseStreamingLevels = async filter =>
    {
        asked++;
        var dialog = new StreamingLevelFilterWindow(filter);
        dialog.Show();
        await Task.Delay(300);
        if (asked == 1) SaveTo(dialog, outputDir, "streaming-levels.png");
        var levels = filter.Levels.ToList();
        Console.WriteLine($"  dialog: {filter.TotalCount} level(s) of {filter.WorldName}, checked by default: " +
                          $"{string.Join(", ", levels.Where(l => l.IsChecked == true).Select(l => l.Name))}");
        if (skip) filter.SkipAll();
        else
        {
            filter.SkipAll();
            foreach (var level in levels.Take(2)) level.IsChecked = true;
            var group = filter.Children.OfType<LevelGroupNode>().First();
            Console.WriteLine($"  group '{group.Name}' after checking 2: {(group.IsChecked is null ? "mixed" : group.IsChecked.ToString())}");
        }
        dialog.Close();
    };

    async Task<string[]> Export()
    {
        await vm.ExportWorldCommand.ExecuteAsync(new List<AssetItem> { new(map) });
        await vm.RunExportSessionAsync();
        return vm.ExportSession.Groups.SelectMany(g => g.Items).Where(i => i.FirstFile?.EndsWith(".usda") == true)
            .Select(i => Path.GetFileNameWithoutExtension(i.FirstFile!)).ToArray();
    }
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var worlds = await Export();
    Console.WriteLine($"Two levels: asked {asked} time(s), {worlds.Length} world file(s): {string.Join(", ", worlds)} | " +
                      $"{vm.ExportSession.SucceededCount} ok, {vm.ExportSession.FailedCount} failed, {watch.Elapsed.TotalSeconds:F1}s (clock {vm.ExportSession.ElapsedText})");
    skip = true;
    asked = 0;
    worlds = await Export();
    Console.WriteLine($"Skip: asked {asked} time(s), {worlds.Length} world file(s): {string.Join(", ", worlds)}");
    window.Close();
}

// FModel's new asset explorer: tiles with thumbnails, list view, category filter, search, selection, opening a file
// (the tabs come back) and ⌘[ (the explorer comes back)
async Task ExplorerTestAsync(MainWindow window, MainViewModel vm)
{
    var explorer = vm.Explorer;
    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Textures");
    await WaitFor(() => explorer.Visible.OfType<ExplorerFile>().All(f => f.Category != AssetCategory.All || f.TypeName != "uasset"), "explorer types");
    await WaitFor(() => explorer.Visible.OfType<ExplorerFile>().Count(f => f.HasThumbnail) >= 8, "thumbnails");
    var files = explorer.Visible.OfType<ExplorerFile>().ToList();
    Console.WriteLine($"Explorer: visible {vm.IsExplorerVisible} | {explorer.Status} | {explorer.Columns} columns, {explorer.Rows.Count} rows | " +
                      $"thumbnails {files.Count(f => f.HasThumbnail)} | types {string.Join(", ", files.GroupBy(f => f.TypeName).Select(g => $"{g.Key}×{g.Count()}"))}");
    Save(window, "explorer-grid.png");

    explorer.IsList = true;
    await Task.Delay(300);
    Console.WriteLine($"  list: {explorer.Rows.Count} rows, setting {vm.Settings.ExplorerViewMode}");
    Save(window, "explorer-list.png");
    explorer.IsGrid = true;

    explorer.SearchText = "streaming";
    Console.WriteLine($"  search 'streaming': {string.Join(", ", explorer.Visible.Select(e => e.Name))}");
    explorer.SearchText = "";

    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures");
    await Task.Delay(200);
    Console.WriteLine($"  Fixtures: {explorer.Status} | first {string.Join(", ", explorer.Visible.Take(4).Select(e => e.Name))}");
    var materials = explorer.Visible.OfType<ExplorerFolder>().First(f => f.Name == "Materials");
    Console.WriteLine($"  'Materials' tile: badge {materials.HasBadge}, {materials.AssetsText}");
    Save(window, "explorer-folders.png");

    // Category filter: every file's category is read, then only materials stay
    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Materials");
    explorer.SelectedCategory = AssetCategory.Materials;
    await WaitFor(() => !explorer.Status.StartsWith("Reading"), "category filter");
    await Task.Delay(200);
    Console.WriteLine($"  Materials folder, category Materials: {string.Join(", ", explorer.Visible.OfType<ExplorerFile>().Select(f => $"{f.Name} ({f.TypeName})"))}");
    explorer.SelectCategory(3); // ⌥3: Texture
    await WaitFor(() => !explorer.Status.StartsWith("Reading"), "category filter");
    Console.WriteLine($"  ⌥3 -> {explorer.SelectedCategory}: {explorer.Visible.Count} | empty text {explorer.IsEmpty}");
    explorer.SelectedCategory = AssetCategory.All;

    // Selection drives the Packages tab's INFORMATION; ⇧ extends
    var first = explorer.Visible.OfType<ExplorerFile>().First();
    explorer.Click(first, Avalonia.Input.KeyModifiers.None);
    explorer.Move(1, 0, extend: true);
    Console.WriteLine($"  selected {string.Join(", ", explorer.SelectedEntries.Select(e => e.Name))} | package {vm.SelectedPackage?.Name}");

    // Opening a file hides the explorer; ⌘[ brings it back; Z switches
    await vm.ExtractAsync(first.File);
    var afterOpen = vm.IsExplorerVisible;
    window.RaiseEvent(new Avalonia.Input.KeyEventArgs
        { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.OemOpenBrackets, KeyModifiers = Avalonia.Input.KeyModifiers.Meta });
    var afterBack = vm.IsExplorerVisible;
    window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Z });
    Console.WriteLine($"  open -> explorer {afterOpen} | ⌘[ -> {afterBack} | Z -> {vm.IsExplorerVisible} | tab '{vm.SelectedTab?.Header}'");

    vm.NavigateToFolder("CUE4ParseFixtures/Content/Fixtures/Textures");
    vm.IsExplorerVisible = false; // the rest of the run looks at the tabs
}

// FModel's Export Folder: a folder and its subfolders, a summary in the console, Esc cancels, and the export hotkeys
// act on the folder when the tree has the focus
async Task FolderExportTestAsync(MainWindow window, MainViewModel vm)
{
    int Count(string folder, string extension) =>
        Directory.Exists(Path.Combine(vm.Settings.ExportsDir, folder))
            ? Directory.GetFiles(Path.Combine(vm.Settings.ExportsDir, folder), "*" + extension, SearchOption.AllDirectories).Length
            : 0;
    string LastLogs(int before) => string.Join(" / ", vm.Logs.Skip(before).Select(l => $"[{l.Kind}] {l.Message}"));
    var root = "CUE4ParseFixtures/Content/Fixtures";

    var before = vm.Logs.Count;
    await vm.ExportFolderAsync(vm.NavigateToFolder(root)!, FolderExport.Properties);
    Console.WriteLine($"Export Folder properties: {Count(root, ".json")} json of {MainViewModel.FolderFiles(vm.SelectedFolder!).Count()} files | {vm.StatusText} | {LastLogs(before)}");

    before = vm.Logs.Count;
    await vm.ExportFolderAsync(vm.NavigateToFolder(root + "/Blueprints")!, FolderExport.Code);
    Console.WriteLine($"  code: {Count(root + "/Blueprints", ".cpp")} cpp | {LastLogs(before)}");

    before = vm.Logs.Count;
    await vm.ExportFolderAsync(vm.NavigateToFolder(root + "/Audio")!, FolderExport.Audio);
    Console.WriteLine($"  audio: {Count(root + "/Audio", ".wav")} wav | {LastLogs(before)}");

    // Esc right after starting
    Directory.Delete(Path.Combine(vm.Settings.ExportsDir, root), true);
    before = vm.Logs.Count;
    var running = vm.ExportFolderAsync(vm.NavigateToFolder(root)!, FolderExport.Properties);
    await Task.Delay(30);
    var canCancel = vm.CanCancel;
    window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
    await running;
    Console.WriteLine($"  Esc: could cancel {canCancel} | {Count(root, ".json")} json | status {vm.StatusText} | {LastLogs(before)}");

    // ⌘⇧P (Export Properties, set by an earlier test) with the focus in the folder tree exports the folder
    vm.Settings.Hotkeys.ExportProperties = new Hotkey(Key.P, KeyModifiers.Meta | KeyModifiers.Shift);
    vm.SelectedLeftTab = 1;
    var animations = vm.NavigateToFolder(root + "/Animations")!;
    await Task.Delay(100);
    window.FindControl<ListBox>("FolderList")!.Focus();
    before = vm.Logs.Count;
    window.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = Key.P, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift });
    await WaitFor(() => vm.IsReady && !vm.CanCancel && vm.Logs.Count > before + 1, "folder hotkey export");
    Console.WriteLine($"  ⌘⇧P on the tree ({animations.Name}): {Count(root + "/Animations", ".json")} json | {LastLogs(before)}");
    vm.Settings.Hotkeys.ExportProperties = Hotkey.None;
    Directory.Delete(Path.Combine(vm.Settings.ExportsDir, root), true);
    vm.NavigateToFolder(root + "/Textures"); // where the rest of the run expects to be
    vm.IsExplorerVisible = false;
}

// Per triangle, whether the vertices' tangent follows +U and the shader's binormal (-cross(N, T)·sign) follows +V,
// the directions a UE (DirectX-style) normal map's X and Y point to
static string TangentFrameCheck(ViewerModel model)
{
    const int f = ViewerModel.FloatsPerVertex;
    var v = model.Vertices;
    System.Numerics.Vector3 P(uint i) => new(v[i * f], v[i * f + 1], v[i * f + 2]);
    int tangentOk = 0, binormalOk = 0, total = 0;
    for (var k = 0; k + 2 < model.Indices.Length; k += 3)
    {
        uint a = model.Indices[k], b = model.Indices[k + 1], c = model.Indices[k + 2];
        var (e1, e2) = (P(b) - P(a), P(c) - P(a));
        float du1 = v[b * f + 6] - v[a * f + 6], dv1 = v[b * f + 7] - v[a * f + 7];
        float du2 = v[c * f + 6] - v[a * f + 6], dv2 = v[c * f + 7] - v[a * f + 7];
        var det = du1 * dv2 - du2 * dv1;
        if (Math.Abs(det) < 1e-10f) continue;
        var dPdu = (e1 * dv2 - e2 * dv1) / det;
        var dPdv = (e2 * du1 - e1 * du2) / det;
        var n = new System.Numerics.Vector3(v[a * f + 3], v[a * f + 4], v[a * f + 5]);
        var t = new System.Numerics.Vector3(v[a * f + 8], v[a * f + 9], v[a * f + 10]);
        if (t.LengthSquared() < 1e-6f || n.LengthSquared() < 1e-6f) continue;
        var binormal = -System.Numerics.Vector3.Cross(n, t) * v[a * f + 11];
        total++;
        if (System.Numerics.Vector3.Dot(t, dPdu) > 0) tangentOk++;
        if (System.Numerics.Vector3.Dot(binormal, dPdv) > 0) binormalOk++;
    }
    return total == 0 ? "no tangents" : $"T along +U {100.0 * tangentOk / total:0}%, B along +V {100.0 * binormalOk / total:0}% of {total:N0} triangles";
}

async Task WaitFor(Func<bool> condition, string what)
{
    for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(50);
    if (!condition()) Console.WriteLine($"Timed out waiting for {what}");
}

void Save(TopLevel top, string name) => SaveTo(top, outDir, name);

void SaveTo(TopLevel top, string directory, string name)
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    var frame = top.CaptureRenderedFrame();
    var path = Path.Combine(directory, name);
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
