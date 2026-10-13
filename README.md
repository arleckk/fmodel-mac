# FModelMac

macOS (Apple Silicon) port of [FModel](https://github.com/4sval/FModel) on top of
[CUE4Parse](https://github.com/FabianFG/CUE4Parse), with an Avalonia UI.

| Folder | What |
|---|---|
| `app/` | The app (`FModelMac`), the test harness (`FModelMac.Snapshot`) and packaging scripts. See `app/README.md`. |
| `poc/` | The first proof of concept (CUE4Parse + Oodle on macOS). See `poc/README.md`. |
| `patches/` | Our changes to CUE4Parse, applied on top of the pinned commit. |
| `scripts/` | `setup-upstream.sh` recreates the untracked folders; `save-patches.sh` refreshes the patch. |

Not tracked (recreated by `scripts/setup-upstream.sh`):

- `upstream/`: FModel at `d615497` with the CUE4Parse submodule at `3fc091a`, plus `patches/CUE4Parse.patch`.
- `oodle-src/`: WorkingRobot/OodleUE at `5e38cb6`, built for macOS arm64.
- `vendor/`: vgmstream and its dependencies (`--with-vgmstream`, or `app/packaging/build-vgmstream.sh`).

```bash
scripts/setup-upstream.sh --with-vgmstream
app/packaging/build-app.sh
```

After changing anything under `upstream/CUE4Parse`, run `scripts/save-patches.sh` and commit the patch.

## License and credits

FModelMac is an **unofficial** port of [FModel](https://github.com/4sval/FModel). It is not affiliated
with the FModel authors, Epic Games or any game publisher. As a derivative of FModel it is licensed
under the **GNU GPL v3.0** (see [LICENSE](LICENSE)); third-party components and their licenses are
listed in [NOTICE](NOTICE).

- [FModel](https://github.com/4sval/FModel) (GPL-3.0): the features, interface and resources (icons,
  syntax definitions) this port is based on.
- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (Apache-2.0): reads the game files; our changes
  are in `patches/CUE4Parse.patch`.
- Oodle is proprietary (Epic Games Tools) and isn't in this repository; an app built with it is for
  personal use and must not be redistributed.
