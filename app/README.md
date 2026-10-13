# FModel: Avalonia window

First macOS desktop window on top of CUE4Parse: loads a game, browses
folders, shows properties (JSON) and previews textures.

## Running

```bash
export PATH=$HOME/.dotnet:$PATH
cd app/FModel
dotnet run
```

No native Oodle library is needed or bundled: Oodle is decompressed by OodleSharp (managed C#,
MIT), which CUE4Parse uses by default. On a 9-level map it measured about 4% slower than native
(27.7 s vs 26.5 s), with identical results. Optionally, put a native `liboodle-data-shared.dylib`
in the data folder (`~/Library/Application Support/FModel/`) and it will be used instead.

## Packaging as a .app

```bash
app/packaging/build-app.sh
```

Produces `app/dist/FModel.app` (~190 MB): a self-contained publish for `osx-arm64`
(no .NET install required) with ReadyToRun, `.icns` icon, `Info.plist`, the `fmodel` command-line link and
ad-hoc signing (Apple Silicon won't run unsigned code). Requires macOS 14+.

- **Icon:** `packaging/make_icon.py` takes the logo from `FModel.ico` (256 px max.) and composites it
  onto the Big Sur icon template. Its default `mac` look (teal body and a Command-key ⌘ badge) keeps the
  logo but sets this port apart from the official FModel in the Dock. To regenerate it:
  `python3 -I app/packaging/make_icon.py upstream/FModel/FModel.ico <tmp>`, then
  `iconutil -c icns <tmp>/FModel.iconset -o app/packaging/FModel.icns`.
- **unluac (compiled Lua):** `app/packaging/build-unluac.sh` builds FModel's unluac (Java, MIT,
  commit `fe187b4`) as `vendor/unluac-build/libunluac.dylib` with GraalVM Native Image; the first
  time it downloads GraalVM CE 25 (~340 MB, verified against its SHA-256) to `vendor/graalvm`. FModel only
  publishes the library for Windows and Linux. If it's there, `build-app.sh` puts it in the app along with its license.
- **Logs:** `~/Library/Logs/FModel/FModel-<date>.log` (there's no console inside the .app).
- **Ad-hoc signing:** works on this Mac. To share it with other people without the Gatekeeper
  warning you need an Apple Developer ID and notarization.

## Interface (same as FModel)

Replicates FModel's `MainWindow.xaml` with AdonisUI's dark palette (layers `#2A2B34`,
`#32323F`, `#262630`…) and its accents (`#206BD4` ready, `#D49220` loading, `#C22B2B` error).

- **Menu:** Directory (Selector, AES, Archives Info), Packages (Search ⌘⇧F, References ⌘⇧R),
  Views, Settings, Help.
- **Image Merger (Views › Image Merger):** as in FModel, joins several images in rows (Images Per
  Row, which starts at the square root of the number of images, and Margin Between Images, which is
  saved), with Add…, Move Up/Down, Remove, Clear, Open Image and Save Image. Besides files
  (PNG, JPEG, BMP, GIF, WebP and TIFF, the latter converted with macOS's `sips`), it accepts textures
  straight from their preview: right-click › Add to Image Merger.
- **Backup (Directory › Backup) and Loading Mode "All (New)" / "All (Modified)":** as in FModel, a
  backup (`.fbkp`) stores the path, size and encryption of every file in the game; when loading
  with All (New) or All (Modified) you pick an older backup and only see the new files or
  the ones that changed. Same format as FModel (LZ4 frame, including its old unversioned format),
  so its backups work here and vice versa. The window creates the backup of the current game in
  `Output/Backups`, lists yours (Show in Finder, Load New, Load Modified) and downloads FModel's
  (its service, Fortnite). The backup is read as a stream: Fortnite 41.20's (6.7 million
  entries, 43 MB) is compared in ~4 s with ~130 MB of memory.
- **Favorite Directories (tab between Archives and Folders, and Packages › Favorite Directories):**
  folder shortcuts, as in FModel. The tab lists them (those that don't exist in the loaded game are
  dimmed); double-click, Enter or the Go To button open the folder in Folders (loading the
  archives if needed), and its buttons and context menu add (from the selected folder),
  edit, reorder, copy the path or delete (also with Delete). Each **profile** keeps its own
  list (Directory › Profiles): switching profiles switches the favorites; a new profile starts
  with the list for its game folder, and the first time a game is mounted FModel's presets are added
  (Fortnite, VALORANT, Dead by Daylight).
  The Folders tree has a context menu with "Export Folder", "Favorite Directory" and "Copy
  Directory Path"; the dialog warns if the folder doesn't exist in the loaded game.
- **Export Folder** (right-click a folder in the tree or the explorer, or an export shortcut
  with focus on the tree): Raw Data, Properties, Textures, Decompiled Blueprints, Models, Worlds,
  Animations or Audio for every file in the folder and its subfolders, like FModel. Textures,
  models, worlds, animations and raw data go through the Export Session; packages are read in
  parallel and an unreadable one is skipped. With many files the console shows a summary (how many were
  saved, how many had nothing, the first errors) and the status bar shows progress. **Esc**
  cancels (including the running Export Session). DSR: 1,095 Properties in 4.6 s, 104 textures in 4.8 s.
- **Left panel, Archives tab:** Loading Mode (Multiple/All) + Load, GAME ARCHIVES list
  (locked ones are grayed out) and INFORMATION for the selected container.
- **Folders:** flattened, virtualized tree with FModel's folder icons, brown package
  counter, rotating arrow, "Bring Selected Folder To View" and "Collapse All".
- **N Packages:** search, clickable breadcrumb, list with per-type icons and context menu
  (Extract in New Tab, Show Metadata, Find References, Decompile Blueprint, Export › Raw Data/
  Properties/Texture/Model/World/Animation/Audio/Decompiled Blueprints, Copy › paths). Asset tabs have their own menu (Close
  Tab/All/Other, Save Text…, Show Metadata, Find References, Decompile Blueprint).
- **Decompile Blueprint:** the package's classes as pseudo C++ (variables with their default
  values and functions from the Kismet bytecode), in a new tab with C++ highlighting. Uses
  CUE4Parse's decompiler, same as FModel, but always reads the bytecode: in FModel you first have to
  enable "Serialize Script Bytecode" and the option is hidden. Save Text… saves the `.cpp`.
  In FF VII, 800 Blueprints and Widgets were decompiled without failures.
