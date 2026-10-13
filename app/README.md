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
- **unluac (Lua compilado):** `app/packaging/build-unluac.sh` compila el unluac de FModel (Java, MIT,
  commit `fe187b4`) como `vendor/unluac-build/libunluac.dylib` con GraalVM Native Image; la primera
  vez descarga GraalVM CE 25 (~340 MB, verificado con su SHA-256) a `vendor/graalvm`. FModel sólo
  publica la librería para Windows y Linux. Si está, `build-app.sh` la mete en la app con su licencia.
- **Logs:** `~/Library/Logs/FModelMac/FModelMac-<fecha>.log` (dentro del .app no hay consola).
- **Firma ad-hoc:** sirve en esta Mac. Para compartirlo con otras personas sin el aviso de
  Gatekeeper hace falta un Developer ID de Apple y notarizar.

## Interfaz (igual a FModel)

Replica `MainWindow.xaml` de FModel con la paleta oscura de AdonisUI (capas `#2A2B34`,
`#32323F`, `#262630`…) y sus acentos (`#206BD4` listo, `#D49220` cargando, `#C22B2B` error).

- **Menú:** Directory (Selector, AES, Archives Info), Packages (Search ⌘⇧F, References ⌘⇧R),
  Views, Settings, Help.
- **Image Merger (Views › Image Merger):** como en FModel, une varias imágenes en filas (Images Per
  Row, que arranca en la raíz cuadrada del número de imágenes, y Margin Between Images, que se
  guarda), con Add…, Move Up/Down, Remove, Clear, Open Image y Save Image. Además de archivos
  (PNG, JPEG, BMP, GIF, WebP y TIFF, este último convertido con `sips` de macOS), acepta texturas
  directo desde su vista previa: clic derecho › Add to Image Merger.
- **Backup (Directory › Backup) y Loading Mode "All (New)" / "All (Modified)":** como en FModel, un
  backup (`.fbkp`) guarda la ruta, el tamaño y el cifrado de todos los archivos del juego; al cargar
  con All (New) o All (Modified) se elige un backup más viejo y sólo se ven los archivos nuevos o
  los que cambiaron. Mismo formato que FModel (LZ4 frame, incluido su formato viejo sin versión),
  así que sus backups sirven aquí y al revés. La ventana crea el backup del juego actual en
  `Output/Backups`, lista los tuyos (Show in Finder, Load New, Load Modified) y descarga los de
  FModel (su servicio, Fortnite). El backup se lee como stream: el de Fortnite 41.20 (6.7 millones
  de entradas, 43 MB) se compara en ~4 s con ~130 MB de memoria.
- **Favorite Directories (Packages › Favorite Directories):** atajos a carpetas, guardados por
  carpeta del juego, como en FModel: Add Directory… y, por cada uno, Go To / Edit Directory… /
  Delete Directory. Go To carga los archives si hace falta y abre la carpeta en Folders. Al montar
  un juego por primera vez se agregan los presets de FModel (Fortnite, VALORANT, Dead by Daylight).
  El árbol de Folders tiene menú contextual con "Export Folder", "Favorite Directory" y "Copy
  Directory Path"; el diálogo avisa si la carpeta no existe en el juego cargado.
- **Export Folder** (clic derecho en una carpeta del árbol o del explorador, o un atajo de exportación
  con el foco en el árbol): Raw Data, Properties, Textures, Decompiled Blueprints, Models, Worlds,
  Animations o Audio de todos los archivos de la carpeta y sus subcarpetas, como FModel. Texturas,
  modelos, mundos, animaciones y raw data pasan por la Export Session; los paquetes se leen en
  paralelo y uno ilegible se salta. Con muchos archivos la consola muestra un resumen (cuántos se
  guardaron, cuántos no tenían nada, los primeros errores) y la barra de estado el avance. **Esc**
  cancela (también la Export Session en curso). DSR: 1,095 Properties en 4.6 s, 104 texturas en 4.8 s.
- **Panel izquierdo, pestaña Archives:** Loading Mode (Multiple/All) + Load, lista GAME ARCHIVES
  (los bloqueados salen en gris) e INFORMATION del contenedor seleccionado.
