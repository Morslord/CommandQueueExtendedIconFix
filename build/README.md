# CommandQueueExtended Icon Fix

## Replaced by CommandQueueRemastered

**This mod has been superseded by [CommandQueueRemastered](https://thunderstore.io/c/riskofrain2/p/Morslord/CommandQueueRemastered/) and is being retired. Please use CommandQueueRemastered for new installations.**

CommandQueueRemastered replaces **CommandQueueExtended**, **CommandQueueExtendedIconFix** and **ProperSave_CommandQueueExtended**, combining their functionality in one mod. This separate Icon Fix is no longer needed when using Remastered.

### How to switch

1. Disable or remove **CommandQueueExtended** and **CommandQueueExtendedIconFix** from your mod manager profile.
2. If installed, also disable or remove **ProperSave_CommandQueueExtended**, which Remastered replaces as well. Keep **ProperSave** itself if you use it for run saves.
3. Install [**CommandQueueRemastered**](https://thunderstore.io/c/riskofrain2/p/Morslord/CommandQueueRemastered/) through your Thunderstore-compatible mod manager.

Existing queues and presets from the older mods are not imported by Remastered; recreate them after switching.

**Version 1.2.1 only updates the README, changelog and package manifest. The plugin DLL and its behavior are unchanged from 1.2.0.**

---

## Legacy documentation

The information below describes the old Icon Fix for users who continue using CommandQueueExtended.

Fixes the missing/white item icons in **CommandQueueExtended** for Risk of Rain 2 and adds helpful information to its **available-item selection grid**.

## Features

- **Item icons work again:** Displays the correct item pictures in the Command Queue.
- **Item descriptions on hover:** See an item's description without leaving the Command Queue. With [LookingGlass](https://thunderstore.io/c/riskofrain2/p/DropPod/LookingGlass/) installed, tooltips also include its more detailed stats.
- **See how many you own:** Every available item shows `x0`, `x1`, `x2`, etc. These numbers show your survivor's **current inventory**, not how many items you have queued. `x0` is grey and owned items are white.
- **Choose where tooltips appear:** Show descriptions at the **bottom center** of the screen or next to your **cursor**.
- **Optional in-game settings:** With [RiskOfOptions](https://thunderstore.io/package/Rune580/Risk_Of_Options/) installed, change both tooltip settings in-game, even during a run.

The original queue management, item selection, and saving features are preserved. Only the **available-item grid** is changed; the upper row of queued items is left alone.

## Settings

With RiskOfOptions installed, open:

**Settings → MOD OPTIONS → CommandQueueExtended Icon Fix → Tooltips**

| Option | Default | What it does |
| --- | --- | --- |
| **Tooltip Position** | **BottomCenter** | **BottomCenter:** Shows the description near the bottom center so it doesn't cover the item grid. **Cursor:** Shows it near your mouse pointer. |
| **Ignore Enable Inspect** | **Off** | **Off:** Follows the game's Enable Inspect option. **On:** Shows Command Queue descriptions even when Enable Inspect is turned off in the game. |

Both options change **immediately**, including mid-run. Turning on **Ignore Enable Inspect** does **not** change the game's setting or enable item inspection elsewhere. The owned-item counters always stay visible.

The checkbox may use the translated game name for *Enable Inspect* if available.

## Without RiskOfOptions

You can still change the two settings in:

```text
BepInEx/config/local.commandqueue.iconfix.cfg
```

```ini
[Tooltips]
Position = BottomCenter
IgnoreEnableInspect = false
```

Use `Cursor` for mouse-following descriptions. Set `IgnoreEnableInspect = true` to display Command Queue tooltips independently of the game's Enable Inspect setting.

## Installation and compatibility

For new installations, use [CommandQueueRemastered](https://thunderstore.io/c/riskofrain2/p/Morslord/CommandQueueRemastered/). The following requirements apply only to the legacy Icon Fix: **CommandQueueExtended 2.0.0** is required and is listed as a dependency. The mod targets **Risk of Rain 2 v1.5.0** and **BepInEx 5**.

**Optional mods:**

- [RiskOfOptions](https://thunderstore.io/package/Rune580/Risk_Of_Options/) — adds the in-game settings menu.
- [LookingGlass](https://thunderstore.io/c/riskofrain2/p/DropPod/LookingGlass/) — provides enhanced item descriptions and calculated stats.

The icon fix, tooltips, and inventory counters still work without either optional mod. Without LookingGlass, descriptions use the normal game text.

## Source and feedback

Source code, technical details, and build instructions: [GitHub — CommandQueueExtended Icon Fix](https://github.com/Morslord/CommandQueueExtendedIconFix/).

This addon runs alongside CommandQueueExtended; it does not replace the original mod or change saved queues.