- **Settings › General › Advanced:** FModel's "Serialize script bytecode" and "Serialize inlined shader maps",
  to see that data in the properties JSON. They apply without remounting.
- **Show Metadata:** the package summary, name map, imports and exports as JSON, in a
  new tab (like FModel).
- **Find References / Search For Packages:** the search window has the By Path and
  References tabs, each with a filter, an Archive column and the same context menu. FModel only finds
  references in IoStore games (it reads the container header); here it also works with `.pak`:
  the first time, the headers of all packages are read and an import index is built
  (38,302 FF VII packages in 0.7 s; 103,780 from DSR in ~30 s) and saved to disk, per game and
  per set of mounted archives, so subsequent times it loads in a fraction of a second.
  On mount, the *virtual paths* from the `.uplugin` files are loaded as in FModel, so that imports
  from plugins (`/ACLPlugin/…`) resolve: without them DSR couldn't decode a single animation.
- **3D Viewer** (Views › 3D Viewer, or double-click a mesh): static and skeletal
  meshes (LOD 0) with their diffuse texture, grid, wireframe, skeleton, per-material visibility
  and PNG capture. Shading is PBR (GGX), with sRGB textures, ACES tone mapping, a key
  light that follows the camera from above and to one side, a fill light and sky/ground ambient
  light. It uses each material's **normal map** (mesh tangents, UE's DirectX
  convention) and its **mask** texture for roughness, metallic and occlusion. Since every game packs
  masks differently, the layout comes from the texture or parameter name (`_ORM`, `_RMA`,
  `MSRAO`, `SRXO`, `_Roughness`…, or Fortnite's `SpecularMasks`); if it isn't recognized, the mask
  isn't used. The **Normal Maps** toggle turns both off. If the material has no known diffuse
  parameter, its own color texture (sRGB) whose name indicates it (`BaseTexture`,
  `MainTex`, `*_BaseColor`…) is taken before the `PM_Diffuse` that CUE4Parse guesses, which can be the
  parent material's default texture (in DSR, Flamedramon came out with Apocalymon's).
  **Transparency** follows each material's UE blend mode, read along the instance chain (an instance's
  `BasePropertyOverrides` only count with their `bOverride_` flag; CUE4Parse's `GetParams` would turn a
  masked parent opaque when an instance overrides anything else). **Masked** materials (foliage, fences,
  feathers) are cut out at their `OpacityMaskClipValue`; **translucent**, **additive** and **modulate**
  ones are drawn after the opaque ones, farthest first, without writing depth. The opacity comes from the
  diffuse texture's alpha when it has one, else from a texture named for it (`Opacity`, `Alpha`,
  `Cutout`… but not a bare `Mask`, which games use for color regions); translucent ones also take their
  `Opacity` scalar (0.5 when they have none, e.g. glass). A masked material with nothing to cut by stays
  opaque. Each material's line in MATERIALS says how it blends.
  **Controls** (like Snooper, with its **Orbit / Fly** selector, which is remembered): in Orbit dragging
  rotates around the model and scroll or pinch zoom in; in Fly dragging looks around, scroll
  moves forward and +/- (or scrolling while dragging) change the speed. In both: WASD moves and Q/E
  go down/up while you hold the key (Shift faster, ⌥ slower), right-click, middle-click or
  Shift-drag pans, the **arrow keys** rotate the camera like a drag in that direction, C/X
  zoom the lens in/out, F frames the selection and Esc closes the viewer. Keys work without having to click
  the view first. The **CAMERA** section of the right panel has a speed slider per axis
  (X: A/D, Y: W/S, Z: Q/E) and another for rotation (dragging and arrow keys), from 0.1× to 10×, which are remembered. Each mesh you open is **added to the scene** alongside the
  others; the outliner (SCENE) lets you hide, remove or select each one, and Clear Scene empties it.
  Double-clicking a **material** (instance or base material) shows it on a cube as in FModel
  (the next material replaces the cube's); with a section's material button turned on
  in the MATERIALS list, the next material you open **replaces that section's** (FModel's
  "overwrite material"). Settings › Preview has Preview Materials and **Preview Max
  Texture Size** (4–4096 px, 1024 by default) for the viewer's textures; maps use 512 at
  most (256 for normal maps and masks), and everything is decoded with the game's texture
  platform.
  Double-clicking a **map** (.umap) loads the whole level: actors and attached components,
  instances (ISM/HISM/foliage), streaming levels (with their transform), level instances and
  overridden materials, with GPU instancing (one draw call per mesh). **Spline
  meshes** (roads, tracks, cables) bend along their spline, normals included.
  **Landscapes** are built as continuous terrain and painted with the **actual textures of their layers**:
  each layer takes the diffuse texture of the material whose name matches (Grass_LayerInfo →
  T_Ground_Grass_D), tiled in terrain space like UE's `LandscapeCoords`, every however many
  meters its tiling scalar says (`Cliff_TilingSize`, `Ground_Tiling`…; 8 m if there is none), and each
  component's paint weights blend its four main layers in the shader. Layers without a
  texture (or beyond four) use the average color, with a name-based palette as a last
  resort (FModel's viewer doesn't show landscapes).
  **World Partition** maps (UE5) load all their runtime cells in parallel, both from the
  UE 5.3+ hash set and from the 5.0–5.2 spatial grids: the main content stays in the
  map, each runtime data layer is a separate item (visible if it starts active in the game)
  and each HLOD layer another, hidden by default. Skies and giant backdrops go to a separate item,
  hidden by default.
  When you select a skeletal mesh, the **ANIMATIONS** section lists the animations made
  for its skeleton (or its compatible skeletons) in any game: every animation points to the
  skeleton it was made with, so they're the packages that reference it whose main export is
  a sequence, montage, composite, blend space or aim offset. One click plays it; the filter searches
  by name. In .pak games the reference index is built once (DSR: ~30 s) and saved in
  `References/` in the data directory (afterwards it loads in 0.1 s); meanwhile the list shows what's
  in the folders near the skeleton.
  The animation's **notifies** show up as marks under the timeline (orange: effect,
  green: sound, gray: event; hover to see the details, click to jump there). They're read without
  knowing the game's classes: what each notify references decides its type (a Niagara or
  Cascade system is an effect, a sound asset a sound, a *Socket*/*Bone* property the attachment
  point), so UE's own (`AnimNotify_PlayNiagaraEffect`, `PlayParticleEffect`,
  `TimedParticleEffect`, `PlaySound`) and each game's custom ones (DSR, FF VII) work the same; a sound named
  only by its event is looked up as a package with that name (or `_Cue`). Montages include the
  notifies of the sequences they play. With **Sounds** the sounds play in sync
  (SoundWave, SoundCue, Wwise, etc., through the same extractor as the Audio Player); with **Effects** each
  active effect is drawn at its socket with its name, for as long as it lasts (at least 1.2 s). It's an
  **approximation**: the system isn't simulated (Niagara runs compiled scripts); instead each emitter
  (Niagara sprite, ribbon or mesh renderer; Cascade's `ParticleModuleRequired`) is drawn as
  a camera-facing additive sprite with its material's main texture, flipbooks
  (`SubImageSize` / `SubImages_*`) animated over the effect's lifetime, growth, spin and
  fade; the size comes from the emitter's or the system's fixed bounds. Color textures without
  their own background are combined with the material's opacity texture (or fade out in a circle);
  distortion materials aren't drawn and placeholders (`DefaultTexture`, `T_White`) are a
  soft glow. Particle colors are computed by the Niagara VM, so a grayscale texture
  takes the material's color or, if it has none, one based on the effect's name (fire, ice, thunder…).
  With a skeletal mesh open, double-clicking an animation (sequence, montage or
  composite) also plays it: play/pause, timeline, speed, loop and optional root motion.
  **Blend spaces** (1D and 2D) show a slider per axis and blend their samples using the
  weight grid that UE cooks (`GridSamples`), phase-synchronized. **Morph targets**
  appear with one slider each; if the animation carries curves with the same name, those take over.
  In UE5 PC cooks the morph deltas only exist in the mesh's GPU buffer;
  CUE4Parse's `PopulateMorphTargetVerticesData()` recovers them.
  Bones are matched by name, as in Snooper, and skinning runs on the GPU with the matrices in
  a float texture (macOS has no SSBO).
  **Save Video…** (next to Save Screenshot, visible with an animation loaded) saves one cycle of
  the animation as MP4 or MOV: H.264 at 60 fps, at the viewer's size fitted to 1920×1080, from the
  current camera and at the chosen speed (with **Loop** off it adds the final frame). It's rendered
  frame by frame offscreen, so no frames are dropped even if the scene is heavy,
  with effects and grid/skeleton according to their checkboxes (no selection box or
  markers); with **Sounds** the notifies' sounds are mixed into a stereo AAC track at their
  time. macOS encodes it (AVFoundation `AVAssetWriter`, through the Objective-C runtime), with no
  dependencies. While recording, the button shows progress and can be used to cancel.
- **Audio Player (Views › Audio Player):** like FModel's: output device, volume,
  encoding/duration/bytes per second, playlist with search (Play, Remove, Save, Save
  Playlist), clock, previous/play/next, 75-bar spectrum analyzer and timeline
  with waveform (click or drag to seek). Shortcuts: Space, Cmd+←/→, Delete. Extracting a
  sound wave, Wwise event or media, FMOD event/bank, CRIWARE cue sheet or wave bank,
  Square Enix SEAD bank or a `.wem/.bnk/.pck/.bank/.awb/.acb` adds it and plays it.
  Export › Audio saves every sound in the selection as `.wav` (or in its original format,
  depending on Settings › Audio). Output uses macOS's AudioToolbox (AudioQueue).
  WAV (PCM, float, MS/IMA ADPCM), Ogg Vorbis (NVorbis, like FModel) and CRIWARE HCA/ADX are
  decoded inside the app; Wwise, Bink, UE Opus, AT9 and the rest go through
  [vgmstream](https://vgmstream.org), which ships inside the .app: `packaging/build-vgmstream.sh`
  builds it (r2117) with a minimal FFmpeg 7.1.1 with decoders only (LGPL), and mpg123, vorbis, speex,
  atrac9 and celt statically linked, so it only depends on libSystem (5 MB). Its licenses go in
  `Contents/Resources/Licenses/vgmstream`. The official Mac build depends on exact Homebrew
  versions; if ours is missing, Homebrew's is used (`brew install vgmstream`). RAD Audio (UE 5) needs
  `radadec`, same as in FModel: build it from Unreal Engine and put it in the data folder.
- **Right panel:** per-asset tabs (double-click replaces the current one, "+" adds, middle-click
  closes), properties with FModel's `Json.xshd` and the texture on the right with an adjustable splitter.
- **Asset explorer** (FModel's "Preview New Explorer System", toggle at the top
  right): when you pick a folder, the right panel shows its subfolders and files as
  **tiles** with a thumbnail (textures, images, Fortnite icons) or an icon and color by category,
  main class type and size; or as a **list** (name, type, size). Search box, category filter
  (⌥0–⌥9), selection with click/⌘/⇧ and arrow keys, ⌘A, ⌫ goes up one folder, Enter or double-click
  opens (a file switches to the tabs; a folder is entered, skipping chains of single folders),
  context menu with Extract/Export/Copy like the Packages one, and export shortcuts act
  on the selected tiles. Only the packages on screen are read (4 at a time, the
  most recent first), so large folders open instantly (15 DSR textures with
  thumbnails in 0.27 s); the category filter does read the whole folder. ⌘[ or the mouse "back"
  button go back to the explorer from the tabs; Z (Settings › Hotkeys) toggles, and the floating
  buttons at the bottom right toggle explorer/tabs and thumbnails.
- **Collapsible console** with `[INF]/[WRN]/[ERR]` in FModel's colors, open output folder
  and clear.
- **Status bar** that changes color according to state.
- **Dialogs:** Directory Selector, AES Manager (static key and dynamic keys by GUID), Settings
  (General, Export, Preview, Hotkeys, Lua, Audio, Endpoints and Themes tabs), Search and About.
- **Game profiles (Directory › Profiles), not in FModel:** each game is saved with
  its folder, UE version, AES key and dynamic keys, mapping, endpoints and language; what's already saved
  per folder (versioning, favorites, Lua opcode map, last AES refresh) follows it on its own. The
  menu lists the profiles (the current one checked) to switch with one click: the current game is saved and
  the other one is mounted as it was left. The Directory Selector picks, creates ("New Profile…", starts with no
  keys or mappings), renames and deletes profiles. What you change in AES or Settings is saved to the
  current profile. When upgrading, the configured game becomes the first profile.
- **Decompile Lua (Settings › Lua):** `.luac` files (compiled Lua 5.0–5.5) open as code with
  FModel's unluac, bundled in the app (FModel downloads it and there's no Mac version). Decompile and
  Disassemble modes, Raw string, No debug and LuaJ, and a per-game opcode map for those that
  shuffle the opcodes. If unluac can't, the hex dump is shown and the reason goes to the log.
  Tested with 284 chunks (unluac's tests compiled with `luac` 5.5): all fine. FF VII's `.luac` files
  are encrypted by the game, so there's nothing to decompile there.
- **Find in text (⌘F):** the editor's search panel (AvaloniaEdit), like FModel's Ctrl+F / F3.
  ⌘F opens it from anywhere in the window (starting with the selection), ⌘G / F3 goes to the
  next match and ⇧⌘G / ⇧F3 to the previous one; it has match case, whole word and regex.
- **Multiple images per tab:** as in FModel, all the package's textures, each layer of a
  texture array (`name_0`, `nombre_1`…) and cubemaps unfolded as a panorama. The arrows over the
  image (or ← → after clicking it) step from one to another with "n / total"; Open, Save and Add to Image
  Merger use the one shown. Pixel art is scaled without smoothing and textures are decoded with the
  game's Texture Platform.
- **Streaming Levels:** when exporting a world that loads other levels, the Export Session asks which ones
  to export (FModel's window: tree of actors and levels with checkboxes, Select All/None, Skip and OK);
  the ones you don't check stay referenced in the USD without being exported. The session clock is paused
  while you choose. In DSR, `RealCopyWorld_Subway01` (11 levels) exported 2,304 files without failures.
- **Export › Raw Data and Decompiled Blueprints:** Raw Data goes through the Export Session with
  CUE4Parse's `RawDataExporter` (like FModel) and writes the package files as-is (`.uasset`,
  `.uexp`, `.ubulk`…). Decompiled Blueprints saves Decompile Blueprint's pseudo C++ as `.cpp`.
- **Keep Directory Structure (Settings › General):** on by default, as in FModel. When off,
  properties, `.cpp` files and audio are saved directly in `Exports/` without the package path. The
  Export Session always keeps the structure (CUE4Parse builds it).
- **Settings › Preview:** what the 3D viewer opens on extract, by type (static meshes, skeletal meshes,
  animations, worlds), and "Handle Skeletons as Empty Meshes": a `USkeleton` opens as bare
  bones (animations can be played on it) and Export › Model also takes it; since glTF doesn't
  support a bare skeleton, it's exported as USD. The old "preview meshes" option is migrated automatically.
- **Settings › Hotkeys:** FModel's shortcuts, configurable, with ⌘ instead of Ctrl: switch
  left-panel tab (A/D) and asset (Q/E), add and remove tab (⌘T/⌘W), the eight
  exports (unassigned by default; shown in the Export menu) and the Audio Player ones
  (⌘N adds audio files from disk, K, J, L, X). Modifiers must match exactly,
  so ⌘A doesn't trigger A, and bare keys don't fire while you type in a text field.
- **AES Reload at Launch (Settings › Endpoints):** Always, Never or Once Per Day (default, as in
  FModel; per game folder). If you change the AES endpoint it's queried on OK regardless of
  the option.
- **Settings › Themes:** FModel's 16 JSON Highlight Themes (Default, Nord, Tokyo Night, Monokai,
  Rosé Pine…) with their preview. They're applied on OK to all open tabs; escape sequences
  (`\n`, `\"`) are also colored, which FModel's `.xshd` doesn't distinguish.
- **Export options (Settings › Export):** FModel's: Mesh Format (glTF, USD,
  UEFormat, ActorX), Mesh Quality (LODs), Socket Format, Compression (UEFormat only), Nanite
  Format, morph targets; Texture Format (PNG, JPEG, TGA, WebP), Texture Quality (JPEG/WebP),
  HDR as `.hdr`, all mips; Material Depth and exporting the mesh's materials. They're passed to
  CUE4Parse's `ExportSession`, which now also exports textures (arrays per layer,
  cubemaps as a panorama). If CUE4Parse can't read a mesh's Nanite data with "Only Nanite LOD",
  it's exported again with its fallback mesh and a warning goes to the log.
- **Export Session (Views › Export Session):** as in FModel, Export › Model/Animation/World/
  Texture put the assets in a queue (or they're exported right away with "Export immediately" in
  Settings › Export, off by default just like in FModel). The window shows the queue grouped
  by class, progress, elapsed time and ETA, how many succeeded or failed, and each
  asset's logs (from a Serilog sink that receives what CUE4Parse's exporters report), including
  the materials and textures exported along with a mesh. You can remove assets from the queue,
  cancel, open the file in Finder and, in Export Options, override the options and the folder
  for that session only (or save them as default). Since CUE4Parse uses one set of options per
  run, the queue is exported in groups: worlds (USD only) and, with glTF, animations (USD).
- **Engine versions (Settings › General):** UE Version, Texture Platform and FModel's three
  versioning editors (Custom Versions, Options, MapStructTypes). They're saved per game folder
  and passed to CUE4Parse in the `VersionContainer`. Unlike FModel, the Options and
  MapStructTypes editors start with CUE4Parse's effective values for that version and only save what
  you change. Changing any of these remounts the game on OK.
- **Language (Packages Language):** the language of localized texts (FText). If the game doesn't have
  that exact culture it uses the closest one (`es-MX` → `es`). CUE4Parse only switches to cultures the
  game declares (`CulturesToStage` or `.locmeta`); if it doesn't declare them, the app loads the game's
  `.locres` files on its own. As in CUE4Parse, Engine texts aren't loaded. Changing only the language
  reloads the texts without remounting.
- **Endpoints (Settings › Endpoints):** as in FModel, a URL that returns JSON plus a JSONPath
  expression. The AES one gives the main key and, optionally, the list of dynamic keys
  (`guid`/`key`); the mappings one, the URL of the `.usmap`/`.jmap` and optionally its name. Every time
  the game is mounted the keys are refreshed (according to AES Reload at Launch) and the mapping is downloaded (once; it's cached
  per endpoint in `Mappings/` and reused if the endpoint fails). A local mapping in Settings
  takes priority. The editor (Configure…) has Send/Test, Fortnite and VALORANT presets
  (uedb.dev, the same as FModel), and only lets you save a tested configuration. When mounting
  FortniteGame or ShooterGame with no endpoints, the preset is applied once and the game is remounted.

They're saved in `~/Library/Application Support/FModel/settings.json`. Exports go to
`<Output>/Exports/<package path>`, as in FModel. Models come out as glTF (`.glb`) by
default; animations switch to USD because CUE4Parse doesn't write them in glTF, and worlds are always
USD. Settings and logs from the old `FModelMac` folders (`~/Library/Application Support/FModelMac`,
`~/Library/Logs/FModelMac`) are moved to the `FModel` ones automatically on first launch
(`AppSettings.MovedFromOldName`).

## Command line and MCP server

`FModel` with a command doesn't open the window: it mounts the game, does the job and exits
(`Program.Main` → `Headless/Cli.cs`). The bundle has an `fmodel` link to the executable in
`Contents/Resources/bin` (the volume is case-insensitive, so it can't sit next to `FModel` in `MacOS`);
putting that folder on the PATH gives `fmodel` without `sudo`. Any argument other than the `-psn_`/`-NS…` ones
macOS and Xcode pass is taken as a command, so a typo prints an error instead of opening the app.

```bash
fmodel profiles                                       # saved profiles, * is the active one
fmodel info                                           # mount the active profile and summarize it
fmodel search -p "Digimon SuperRumble" Flamedramon    # a profile by name
fmodel cat /Game/Characters/Hero/Hero --export Hero   # properties JSON (one export)
fmodel export /Game/Characters/Hero --kind models     # into Output/Exports, with Settings › Export
fmodel ls -d <Paks folder> -g GAME_UE4_25 -k 0x… Game/Content   # a game without a profile
```

- **Same settings, read only:** `HeadlessGame` loads `settings.json`, applies the profile (or `--dir`, `--game`,
  `--aes`, `--mappings`) in memory and never saves, so the app stays the only one that changes the settings and
  nothing clashes when both run. AES endpoints are only asked with `--refresh-aes` (the app refreshes them per
  "AES Reload at Launch"); mapping endpoints and their cache work as in the app. The reference index and the
  downloaded mappings are shared with the app.
- **Output:** results on stdout (`--json` for scripts), messages on stderr, logs in
  `~/Library/Logs/FModel/FModel-cli-<date>.log` (`-v` also shows warnings on stderr). Exit codes: 0, 1 (error),
  2 (usage), 130 (Ctrl+C).
- **Exports** (`Headless/HeadlessExport.cs`): properties and decompiled Blueprints are written directly,
  sounds go through the audio player's extractor, and models, animations, worlds, textures and raw data through
  the Export Session (`ExportSessionViewModel`, which already ran without a UI thread for the harness), so
  every export setting, the Nanite fallback and the per-asset errors are the app's.
- **MCP server** (`fmodel mcp`, `Headless/FModelMcpServer.cs`): stdio, with the official C# SDK
  (`ModelContextProtocol.Core`, without the hosting packages). The default game (the active profile, or the
  options given to `mcp`) starts mounting when the server starts and stays mounted; `load_game` switches. Tools:
  `list_profiles`, `load_game`, `game_info`, `list_folder`, `search_files`, `read_file`, `get_package_metadata`,
  `decompile_blueprint`, `find_references`, `get_texture` and `export_assets` (the only one that isn't
  read-only). Long texts are paged by characters (`offset`, `max_chars`, 40,000 by default) and the last texts
  read are kept, so the next page doesn't serialize the package again; lists are paged by entries. Textures are
  scaled down to `max_size` (1024 px by default) before they're sent. User errors (a wrong path, an unknown
  profile) reach the model with their message. stdout is the protocol, so `Console.Out` is pointed at stderr
  once the transport holds the real stream.

```bash
claude mcp add fmodel -- /Applications/FModel.app/Contents/MacOS/FModel mcp
```

## Decisions

- **Avalonia 11.3, not 12:** Avalonia 12 uses SkiaSharp 3.x and CUE4Parse-Conversion pins
  SkiaSharp `[2.88.9]`. FModel has the same lock.
- **ComboBox for the version:** `AutoCompleteBox` rewrote the value while filtering.
- **Dependency advisories:** CUE4Parse pulls `Microsoft.Bcl.Memory 9.0.0` (GHSA-73j8-2gch-69rq) through
  Fmod5Sharp → IndexRange; the patch pins `10.0.12` with central transitive pinning, and `FModel.csproj`
  pins it too (it isn't centrally managed). `SixLabors.ImageSharp` stays at CUE4Parse's `[3.1.12]`: its
  five advisories are fixed only in 4.1.2+, and ImageSharp 4 refuses to build in Release without a Six
  Labors license key, which every person building this repository would need. They're in code the app
  never runs (TIFF CCITT encoding, HistogramEqualization, ICC and BigTIFF decoding): CUE4Parse only
  creates a 16-bit landscape heightmap and encodes it as PNG. `app/Directory.Build.props` and the patch
  list them as reviewed (`NuGetAuditSuppress`). The heightmap encoder now asks for 16-bit grayscale
  explicitly (`LandscapeMeshExporter.HeightmapEncoder`), since ImageSharp 4's default would write 8-bit
  RGBA, should it be upgraded later.
- **FModel resources:** the PNGs in `Assets/Icons`, the `.xshd` files in `Assets/Highlighting` and
  `Themes/Icons.axaml` (generated from `Icons.xaml`) come from FModel, which is GPL-3.0; if
  this app is distributed, it must be GPL-3.0 too.
- **Own 3D viewer, not Snooper:** Snooper (OpenTK/GLFW + ImGui) requires OpenGL 4.6 with SSBO and macOS
  stops at 4.1. The viewer is an Avalonia `OpenGlControlBase` with GLSL 1.50; it uses the same axes
  as CUE4Parse's glTF exporter (Y↔Z), so it looks the same as in Blender.
- **Helvetica Neue font:** Inter turns "0x4" into "0×4" (contextual ligature) and Avalonia doesn't
  accept the system font `.AppleSystemUIFont` (hidden family).

## Headless verification

`FModel.Snapshot` renders the real window with Avalonia Headless + Skia against
CUE4Parse's fixtures and saves PNG screenshots:

```bash
cd app/FModel.Snapshot
dotnet run -- ../../upstream/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8 ./snapshots
```

It uses `FMODEL_DATA_DIR` so it doesn't touch your real configuration.

More modes, against any game (from `app/FModel.Snapshot`):

```bash
# Builds self-contained vgmstream in vendor/vgmstream-build (sources in vendor/vgmstream-src; --clean to start from scratch)
../packaging/build-vgmstream.sh
# Exports models and textures with several option combinations, lists what each one writes and
# captures Settings › Export (FMODEL_UI_ONLY=1 only takes the screenshots)
dotnet run -- --export-options ./out <Paks folder> GAME_UE4_25 <AES|-> Game/Content/.../Mesh.uasset Game/Content/.../Textura.uasset
# Export › Model/Animation on a sample of packages
dotnet run -- --export <Paks folder> GAME_UE4_25 <AES> ./out 3
# Opens the real 3D viewer (OpenGL, needs the display on) and saves screenshots: meshes,
# animations/blend spaces on the last mesh, "morph:<name|*>=<weight>" for the sliders and
# "anims", "anim:<name>", "seek:<sec>" and "play:<sec>" (animation list, notifies with their parsed effects,
# drawn effect layers, markers and sounds), "video[:<name>[.mov]]" (records one cycle to <out>/video.mp4),
# "normalmaps:off|on", "mode:orbit|fly", "key:W[+Shift]=<sec>" (holds the key and reports how far the
# camera moved), "slider:SpeedX|SpeedY|SpeedZ|Rotation=<multiplier>", "drag:dx,dy" and "hittest"; checks the tangent frame, with FMODEL_MASKS=N lists a
# map's masks and captures a close-up of the landscape with and without textures
dotnet run -- --viewer ./out <Paks folder> GAME_UE4_25 <AES|-> <usmap|-> Game/Content/.../Mesh.uasset Game/Content/.../BS_Something.uasset "morph:*=1"
# Extracts and decodes sounds, saves them as .wav and tests playback (silently)
dotnet run -- --audio ./out <Paks folder> GAME_UE4_25 <AES|-> Game/Content/.../Sound.uasset
# Captures the Audio Player and Settings headless with those sounds in the playlist
dotnet run -- --audio-ui ./out <Paks folder> GAME_UE4_25 <AES|-> Game/Content/.../Sound.uasset
# Tests the AES and mappings endpoints (uedb.dev presets), the cache and the editor
dotnet run -- --endpoints ./out
# Mounts with custom versioning and language, checks they reach CUE4Parse, switches language and
# captures Settings › General and the Options editor
dotnet run -- --versioning ./out <Paks folder> GAME_UE4_25 <AES|->
# Decodes an audio file to raw float (to compare with ffmpeg)
dotnet run -- --decode sound.wav wav output.f32
# Prints package JSON (or --scan-morphs to list meshes with morph targets)
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> Game/Content/.../Package.uasset
# Animations for a skeletal mesh (reference search) and their notifies; decodes the first sound
# (FMODEL_ANIMS=N limits how many; FMODEL_EFFECTS=1 lists each effect's layers with their channels)
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --animations Game/Content/.../Mesh.uasset
# A material's parameters (textures with mean ± deviation per channel, colors, scalars)
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --material Game/Content/.../MI_Something.uasset
# A landscape heightmap written and read back with CUE4Parse's encoder (16-bit grayscale, values kept)
dotnet run -- --heightmap-test ./out
# The FModelMac → FModel data folder move, in a scratch folder
dotnet run -- --migrate-test ./out
# The blend each material under a folder resolves to (masked / translucent / additive…), to find test assets
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --scan-blend Game/Content/Characters 400
# Find References (with the index time) and the size of Show Metadata
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --refs Game/Content/.../Package.uasset
# Creates a backup, reads it and compares it with the game (timings); --online-backup <dir> also lists
# FModel's, downloads the smallest one and compares it (requires network)
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --backup ./out
# Decompiles Lua chunks from disk with the bundled libunluac (FMODEL_LUA_MODE=disassemble, FMODEL_LINES=n)
dotnet run -- --unluac <file.luac>...
# Lists the maps that load other levels (what the Streaming Levels dialog shows)
dotnet run -- --json <paks> <EGame> <aes|-> --streaming-levels
# Exports a world with streaming: captures the dialog, keeps two levels and then tests Skip
dotnet run -- --streaming <outdir> <paks> <EGame> <aes|-> <map.umap>
# Profiles: starts from an old configuration for game A, creates B's profile with the Directory Selector,
# switches between both and restarts
dotnet run -- --profiles <outdir> <paks A> <EGame A> <aes A|-> <paks B> <EGame B> <aes B|->
# The MCP server end to end, through the SDK's client: a profile for the game, then every tool (paging, images,
# exports, errors); give the bundle's executable to test the packaged app instead of FModel.dll
dotnet run -- --mcp-test <outdir> <paks> <EGame> <usmap> [app/dist/FModel.app/Contents/Resources/bin/fmodel]
# Decompile Blueprint: lines, time and classes that failed (FMODEL_LINES=n prints n lines)
dotnet run -- --json <Paks folder> GAME_UE4_25 <AES|-> --decompile Game/Content/.../Algo_BP.uasset
```
