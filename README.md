# CommandQueueExtended Icon Fix

A small client-side compatibility addon for [CommandQueueExtended](https://thunderstore.io/c/riskofrain2/p/gabrielgad/CommandQueueExtended/) in **Risk of Rain 2**.

CommandQueueExtended's available-item grid could display blank or white squares because it used outdated `PickupDef.iconSprite` references. This addon restores the item icons and adds hover descriptions and owned-item counters to that grid. It runs alongside CommandQueueExtended and **does not modify or replace `CommandQueue.dll`**.

## Features

- **Correct item icons:** Uses Risk of Rain 2's native `RoR2.UI.ItemIcon` component instead of obsolete sprite assignments.
- **Item descriptions:** Hover over an available item to see its tooltip. Choose `BottomCenter` (default) or `Cursor` placement. By default, tooltips follow the game's **Enable Inspect** setting.
- **Independent inspect override:** **Ignore Enable Inspect** lets Command Queue tooltips appear even when the game's Enable Inspect setting is off, without changing that setting elsewhere in the game.
- **LookingGlass integration (optional):** Uses [LookingGlass](https://github.com/Wet-Boys/LookingGlass/)'s enhanced item descriptions and calculated stats when available, otherwise falls back to vanilla descriptions.
- **Owned-item counters:** Each available item shows your survivor's inventory count (`x0`, `x1`, etc.), regardless of Enable Inspect. `x0` is grey (50% brightness); items you own are shown in white. Counts reflect **owned items**, not items queued for a future pickup.
- **RiskOfOptions integration (optional):** Change both tooltip settings from the **MOD OPTIONS** menu. Changes apply immediately, including during a run, and are saved to the BepInEx configuration.
- **Preserves queue behavior:** Item selection, removal, queue ordering, saving/loading, and the existing queued-item row remain managed by CommandQueueExtended.

Only the **lower available-item selection grid** receives the new counters and hover behavior; the queued items in the upper row are not altered by these features.

## Requirements and compatibility

| Component | Version / notes |
| --- | --- |
| Risk of Rain 2 | Targeted and tested with **1.5.0#1210** |
| [CommandQueueExtended](https://thunderstore.io/c/riskofrain2/p/gabrielgad/CommandQueueExtended/) | **2.0.0** (required) |
| BepInEx | **5**, with CommandQueueExtended's usual dependencies |
| [RiskOfOptions](https://thunderstore.io/package/Rune580/Risk_Of_Options/) | Optional; in-game settings tested with **2.8.6** |
| [LookingGlass](https://thunderstore.io/c/riskofrain2/p/DropPod/LookingGlass/) | Optional; enhanced descriptions tested with **1.16.1** |
| r2modman | Recommended for installation and profile management |

**Test status:** The original icon fix and the additional tooltip, counter, and in-game settings features have been validated in-game during development. The v1.2.0 source cleanup has not yet been independently tested in-game.

## Installation

1. Install **CommandQueueExtended** in your Risk of Rain 2 r2modman profile.
2. Install **RiskOfOptions** if you want to change settings from the in-game menu, and **LookingGlass** if you want more detailed item descriptions. Both are optional.
3. Install the released mod package through r2modman, or build/download `CommandQueueIconFix.dll` and place it in your active profile at:

   ```text
   BepInEx/plugins/CommandQueueExtendedIconFix/CommandQueueIconFix.dll
   ```

4. Ensure there is only one `CommandQueueIconFix.dll` under that profile's `BepInEx/plugins` directory. Launch the game with **Start modded**.

**Do not replace `CommandQueue.dll`.** For a manual installation, place your mod's `icon.png` beside `CommandQueueIconFix.dll` if it was not embedded in the DLL and you want the icon to appear in RiskOfOptions.

## In-game settings (RiskOfOptions)

Open **Settings > MOD OPTIONS > CommandQueueExtended Icon Fix > Tooltips**.

| Setting | Default | Effect |
| --- | --- | --- |
| **Tooltip Position** | `BottomCenter` | `BottomCenter` keeps the tooltip near the bottom center of the screen. `Cursor` uses the game's normal pointer-following position. |
| **Ignore Enable Inspect** | `false` (off) | When on, Command Queue tooltips are displayed even if the game's Enable Inspect setting is off. This does not enable inspect elsewhere. |

Both options apply **without a restart**, including mid-run. The second option tries to reuse the translated in-game name for *Enable Inspect* when an appropriate language token is available, otherwise it uses the English name.

The `xN` counters are always present and are not affected by either tooltip setting.

## BepInEx configuration

The same settings are available without RiskOfOptions. BepInEx creates this file after launching the plugin:

```text
BepInEx/config/local.commandqueue.iconfix.cfg
```

```ini
[Tooltips]
Position = BottomCenter
IgnoreEnableInspect = false
```

Set `Position = Cursor` or `IgnoreEnableInspect = true` as desired. RiskOfOptions uses these **same** `ConfigEntry` values, so in-game changes are saved. When editing the `.cfg` externally, reload it through BepInEx or restart the game.

## How it works

The original CommandQueueExtended selection-grid callbacks still assign `PickupDef.iconSprite` to a plain `UnityEngine.UI.Image`. The plugin's targeted Harmony transpiler redirects those assignments to a helper that clones the game's `ItemIcon` prefab from `HUDSimple`. Existing choice buttons, raycast/click behavior, and queue logic are preserved, with a fallback to the original image if icon creation fails.

For the lower-grid buttons, the plugin adds a native `TooltipProvider`, a transparent pointer target, and a separate TextMeshPro overlay for owned-item counts. Tooltips use RoR2's `TooltipController` and `TooltipProvider.SetContent()`; optional LookingGlass descriptions are obtained through reflection so LookingGlass remains an optional dependency.

The native tooltip lifetime patch prevents a hovered Command Queue tooltip from being removed by an unrelated selection change. A bounded recovery path handles unexpected tooltip-manager resets without creating a custom tooltip window; it avoids replacing a different, currently active native tooltip. Only providers attached to Command Queue's available-item buttons are protected.

When installed, RiskOfOptions is discovered at runtime and receives `ChoiceOption` and `CheckBoxOption` bindings to the plugin's existing BepInEx configuration entries. It is a **soft dependency**: the mod still builds and runs without `RiskOfOptions.dll`.

## Building from source

Requires the .NET SDK (8 or later), your Risk of Rain 2 installation, and an r2modman profile with BepInEx 5. The project targets **.NET Standard 2.1** and does not need the Unity Editor.

```powershell
$env:ROR2_GAME_DIR = 'C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2'
$env:ROR2_PROFILE_DIR = Join-Path $env:APPDATA 'r2modmanPlus-local\RiskOfRain2\profiles\CommandQueue'
dotnet build -c Release
```

You can alternatively pass `-p:GameDir=...` and `-p:ProfileDir=...`. The project references the game's `Unity.TextMeshPro.dll`, `com.unity.multiplayer-hlapi.Runtime.dll`, and `UnityEngine.ImageConversionModule.dll` in addition to its normal RoR2/BepInEx dependencies. There is no compile-time reference to RiskOfOptions or LookingGlass.

The `icon.png` resource is embedded automatically when present beside `CommandQueueIconFix.csproj` during the build. Otherwise the plugin looks for `icon.png` next to its installed DLL at runtime when registering its optional RiskOfOptions icon. The source archive intentionally does not contain your icon artwork.

## GitHub vs. Thunderstore README

This `README.md` is the **GitHub/developer** documentation. `README_THUNDERSTORE.md` is the shorter player-facing document. When making the **Thunderstore distribution ZIP**, put the content of `README_THUNDERSTORE.md` into a file named `README.md` **at the ZIP root**, alongside `manifest.json` and your **256×256** `icon.png`, and include the compiled plugin DLL. The source ZIP itself is **not** a ready-to-install Thunderstore package.

## Troubleshooting

If you don't see the settings in MOD OPTIONS, verify that RiskOfOptions is enabled. The addon itself still functions and can be configured through its `.cfg` file without RiskOfOptions.

If icons or tooltips fail, check `BepInEx/LogOutput.log` for **CommandQueueExtended Icon Fix** warnings/errors and confirm that CommandQueueExtended 2.0.0 is enabled. Normal hovering no longer prints detailed diagnostic lines; configuration changes and important setup failures are still logged.

## Credits

- [gabrielgad](https://thunderstore.io/c/riskofrain2/p/gabrielgad/CommandQueueExtended/) — CommandQueueExtended.
- KubeRoot — original CommandQueue project.
- [Rune580 / RiskOfOptions](https://github.com/Rune580/RiskOfOptions) — optional in-game settings UI.
- [Wet-Boys / LookingGlass](https://github.com/Wet-Boys/LookingGlass/) — optional enhanced item descriptions.

This is an independent compatibility addon, not affiliated with the game developers or the authors of the listed mods.

## Release

### 1.2.0 — Tooltips, counters and in-game settings

**Changes since the published 1.0.0 release:**

- **Hover item descriptions:** Added native tooltips to the available-item grid. Tooltips follow the game's **Enable Inspect** setting by default.
- **Tooltip positioning:** Choose **BottomCenter** (default) or **Cursor**.
- **Independent inspect override:** Added **Ignore Enable Inspect**, allowing Command Queue tooltips even when the game's inspect feature is turned off, without enabling it elsewhere.
- **Optional LookingGlass support:** Shows enhanced item descriptions and calculated stats when LookingGlass is available, with vanilla descriptions as a fallback.
- **Owned-item counters:** Displays `x0`, `x1`, and higher counts on the available-item grid. Unowned items (`x0`) appear grey; owned items appear white. These are inventory counts, not queue counts.
- **Optional RiskOfOptions integration:** Added an in-game dropdown and checkbox, saved through BepInEx and effective immediately—even mid-run. Registers the mod icon when its `icon.png` is available.
- **More reliable tooltips:** Keeps the native tooltip visible while hovering and prevents the Command Queue tooltip handling from interfering with unrelated tooltips.
- **Release cleanup:** Reduced diagnostic log noise while retaining useful warnings and error handling. The original icon fix and CommandQueueExtended's queue functionality remain intact.

### 1.0.0 — Initial release

- Restored item icons in CommandQueueExtended's available-item grid by using Risk of Rain 2's native `ItemIcon` instead of outdated `PickupDef.iconSprite` assignments, without modifying `CommandQueue.dll`.