- **Folders:** árbol aplanado y virtualizado con los íconos de carpeta de FModel, contador café de
  paquetes, flecha que rota, "Bring Selected Folder To View" y "Collapse All".
- **N Packages:** búsqueda, breadcrumb clicable, lista con íconos por tipo y menú contextual
  (Extract in New Tab, Show Metadata, Find References, Decompile Blueprint, Export › Raw Data/
  Properties/Texture/Model/World/Animation/Audio/Decompiled Blueprints, Copy › rutas). Las pestañas de assets tienen su propio menú (Close
  Tab/All/Other, Save Text…, Show Metadata, Find References, Decompile Blueprint).
- **Decompile Blueprint:** las clases del paquete como pseudo C++ (variables con sus valores por
  defecto y funciones a partir del bytecode Kismet), en una pestaña nueva con resaltado de C++. Usa
  el descompilador de CUE4Parse, igual que FModel, pero siempre lee el bytecode: en FModel hay que
  activar antes "Serialize Script Bytecode" y la opción está escondida. Save Text… guarda el `.cpp`.
  En FF VII se descompilaron 800 Blueprints y Widgets sin fallas.
- **Settings › General › Advanced:** "Serialize script bytecode" y "Serialize inlined shader maps"
  de FModel, para ver esos datos en el JSON de propiedades. Se aplican sin volver a montar.
- **Show Metadata:** el resumen del paquete, el name map, los imports y los exports en JSON, en una
  pestaña nueva (como FModel).
- **Find References / Search For Packages:** la ventana de búsqueda tiene las pestañas By Path y
  References, cada una con filtro, columna Archive y el mismo menú contextual. FModel sólo encuentra
  referencias en juegos IoStore (lee el encabezado del contenedor); aquí también funciona con `.pak`:
  la primera vez se leen los encabezados de todos los paquetes y se arma un índice de imports
  (38,302 paquetes de FF VII en 0.7 s; 103,780 de DSR en ~30 s) que se guarda en disco, por juego y
  por conjunto de archivos montados, así que las siguientes veces carga en una fracción de segundo.
  Al montar se cargan las *virtual paths* de los `.uplugin` como en FModel, para que lo importado
  desde plugins (`/ACLPlugin/…`) se resuelva: sin ellas DSR no decodificaba ninguna animación.
