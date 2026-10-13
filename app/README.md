# FModelMac: ventana Avalonia

Primera ventana de escritorio para macOS sobre CUE4Parse: carga un juego,
navega carpetas, muestra propiedades (JSON) y previsualiza texturas.

## Correr

```bash
export PATH=$HOME/.dotnet:$PATH
cd app/FModelMac
dotnet run
```

Requiere haber compilado Oodle antes (ver `poc/README.md`); sin él usa el
descompresor en C#, más lento.

## Empaquetar como .app

```bash
app/packaging/build-app.sh
```

Genera `app/dist/FModelMac.app` (~180 MB): publicación autocontenida para `osx-arm64`
(no requiere .NET instalado) con ReadyToRun, ícono `.icns`, `Info.plist` y firma ad-hoc
(Apple Silicon no ejecuta código sin firmar). Requiere macOS 14+.

- **Ícono:** `packaging/make_icon.py` toma el logo de `FModel.ico` (256 px máx.) y lo compone
  sobre la plantilla de íconos de Big Sur. Para regenerarlo:
  `python3 -I app/packaging/make_icon.py upstream/FModel/FModel.ico <tmp> dark`, luego
  `iconutil -c icns <tmp>/FModelMac.iconset -o app/packaging/FModelMac.icns`.
- **Logs:** `~/Library/Logs/FModelMac/FModelMac-<fecha>.log` (dentro del .app no hay consola).
- **Firma ad-hoc:** sirve en esta Mac. Para compartirlo con otras personas sin el aviso de
  Gatekeeper hace falta un Developer ID de Apple y notarizar.

## Interfaz (igual a FModel)

Replica `MainWindow.xaml` de FModel con la paleta oscura de AdonisUI (capas `#2A2B34`,
`#32323F`, `#262630`…) y sus acentos (`#206BD4` listo, `#D49220` cargando, `#C22B2B` error).

- **Menú:** Directory (Selector, AES, Archives Info), Packages (Search ⌘⇧F), Views, Settings,
  Help. Lo que todavía no existe en macOS (Backup, References, Export Session, Image Merger)
  aparece deshabilitado.
- **Panel izquierdo, pestaña Archives:** Loading Mode (Multiple/All) + Load, lista GAME ARCHIVES
  (los bloqueados salen en gris) e INFORMATION del contenedor seleccionado.
- **Folders:** árbol aplanado y virtualizado con los íconos de carpeta de FModel, contador café de
  paquetes, flecha que rota, "Bring Selected Folder To View" y "Collapse All".
- **N Packages:** búsqueda, breadcrumb clicable, lista con íconos por tipo y menú contextual
  (Extract in New Tab, Export › Model/World/Animation/Properties/Texture, Copy › rutas).
- **Visor 3D** (Views › 3D Viewer, o doble clic sobre una malla): mallas estáticas y con
  esqueleto (LOD 0) con su textura difusa, grid, wireframe, esqueleto, visibilidad por material
  y captura PNG. Arrastrar orbita, clic derecho/Shift paneo, scroll o pellizco zoom, WASD/QE
  mueve, F encuadra la selección. Cada malla que abres se **agrega a la escena** junto a las
  demás; el outliner (SCENE) permite ocultar, quitar o seleccionar cada una, y Clear Scene vacía.
  Doble clic en un **mapa** (.umap) carga el nivel completo: actores y componentes adjuntos,
  instancias (ISM/HISM/foliage), landscapes, niveles de streaming (con su transformación), level
  instances y materiales sobrescritos, con instancing en GPU (una llamada de dibujo por malla).
  Los mapas de **World Partition** (UE5) cargan todas sus celdas de runtime en paralelo, tanto del
  hash set de UE 5.3+ como de los grids espaciales de 5.0–5.2: el contenido principal queda en el
  mapa, cada data layer de runtime es un elemento aparte (visible si empieza activado en el juego)
  y cada capa HLOD otro, oculto por defecto. Cielos y fondos gigantes van a un elemento aparte,
  oculto por defecto.
  Con una malla con esqueleto abierta, doble clic en una animación (secuencia, montage o
  composite) la reproduce: play/pausa, barra de tiempo, velocidad, bucle y root motion opcional.
  Los **blend spaces** (1D y 2D) muestran un slider por eje y mezclan sus muestras con la
  cuadrícula de pesos que UE cocina (`GridSamples`), sincronizadas por fase. Los **morph targets**
  aparecen con un slider cada uno; si la animación trae curvas con el mismo nombre, ellas mandan.
  En cooks de UE5 para PC los deltas de morph sólo existen en el búfer de GPU de la malla;
  `PopulateMorphTargetVerticesData()` de CUE4Parse los recupera.
  Los huesos se empatan por nombre, como en Snooper, y el skinning va en GPU con las matrices en
  una textura float (macOS no tiene SSBO).
