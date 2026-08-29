# OmenTools integration

OCNFarmer references a vendored OmenTools source snapshot through
`dependencies/OmenTools/OmenTools.csproj`.

## Current snapshot

- Upstream: https://github.com/AtmoOmen/OmenTools
- Branch: `main`
- Commit: `27fce383fea63eedcbde92fca4b81f6e8886be4e`
- Snapshot date: 2026-08-20
- License: MIT; see `dependencies/OmenTools/LICENSE`
- SDK: `Dalamud.CN.NET.Sdk/15.0.0`
- Runtime dependencies: `GuerrillaNtp.dll` 3.1.0 and `TinyPinyin.dll` 1.1.0

## Build and package

Run:

```powershell
.\Build-Release.ps1
```

The script creates `bin/Release/latest.zip` from an explicit allowlist. The
archive must contain exactly:

- `NorthIslandChestPlugin.dll`
- `NorthIslandChestPlugin.json`
- `OmenTools.dll`
- `GuerrillaNtp.dll`
- `TinyPinyin.dll`
- `OmenTools.LICENSE`

Dalamud, FFXIVClientStructs, Lumina, Newtonsoft.Json, and other Dalamud-shipped
assemblies must not be bundled.

## Runtime activation

OCNFarmer initializes OmenTools with an explicit allowlist. Only
`GamePacketManager` is started for the headless currency exchange flow; all
other OmenTools services remain disabled.

When a feature starts using OmenTools services:

1. Call `DService.Init(pluginInterface, options)` once in the plugin constructor.
2. Add a service to `EnableOnly(...)` only when a feature actually requires it.
3. Call `DService.Uninit()` once during `Dispose()` after feature work has stopped.
4. Keep direct packet sending behind game-version checks and in-game validation.
5. Re-run `Build-Release.ps1` and verify the package on the current Dalamud CN build.

Do not copy `Global/GlobalUsing.OmenTools.cs` wholesale unless the project is
ready to accept all aliases and namespace imports it defines.