- **Visor 3D** (Views › 3D Viewer, o doble clic sobre una malla): mallas estáticas y con
  esqueleto (LOD 0) con su textura difusa, grid, wireframe, esqueleto, visibilidad por material
  y captura PNG. El sombreado es PBR (GGX), con texturas en sRGB, tone mapping ACES, una luz
  principal que sigue a la cámara desde arriba y a un lado, otra de relleno y luz ambiental de
  cielo/suelo. Usa el **normal map** de cada material (tangentes de la malla, convención DirectX
  de UE) y su textura de **máscaras** para roughness, metallic y oclusión. Como cada juego empaca
  las máscaras distinto, el layout sale del nombre de la textura o del parámetro (`_ORM`, `_RMA`,
  `MSRAO`, `SRXO`, `_Roughness`…, o el `SpecularMasks` de Fortnite); si no se reconoce, la máscara
  no se usa. El interruptor **Normal Maps** apaga ambos. Si el material no tiene un parámetro
  difuso conocido, se toma su propia textura de color (sRGB) cuyo nombre lo indique (`BaseTexture`,
  `MainTex`, `*_BaseColor`…) antes que el `PM_Diffuse` que adivina CUE4Parse, que puede ser la
  textura por omisión del material padre (en DSR, Flamedramon salía con la de Apocalymon).
  **Controles** (como Snooper, con su selector **Orbit / Fly**, que se recuerda): en Orbit arrastrar
  gira alrededor del modelo y el scroll o el pellizco acercan; en Fly arrastrar mira alrededor, el
  scroll avanza y +/- (o scroll mientras arrastras) cambian la velocidad. En ambos: WASD mueve y Q/E
  baja/sube mientras mantienes la tecla (Shift más rápido, ⌥ más lento), clic derecho, central o
  Shift-arrastre hace paneo, las **flechas** giran la cámara como un arrastre en esa dirección, C/X
  acercan/alejan la lente, F encuadra la selección y Esc cierra el visor. Las teclas funcionan sin tener que hacer clic
  antes en la vista. La sección **CAMERA** del panel derecho tiene un slider de velocidad por eje
  (X: A/D, Y: W/S, Z: Q/E) y otro de rotación (arrastre y flechas), de 0.1× a 10×, que se recuerdan. Cada malla que abres se **agrega a la escena** junto a las
  demás; el outliner (SCENE) permite ocultar, quitar o seleccionar cada una, y Clear Scene vacía.
  Doble clic en un **material** (instancia o material base) lo muestra sobre un cubo como en FModel
  (el siguiente material reemplaza al del cubo); con el botón de material de una sección encendido
  en la lista MATERIALS, el siguiente material que abras **sustituye el de esa sección** (el
  "overwrite material" de FModel). Settings › Preview tiene Preview Materials y **Preview Max
  Texture Size** (4–4096 px, 1024 por omisión) para las texturas del visor; los mapas usan 512 como
  máximo (256 para normal maps y máscaras), y todo se decodifica con la plataforma de texturas del
  juego.
  Doble clic en un **mapa** (.umap) carga el nivel completo: actores y componentes adjuntos,
  instancias (ISM/HISM/foliage), niveles de streaming (con su transformación), level instances y
  materiales sobrescritos, con instancing en GPU (una llamada de dibujo por malla). Las **spline
  meshes** (carreteras, vías, cables) se doblan a lo largo de su spline, con normales incluidas. Los
  **landscapes** se arman como un terreno continuo y se pintan con las **texturas reales de sus capas**:
  cada capa toma la textura difusa del material cuyo nombre coincide (Grass_LayerInfo →
  T_Ground_Grass_D), repetida en el espacio del terreno como el `LandscapeCoords` de UE, cada cuántos
  metros diga su escalar de tiling (`Cliff_TilingSize`, `Ground_Tiling`…; 8 m si no hay), y los pesos
  de pintura de cada componente mezclan en el shader sus cuatro capas principales. Las capas sin
  textura (o más allá de cuatro) usan el color promedio, con una paleta por nombre como último
  recurso (el visor de FModel no muestra landscapes).
  Los mapas de **World Partition** (UE5) cargan todas sus celdas de runtime en paralelo, tanto del
  hash set de UE 5.3+ como de los grids espaciales de 5.0–5.2: el contenido principal queda en el
  mapa, cada data layer de runtime es un elemento aparte (visible si empieza activado en el juego)
  y cada capa HLOD otro, oculto por defecto. Cielos y fondos gigantes van a un elemento aparte,
  oculto por defecto.
  Al seleccionar una malla con esqueleto, la sección **ANIMATIONS** lista las animaciones hechas
  para su esqueleto (o sus esqueletos compatibles) en cualquier juego: toda animación apunta al
  esqueleto con que se hizo, así que son los paquetes que lo referencian cuyo export principal es
  una secuencia, montage, composite, blend space o aim offset. Un clic la reproduce; el filtro busca
  por nombre. En juegos .pak el índice de referencias se arma una vez (DSR: ~30 s) y se guarda en
  `References/` del directorio de datos (luego carga en 0.1 s); mientras tanto la lista muestra lo
  que hay en las carpetas cercanas al esqueleto.
  Los **notifies** de la animación aparecen como marcas bajo la barra de tiempo (naranja: efecto,
  verde: sonido, gris: evento; pasa el mouse para ver el detalle, clic para saltar ahí). Se leen sin
  conocer las clases del juego: lo que referencia cada notify decide su tipo (un sistema Niagara o
  Cascade es un efecto, un asset de sonido un sonido, una propiedad *Socket*/*Bone* el punto de
  anclaje), así funcionan igual los de UE (`AnimNotify_PlayNiagaraEffect`, `PlayParticleEffect`,
  `TimedParticleEffect`, `PlaySound`) y los propios de cada juego (DSR, FF VII); un sonido nombrado
  sólo por su evento se busca como paquete de ese nombre (o `_Cue`). Los montages incluyen los
  notifies de las secuencias que reproducen. Con **Sounds** los sonidos suenan sincronizados
  (SoundWave, SoundCue, Wwise, etc., por el mismo extractor del Audio Player); con **Effects** cada
  efecto activo se dibuja en su socket con su nombre, mientras dura (al menos 1.2 s). Es una
  **aproximación**: no se simula el sistema (Niagara corre scripts compilados), sino que cada emisor
  (renderer sprite, ribbon o mesh de Niagara; `ParticleModuleRequired` de Cascade) se dibuja como
  un sprite aditivo de cara a la cámara con la textura principal de su material, flipbooks
  (`SubImageSize` / `SubImages_*`) animados a lo largo de la vida del efecto, crecimiento, giro y
  fundido; el tamaño sale de los bounds fijos del emisor o del sistema. Las texturas de color sin
  fondo propio se combinan con la de opacidad del material (o se desvanecen en círculo); los
  materiales de distorsión no se dibujan y los placeholders (`DefaultTexture`, `T_White`) son un
  brillo suave. Los colores de las partículas los calcula el VM de Niagara, así que una textura en
  gris toma el color del material o, si no tiene, uno por el nombre del efecto (fire, ice, thunder…).
  Con una malla con esqueleto abierta, doble clic en una animación (secuencia, montage o
  composite) también la reproduce: play/pausa, barra de tiempo, velocidad, bucle y root motion opcional.
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
- **Explorador de assets** (el "Preview New Explorer System" de FModel, interruptor arriba a la
  derecha): al elegir una carpeta, el panel derecho muestra sus subcarpetas y archivos como
  **mosaicos** con miniatura (texturas, imágenes, íconos de Fortnite) o ícono y color por categoría,
  tipo de la clase principal y tamaño; o como **lista** (nombre, tipo, tamaño). Buscador, filtro por
  categoría (⌥0–⌥9), selección con clic/⌘/⇧ y flechas, ⌘A, ⌫ sube una carpeta, Enter o doble clic
  abre (un archivo cambia a las pestañas; una carpeta entra, saltando cadenas de carpetas únicas),
  menú contextual con Extract/Export/Copy como el de Packages, y los atajos de exportación actúan
  sobre los mosaicos seleccionados. Solo se leen los paquetes que están en pantalla (4 a la vez, los
  más recientes primero), así que carpetas grandes abren al instante (15 texturas de DSR con
  miniatura en 0.27 s); el filtro por categoría sí lee toda la carpeta. ⌘[ o el botón del ratón
  "atrás" regresan al explorador desde las pestañas; Z (Settings › Hotkeys) alterna, y los botones
  flotantes abajo a la derecha alternan explorador/pestañas y las miniaturas.
- **Consola** desplegable con `[INF]/[WRN]/[ERR]` en los colores de FModel, abrir carpeta de salida
  y limpiar.
- **Barra de estado** que cambia de color según el estado.
- **Diálogos:** Directory Selector, AES Manager (clave estática y dinámicas por GUID), Settings
  (pestañas General, Export, Preview, Hotkeys, Lua, Audio, Endpoints y Themes), Search y About.
- **Perfiles de juego (Directory › Profiles), no existe en FModel:** cada juego queda guardado con
  su carpeta, versión de UE, llave AES y dinámicas, mapping, endpoints e idioma; lo que ya se guarda
  por carpeta (versioning, favoritos, opcode map de Lua, último refresco de AES) lo sigue solo. El
  menú lista los perfiles (el actual marcado) para cambiar con un clic: se guarda el juego actual y
  se monta el otro tal como quedó. El Directory Selector elige, crea ("New Profile…", empieza sin
  llaves ni mappings), renombra y borra perfiles. Lo que cambias en AES o Settings se guarda en el
  perfil actual. Al actualizar, el juego configurado se convierte en el primer perfil.
- **Decompile Lua (Settings › Lua):** los `.luac` (Lua 5.0–5.5 compilado) se abren como código con
  el unluac de FModel, incluido en la app (FModel lo descarga y no hay versión para Mac). Modos
  Decompile y Disassemble, Raw string, No debug y LuaJ, y un opcode map por juego para los que
  revuelven los opcodes. Si unluac no puede, se muestra el volcado hex y el motivo en el log.
  Probado con 284 chunks (las pruebas de unluac compiladas con `luac` 5.5): todos bien. Los `.luac`
  de FF VII están cifrados por el juego, así que ahí no hay nada que decompilar.
- **Buscar en el texto (⌘F):** el panel de búsqueda del editor (AvaloniaEdit), como el Ctrl+F / F3 de
  FModel. ⌘F lo abre desde cualquier parte de la ventana (empieza con lo seleccionado), ⌘G / F3 va a la
  siguiente coincidencia y ⇧⌘G / ⇧F3 a la anterior; tiene mayúsculas, palabra completa y regex.
- **Varias imágenes por pestaña:** como en FModel, todas las texturas del paquete, cada capa de un
  texture array (`nombre_0`, `nombre_1`…) y los cubemaps desdoblados como panorama. Las flechas sobre la
  imagen (o ← → después de hacerle clic) pasan de una a otra con "n / total"; Open, Save y Add to Image
  Merger usan la que se ve. El pixel art se escala sin suavizar y las texturas se decodifican con la
  Texture Platform del juego.
- **Streaming Levels:** al exportar un mundo que carga otros niveles, la Export Session pregunta cuáles
  exportar (la ventana de FModel: árbol de actores y niveles con casillas, Select All/None, Skip y OK);
  los que no marques quedan referenciados en el USD sin exportarse. El reloj de la sesión se pausa
  mientras eliges. En DSR, `RealCopyWorld_Subway01` (11 niveles) exportó 2,304 archivos sin fallas.
- **Export › Raw Data y Decompiled Blueprints:** Raw Data pasa por la Export Session con el
  `RawDataExporter` de CUE4Parse (como FModel) y escribe los archivos del paquete tal cual (`.uasset`,
  `.uexp`, `.ubulk`…). Decompiled Blueprints guarda el pseudo C++ de Decompile Blueprint como `.cpp`.
- **Keep Directory Structure (Settings › General):** encendido por defecto, como en FModel. Apagado,
  las propiedades, los `.cpp` y el audio se guardan directo en `Exports/` sin la ruta del paquete. La
  Export Session siempre conserva la estructura (CUE4Parse la arma).
- **Settings › Preview:** qué abre el visor 3D al extraer, por tipo (mallas estáticas, esqueléticas,
  animaciones, mundos), y "Handle Skeletons as Empty Meshes": un `USkeleton` se abre como huesos
  solos (se pueden reproducir animaciones sobre él) y Export › Model también lo toma; como glTF no
  admite un esqueleto solo, se exporta en USD. La opción vieja "preview meshes" se migra sola.
- **Settings › Hotkeys:** los atajos de FModel, configurables, con ⌘ en vez de Ctrl: cambiar de
  pestaña del panel izquierdo (A/D) y de asset (Q/E), agregar y quitar pestaña (⌘T/⌘W), las ocho
  exportaciones (sin asignar por defecto; se muestran en el menú Export) y los del Audio Player
  (⌘N agrega archivos de audio del disco, K, J, L, X). Los modificadores deben coincidir exactos,
  así ⌘A no dispara A, y las teclas solas no se disparan mientras escribes en un campo de texto.
- **AES Reload at Launch (Settings › Endpoints):** Always, Never u Once Per Day (por defecto, como en
  FModel; por carpeta del juego). Si cambias el endpoint de AES se consulta al dar OK sin importar
  la opción.
- **Settings › Themes:** los 16 JSON Highlight Themes de FModel (Default, Nord, Tokyo Night, Monokai,
  Rosé Pine…) con su vista previa. Se aplican al dar OK a todas las pestañas abiertas; además se
  colorean las secuencias de escape (`\n`, `\"`), que el `.xshd` de FModel no distingue.
- **Opciones de exportación (Settings › Export):** las de FModel: Mesh Format (glTF, USD,
  UEFormat, ActorX), Mesh Quality (LODs), Socket Format, Compression (sólo UEFormat), Nanite
  Format, morph targets; Texture Format (PNG, JPEG, TGA, WebP), Texture Quality (JPEG/WebP),
  HDR como `.hdr`, todos los mips; Material Depth y exportar los materiales de la malla. Se pasan a
  la `ExportSession` de CUE4Parse, que ahora también exporta las texturas (arrays por capa,
  cubemaps como panorama). Si CUE4Parse no puede leer el Nanite de una malla con "Only Nanite LOD",
  se vuelve a exportar con su malla de respaldo y se avisa en el log.
- **Export Session (Views › Export Session):** como en FModel, Export › Model/Animation/World/
  Texture ponen los assets en una cola (o se exportan al momento con "Export immediately" en
  Settings › Export, apagado por defecto igual que en FModel). La ventana muestra la cola agrupada
  por clase, el progreso, el tiempo y el ETA, cuántos salieron bien o fallaron, y los logs de cada
  asset (de un sink de Serilog que recibe lo que reportan los exportadores de CUE4Parse), incluidos
  los materiales y texturas que se exportan junto con una malla. Se puede quitar assets de la cola,
  cancelar, abrir el archivo en Finder y, en Export Options, sobrescribir las opciones y la carpeta
  sólo para esa sesión (o guardarlas como default). Como CUE4Parse usa un juego de opciones por
  corrida, la cola se exporta en grupos: los mundos (sólo USD) y, con glTF, las animaciones (USD).
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
  que se monta el juego se refrescan las llaves (según AES Reload at Launch) y se descarga el mapping (una vez; queda en caché
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
# Exporta modelos y texturas con varias combinaciones de opciones, lista lo que escribe cada una y
# captura Settings › Export (FMODELMAC_UI_ONLY=1 sólo hace las capturas)
dotnet run -- --export-options ./out <carpeta Paks> GAME_UE4_25 <AES|-> Game/Content/.../Malla.uasset Game/Content/.../Textura.uasset
# Export › Model/Animation sobre una muestra de paquetes
dotnet run -- --export <carpeta Paks> GAME_UE4_25 <AES> ./out 3
# Abre el visor 3D real (OpenGL, necesita la pantalla encendida) y guarda capturas: mallas,
# animaciones/blend spaces sobre la última malla, "morph:<nombre|*>=<peso>" para los sliders y
# "anims", "anim:<nombre>", "seek:<seg>" y "play:<seg>" (lista de animaciones, notifies con sus efectos leídos,
# capas de efecto dibujadas, marcadores y sonidos),
# "normalmaps:off|on", "mode:orbit|fly", "key:W[+Shift]=<seg>" (mantiene la tecla y dice cuánto se movió la
# cámara), "slider:SpeedX|SpeedY|SpeedZ|Rotation=<multiplicador>", "drag:dx,dy" y "hittest"; revisa el marco tangente, con FMODELMAC_MASKS=N lista las máscaras de un
# mapa y captura un acercamiento al landscape con y sin texturas
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
# Animaciones de una malla con esqueleto (búsqueda por referencias) y sus notifies; decodifica el primer sonido
# (FMODELMAC_ANIMS=N limita cuántas; FMODELMAC_EFFECTS=1 lista las capas de cada efecto con sus canales)
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> --animations Game/Content/.../Malla.uasset
# Parámetros de un material (texturas con promedio ± desviación por canal, colores, escalares)
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> --material Game/Content/.../MI_Algo.uasset
# Find References (con el tiempo del índice) y el tamaño de Show Metadata
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> --refs Game/Content/.../Paquete.uasset
# Crea un backup, lo lee y lo compara con el juego (tiempos); --online-backup <dir> además lista los de
# FModel, descarga el más chico y lo compara (requiere red)
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> --backup ./out
# Decompila chunks de Lua del disco con la libunluac incluida (FMODELMAC_LUA_MODE=disassemble, FMODELMAC_LINES=n)
dotnet run -- --unluac <archivo.luac>...
# Lista los mapas que cargan otros niveles (lo que muestra el diálogo de Streaming Levels)
dotnet run -- --json <paks> <EGame> <aes|-> --streaming-levels
# Exporta un mundo con streaming: captura el diálogo, deja dos niveles y luego prueba Skip
dotnet run -- --streaming <outdir> <paks> <EGame> <aes|-> <mapa.umap>
# Perfiles: parte de una configuración vieja del juego A, crea el perfil de B con el Directory Selector,
# cambia entre ambos y reinicia
dotnet run -- --profiles <outdir> <paks A> <EGame A> <aes A|-> <paks B> <EGame B> <aes B|->
# Decompile Blueprint: líneas, tiempo y clases que fallaron (FMODELMAC_LINES=n imprime n líneas)
dotnet run -- --json <carpeta Paks> GAME_UE4_25 <AES|-> --decompile Game/Content/.../Algo_BP.uasset
```
