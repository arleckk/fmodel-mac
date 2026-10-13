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