- **Audio Player (Views › Audio Player):** como el de FModel: dispositivo de salida, volumen,
  codificación/duración/bytes por segundo, playlist con búsqueda (Play, Remove, Save, Save
  Playlist), reloj, anterior/play/siguiente, analizador de espectro de 75 barras y línea de tiempo
  con forma de onda (clic o arrastre para buscar). Atajos: Espacio, Cmd+←/→, Supr. Extraer un
  sound wave, evento o media de Wwise, evento/banco de FMOD, cue sheet o wave bank de CRIWARE,
  banco SEAD de Square Enix o un `.wem/.bnk/.pck/.bank/.awb/.acb` lo agrega y lo reproduce.
  Export › Audio guarda todos los sonidos de la selección en `.wav` (o en su formato original,
  según Settings › Audio). La salida usa AudioToolbox (AudioQueue) de macOS.
  WAV (PCM, float, MS/IMA ADPCM), Ogg Vorbis (NVorbis, como FModel) y CRIWARE HCA/ADX se
  decodifican dentro de la app; Wwise, Bink, UE Opus, AT9 y demás pasan por
  [vgmstream](https://vgmstream.org), que va dentro del .app: `packaging/build-vgmstream.sh` lo
  compila (r2117) con FFmpeg 7.1.1 mínimo y sólo decodificadores (LGPL), mpg123, vorbis, speex,
  atrac9 y celt estáticos, así que sólo depende de libSystem (5 MB). Sus licencias quedan en
  `Contents/Resources/Licenses/vgmstream`. La build oficial para Mac depende de versiones exactas
  de Homebrew; si falta la nuestra, se usa la de Homebrew (`brew install vgmstream`). RAD Audio (UE 5) necesita
  `radadec`, igual que en FModel: compílalo desde Unreal Engine y ponlo en la carpeta de datos.
- **Panel derecho:** pestañas por asset (doble clic reemplaza la actual, "+" agrega, clic central
  cierra), propiedades con el `Json.xshd` de FModel y la textura a la derecha con divisor ajustable.
- **Consola** desplegable con `[INF]/[WRN]/[ERR]` en los colores de FModel, abrir carpeta de salida
  y limpiar.
- **Barra de estado** que cambia de color según el estado.
- **Diálogos:** Directory Selector, AES Manager (clave estática y dinámicas por GUID), Settings
  (pestañas General, Models, Audio y Endpoints), Search y About.
- **Versiones del motor (Settings › General):** UE Version, Texture Platform y los tres editores de
  versioning de FModel (Custom Versions, Options, MapStructTypes). Se guardan por carpeta del juego
  y se pasan a CUE4Parse en el `VersionContainer`. A diferencia de FModel, los editores de Options y
  MapStructTypes arrancan con los valores efectivos de CUE4Parse para esa versión y sólo guardan lo
  que cambias. Cambiar cualquiera de estos vuelve a montar el juego al dar OK.
- **Idioma (Packages Language):** el idioma de los textos localizados (FText). Si el juego no tiene
  esa cultura exacta usa la más parecida (`es-MX` → `es`). CUE4Parse sólo cambia a culturas que el
  juego declara (`CulturesToStage` o `.locmeta`); si no las declara, la app carga los `.locres` del
  juego por su cuenta. Como en CUE4Parse, los textos de Engine no se cargan. Cambiar sólo el idioma
  recarga los textos sin volver a montar.
- **Endpoints (Settings › Endpoints):** como en FModel, una URL que devuelve JSON más una expresión
  JSONPath. El de AES da la llave principal y, opcionalmente, la lista de llaves dinámicas
  (`guid`/`key`); el de mappings, la URL del `.usmap`/`.jmap` y opcionalmente su nombre. Cada vez
  que se monta el juego se refrescan las llaves y se descarga el mapping (una vez; queda en caché
  por endpoint en `Mappings/` y se reutiliza si el endpoint falla). Un mapping local en Settings
  tiene prioridad. El editor (Configure…) tiene Send/Test, presets de Fortnite y VALORANT
  (uedb.dev, los mismos que FModel), y sólo deja guardar una configuración probada. Al montar
  FortniteGame o ShooterGame sin endpoints se aplica el preset una vez y se vuelve a montar.

Se guardan en `~/Library/Application Support/FModelMac/settings.json`. Las exportaciones van a
`<Output>/Exports/<ruta del paquete>`, como en FModel. Los modelos salen en glTF (`.glb`) por
defecto; las animaciones pasan a USD porque CUE4Parse no las escribe en glTF, y los mundos siempre
son USD.

## Decisiones

- **Avalonia 11.3, no 12:** Avalonia 12 usa SkiaSharp 3.x y CUE4Parse-Conversion fija
  SkiaSharp `[2.88.9]`. FModel tiene el mismo candado.
- **ComboBox para la versión:** `AutoCompleteBox` reescribía el valor al filtrar.
- **Recursos de FModel:** los PNG de `Assets/Icons`, los `.xshd` de `Assets/Highlighting` y
  `Themes/Icons.axaml` (generado de `Icons.xaml`) vienen de FModel, que es GPL-3.0; si se
  distribuye esta app, también debe ser GPL-3.0.
- **Visor 3D propio, no Snooper:** Snooper (OpenTK/GLFW + ImGui) pide OpenGL 4.6 con SSBO y macOS
  se queda en 4.1. El visor es un `OpenGlControlBase` de Avalonia con GLSL 1.50; usa los mismos ejes
  que el exportador glTF de CUE4Parse (Y↔Z), así que se ve igual que en Blender.
- **Fuente Helvetica Neue:** Inter convierte "0x4" en "0×4" (ligadura contextual) y Avalonia no
  acepta la fuente del sistema `.AppleSystemUIFont` (familia oculta).

## Verificación sin pantalla

`FModelMac.Snapshot` renderiza la ventana real con Avalonia Headless + Skia contra los
fixtures de CUE4Parse y guarda capturas PNG:

```bash
cd app/FModelMac.Snapshot
dotnet run -- ../../upstream/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8 ./snapshots
```

Usa `FMODELMAC_DATA_DIR` para no tocar tu configuración real.

Más modos, contra cualquier juego (desde `app/FModelMac.Snapshot`):

```bash
# Compila vgmstream autocontenido en vendor/vgmstream-build (fuentes en vendor/vgmstream-src; --clean para empezar de cero)
../packaging/build-vgmstream.sh
# Export › Model/Animation sobre una muestra de paquetes
dotnet run -- --export <carpeta Paks> GAME_UE4_25 <AES> ./out 3
# Abre el visor 3D real (OpenGL, necesita la pantalla encendida) y guarda capturas: mallas,
# animaciones/blend spaces sobre la última malla, y "morph:<nombre|*>=<peso>" para los sliders
dotnet run -- --viewer ./out <carpeta Paks> GAME_UE4_25 <AES|-> <usmap|-> Game/Content/.../Malla.uasset Game/Content/.../BS_Algo.uasset "morph:*=1"
# Extrae y decodifica sonidos, los guarda en .wav y prueba la reproducción (en silencio)
dotnet run -- --audio ./out <carpeta Paks> GAME_UE4_25 <AES|-> Game/Content/.../Sonido.uasset
# Captura el Audio Player y Settings sin pantalla con esos sonidos en la playlist
dotnet run -- --audio-ui ./out <carpeta Paks> GAME_UE4_25 <AES|-> Game/Content/.../Sonido.uasset
# Prueba los endpoints de AES y mappings (presets de uedb.dev), la caché y el editor
dotnet run -- --endpoints ./out
# Monta con versioning e idioma propios, verifica que lleguen a CUE4Parse, cambia de idioma y
# captura Settings › General y el editor de Options
dotnet run -- --versioning ./out <carpeta Paks> GAME_UE4_25 <AES|->
# Decodifica un archivo de audio a float crudo (para comparar con ffmpeg)
dotnet run -- --decode sonido.wav wav salida.f32
# Imprime el JSON de paquetes (o --scan-morphs para listar mallas con morph targets)
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> Game/Content/.../Paquete.uasset
```
