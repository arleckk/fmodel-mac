# FModel en macOS: prueba de concepto del núcleo

App de consola que corre **CUE4Parse** (el motor de FModel) nativo en macOS arm64:
monta contenedores Pak/IoStore (cifrados y comprimidos), lista archivos, exporta
paquetes a JSON y texturas a PNG.

## Estructura

```
fmodel-mac/
├── upstream/        clon de 4sval/FModel (+ submódulo CUE4Parse)
├── oodle-src/       clon de WorkingRobot/OodleUE (Oodle 2.9.16)
└── poc/FModelMac.Poc
```

## Requisitos

- .NET 10 SDK (instalado en `~/.dotnet`, no está en el PATH por defecto)
- Xcode command line tools y CMake (`brew install cmake`)

```bash
export PATH=$HOME/.dotnet:$PATH
```

## 1. Parche a CUE4Parse-Natives

Apple clang 17+ trata `-Wmissing-template-arg-list-after-template-kw` como error
en los headers de ACL. Ya está parcheado en
`upstream/CUE4Parse/CUE4Parse-Natives/CMakeLists.txt` (se desactiva ese warning
con Clang). Sin el parche, `dotnet build` de CUE4Parse falla.

## 2. Compilar Oodle para macOS arm64

OodleUE no publica binarios para macOS, pero su código compila sin cambios:

```bash
cd oodle-src/build
cmake -S . -B builds/macos -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++
cmake --build builds/macos --config Release -j 10
```

Resultado: `builds/macos/lib/liboodle-data-shared.dylib`. El `.csproj` de la PoC
lo copia junto al ejecutable si existe.

## 3. Compilar y correr

```bash
cd poc/FModelMac.Poc
dotnet build -c Release
dotnet bin/Release/net10.0/FModelMac.Poc.dll --help
```

Ejemplo con los fixtures de CUE4Parse (IoStore cifrado + Oodle + sin versión):

```bash
F=../../upstream/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8
dotnet bin/Release/net10.0/FModelMac.Poc.dll $F/IoStore/Unversioned/OodleEncrypted \
  --global $F/IoStore/Unversioned/global.utoc \
  --usmap $F/Mappings/CUE4ParseFixtures-Oodle.usmap \
  --aes-ascii CUE4ParseFixtureAESKey0123456789 \
  --export T_BC7.uasset --export SK_Fixture.uasset --read-all
```

Con un juego real (los `.pak`/`.utoc` están en `<Juego>/Content/Paks`):

```bash
dotnet bin/Release/net10.0/FModelMac.Poc.dll "/ruta/al/Juego/Content/Paks" \
  --game GAME_UE5_4 --aes 0xTU_CLAVE --usmap /ruta/mappings.usmap --read-all
```

`--oodle managed` fuerza el descompresor en C# (OodleSharp) para comparar.

## Resultados (macOS 26.6.2, Apple Silicon, .NET 10.0.12)

- Suite de pruebas de CUE4Parse: **211 correctas, 0 fallidas, 1 omitida** (también se omite en upstream).
- Nativas: `CUE4Parse-Natives.dylib` (ACL) y `liboodle-data-shared.dylib` cargan bien.
- IoStore cifrado + Oodle + mappings: monta, descomprime 111/111 archivos y exporta
  texturas BC1/BC5/BC7/FloatRGBA/streaming a PNG correctas, además de mallas,
  blueprints y materiales a JSON.
- Oodle nativo y administrado dan **bytes idénticos** (mismo SHA-256) en Pak,
  IoStore y IoStore particionado. El nativo fue de 2x a 30x más rápido en estos
  fixtures pequeños; falta medirlo con un juego real.
