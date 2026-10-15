# Changelog

## 1.2.1

### Documentation and package metadata
- Added a retirement notice and a link to [CommandQueueRemastered](https://thunderstore.io/c/riskofrain2/p/Morslord/CommandQueueRemastered/) in the README.
- Clarified that CommandQueueRemastered replaces CommandQueueExtended, CommandQueueExtendedIconFix and ProperSave_CommandQueueExtended.
- Added instructions for switching to Remastered and retained the previous documentation for legacy users.
- Updated the package description and website URL to direct users to CommandQueueRemastered.
- No plugin code or behavior changes. The DLL and dependency on CommandQueueExtended are unchanged from 1.2.0.

## 1.2.0

### Added
- Item tooltips for the Command Queue selection grid.
- LookingGlass integration for enhanced item descriptions.
- Inventory counters on item icons (`x0`, `x1`, etc.).
- Distinct counter colors: grey for unowned items, white for owned items.
- Configurable tooltip positioning (`BottomCenter` or `Cursor`).
- `Ignore Enable Inspect` option to display tooltips independently of the game's Inspect setting.
- Optional RiskOfOptions integration with live configuration changes and mod icon support.

### Improved
- Tooltip lifecycle handling to prevent descriptions from disappearing while hovering.
- Compatibility with Risk of Rain 2's native tooltip system.
- Reduced diagnostic logging and improved error handling.

### Documentation
- Updated GitHub README with the complete v1.2.0 feature set.
- Added a separate player-focused Thunderstore README.
- Updated the mod manifest and version information.

All existing item icon fixes remain intact. LookingGlass and RiskOfOptions are optional dependencies.