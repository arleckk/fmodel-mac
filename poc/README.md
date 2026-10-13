# FModel on macOS: core proof of concept

Console app that runs **CUE4Parse** (FModel's engine) natively on macOS arm64:
it mounts Pak/IoStore containers (encrypted and compressed), lists files, exports
packages to JSON and textures to PNG.

> **Note:** the app (`app/`) no longer uses or bundles native Oodle; it decompresses with
> OodleSharp (managed C#, MIT), CUE4Parse's default (about 4% slower than native on a 9-level map,
> 27.7 s vs 26.5 s, with identical results). `scripts/setup-upstream.sh` no longer sets up
> `oodle-src/`; to rerun this PoC's native-vs-managed comparison, clone and build it by hand as
> described in step 2. The rest of this document is the PoC's historical record.

## Structure

```
fmodel-mac/
├── upstream/        clone of 4sval/FModel (+ CUE4Parse submodule)
├── oodle-src/       clone of WorkingRobot/OodleUE (Oodle 2.9.16)
└── poc/FModel.Poc
```

## Requirements

- .NET 10 SDK (installed in `~/.dotnet`, not on the PATH by default)
- Xcode command line tools and CMake (`brew install cmake`)

```bash
export PATH=$HOME/.dotnet:$PATH
```

## 1. Patch to CUE4Parse-Natives

Apple clang 17+ treats `-Wmissing-template-arg-list-after-template-kw` as an error
in the ACL headers. It's already patched in
`upstream/CUE4Parse/CUE4Parse-Natives/CMakeLists.txt` (that warning is disabled
with Clang). Without the patch, CUE4Parse's `dotnet build` fails.

## 2. Build Oodle for macOS arm64

OodleUE doesn't publish macOS binaries, but its code builds unchanged:

```bash
cd oodle-src/build
cmake -S . -B builds/macos -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++
cmake --build builds/macos --config Release -j 10
```

Result: `builds/macos/lib/liboodle-data-shared.dylib`. The PoC's `.csproj`
copies it next to the executable if it exists.

## 3. Build and run

```bash
cd poc/FModel.Poc
dotnet build -c Release
dotnet bin/Release/net10.0/FModel.Poc.dll --help
```

Example with CUE4Parse's fixtures (encrypted IoStore + Oodle + unversioned):

```bash
F=../../upstream/CUE4Parse/CUE4Parse.Tests/Fixtures/UE5_8
dotnet bin/Release/net10.0/FModel.Poc.dll $F/IoStore/Unversioned/OodleEncrypted \
  --global $F/IoStore/Unversioned/global.utoc \
  --usmap $F/Mappings/CUE4ParseFixtures-Oodle.usmap \
  --aes-ascii CUE4ParseFixtureAESKey0123456789 \
  --export T_BC7.uasset --export SK_Fixture.uasset --read-all
```

With a real game (the `.pak`/`.utoc` files are in `<Game>/Content/Paks`):

```bash
dotnet bin/Release/net10.0/FModel.Poc.dll "/path/to/Game/Content/Paks" \
  --game GAME_UE5_4 --aes 0xYOUR_KEY --usmap /path/to/mappings.usmap --read-all
```

`--oodle managed` forces the C# decompressor (OodleSharp) for comparison.

## Results (macOS 26.6.2, Apple Silicon, .NET 10.0.12)

- CUE4Parse test suite: **211 passed, 0 failed, 1 skipped** (also skipped upstream).
- Natives: `CUE4Parse-Natives.dylib` (ACL) and `liboodle-data-shared.dylib` load fine.
- Encrypted IoStore + Oodle + mappings: mounts, decompresses 111/111 files and exports
  BC1/BC5/BC7/FloatRGBA/streaming textures to correct PNGs, plus meshes,
  blueprints and materials to JSON.
- Native and managed Oodle produce **identical bytes** (same SHA-256) on Pak,
  IoStore and partitioned IoStore. Native was 2x to 30x faster on these
  small fixtures; it still needs to be measured with a real game.
