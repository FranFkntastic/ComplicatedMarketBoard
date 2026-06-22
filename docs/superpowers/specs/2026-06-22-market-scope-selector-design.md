# Market Scope Selector Design

## Goal

Replace hand-typed additional world, data center, and region settings with a structured picker, make the main market scope dropdown easier to scan by grouping worlds under their data centers, and support custom market scopes made from specific regions, data centers, and worlds.

## Current Behavior

The plugin stores market scopes as strings:

- `P.Config.selectedWorld` is the active Universalis scope.
- `P.Config.AdditionalWorlds` contains extra region, data center, or world names.

The config window currently asks users to type those names manually. The main selector flattens region, data center, home world, additional worlds, and home data center worlds into one list. Manually added entries are marked with `*`.

This is compact, but it is fragile and hard to discover. It also cannot represent richer comparisons like `current world plus Europe` or `these specific worlds across two data centers` as a single reusable scope.

## Design

Keep the existing string config contract for normal region, data center, and world scopes, but add a typed custom scope model for composed selections. Replace the UI and list-building logic with a typed market scope catalog.

Each known scope will have:

- `Name`: canonical stored/query value used by config and selector state.
- `DisplayName`: user-facing label.
- `Kind`: `Region`, `DataCenter`, or `World`.
- `RegionName`: canonical region name.
- `DataCenterName`: canonical parent data center for worlds.
- `IsHomeWorld`: whether this is the player or configured home world.
- `IsAdditional`: whether it is directly included by settings.

Known regions, data centers, and worlds are built from Lumina world data where possible. Regions are normalized to plugin display names, with Universalis API normalization handled separately. For example, the UI should display `North America`, while API requests normalize it to `North-America`.

Custom scopes will have:

- `Name`: user-facing custom scope name.
- `IncludedScopes`: canonical region, data center, and world names selected by the user.
- `ExpandedWorlds`: the concrete world list derived from `IncludedScopes`.
- `QueryTargets`: the minimal region, data center, and world target list used for Universalis requests.

Custom scopes are user-defined groups. They may include broad scopes and individual worlds at the same time, such as:

- Current world plus Europe.
- Specific worlds from Aether and Primal.
- One full data center plus several worlds from another data center.
- A full region plus one local comparison world.

For Universalis requests, a custom scope expands into one or more normal Universalis requests and merges the responses client-side. Region and data center inclusions should be queried as their broad scope targets. Individual world inclusions should be queried as world targets. This avoids relying on unsupported comma-separated world targets while still letting the plugin represent mixed scopes that Universalis region and data center endpoints cannot express directly.

## Additional Scope Settings

The `Additional Worlds/DCs/Regions` config section becomes a structured picker instead of raw text fields.

The picker groups checkboxes like this:

```text
Regions
  [ ] Japan
  [x] North America
  [ ] Europe
  [ ] Oceania

Data Centers
  North America
    [x] Aether
      [x] Adamantoise
      [x] Cactuar
      [x] Faerie
      [x] Gilgamesh
      [x] Jenova
      [x] Midgardsormr
      [x] Sargatanas
      [x] Siren
    [ ] Crystal
    [ ] Dynamis
    [ ] Primal
```

Checking a data center makes every world under that data center visible in the main market scope selector. Nested world entries may then be unchecked individually.

This produces three settings states:

- A checked region adds the region scope.
- A checked data center adds the data center scope and immediately adds all of its worlds as explicit checked world entries.
- Unchecking a world under a checked data center removes that world entry, while leaving the data center scope checked.

To keep the simple global selector behavior easy to understand, normal additional scopes still store explicit inclusions in `AdditionalWorlds`. Checking a data center writes the data center name plus all of its current world names into `AdditionalWorlds`. Unchecking a nested world removes that world name from `AdditionalWorlds`. The data center stays available as a cross-world query target as long as its own name remains checked.

This means newly added worlds would not appear under an already-saved data center until the user toggles that data center again. That is acceptable for this release because worlds rarely change and the config remains easy to understand.

Custom scopes use their own config collection instead of overloading `AdditionalWorlds`.

```csharp
public List<CustomMarketScope> CustomMarketScopes = [];

public class CustomMarketScope
{
    public string Name { get; set; } = "";
    public List<string> IncludedScopes { get; set; } = [];
}
```

`IncludedScopes` stores the user-selected canonical region, data center, and world names. The concrete world list is derived when the selector is built or a request is made.

Legacy hand-typed values should be replaced wherever possible:

- `North-America` becomes `North America` in config.
- Case-insensitive world and data center names become their canonical names.
- Unknown entries are not silently used. They appear in a small `Unknown saved entries` section with delete controls.

## Custom Scope Settings

The settings UI gets a `Custom scopes` section under `Additional Worlds/DCs/Regions`.

Each custom scope row shows:

- Name field.
- Edit button.
- Delete button.

Editing opens a picker using the same region, data center, and world tree as the additional-scope picker. The user can check broad scopes and individual worlds together. The UI should show a compact summary, such as `18 worlds from Europe, Siren`.

Custom scope expansion rules:

- A region expands to all public worlds in that region.
- A data center expands to all public worlds in that data center.
- A world expands to itself.
- Duplicate worlds are removed.
- The current/home world can be included explicitly.
- Unknown saved entries are shown inside the editor and can be removed.

The first implementation should not add custom scope arithmetic beyond inclusion. No negative or excluded scopes are required. If the user wants `Europe except one world`, they can select the relevant data centers and worlds directly.

## Main Market Scope Dropdown

The main selector becomes grouped:

```text
Regions
  North America
  Europe*

Data Centers
  Aether*
    Adamantoise*
    Cactuar*
    Faerie*
    Gilgamesh*
    Jenova*
    Midgardsormr*
    Sargatanas*
    Siren*
  Crystal
  Dynamis
  Primal

Custom Scopes
  My World + Europe
  Raid Sales Watchlist
```

Rules:

- Always show the player's home region and home data center.
- Always show all worlds under every visible or checked data center.
- Show checked regions and data centers from settings.
- Show individually checked worlds under their parent data center.
- Show custom scopes in their own section.
- Indent worlds under their parent data center.
- Keep the home world highlight.
- Keep `*` or a similarly compact marker for settings-added scopes.

Selecting a region, data center, world, or custom scope still updates the active selector state and refreshes the current item through the existing flow. Because custom scopes are not representable as a single normal `selectedWorld` string, the implementation should add a separate active custom-scope identifier while keeping `selectedWorld` for normal scopes.

## API Normalization

Visible names should be friendly and consistent. API names should be normalized at the Universalis boundary.

Required mapping:

```text
North America -> North-America
```

All other known region, data center, and world names should pass through unchanged unless Universalis requires a specific spelling.

Custom scopes should normalize to a distinct list of normal Universalis query targets. If a custom scope includes Europe and Siren, it should query Europe and Siren separately, then merge listings, sale history, upload times, and aggregate counts.

## Risks

The main risk is config compatibility. Existing users may have typed values with casing differences, hyphen differences, or typos. The implementation should canonicalize known values and expose unknown values for deletion.

The second risk is over-expanding the main selector. Showing every world under every checked data center is intentional, but the dropdown must use headers and indentation so it remains readable.

The third risk is custom-scope request shape. Region and data center endpoints return naturally grouped cross-world data, while custom scopes may require multiple requests and response merging. The implementation should keep request target construction and response merging centralized so normal and custom scopes do not drift.

## Verification

Manual verification should cover:

- Existing `North-America` config value becomes visible as `North America`.
- Checking a region adds it to the main selector.
- Checking a data center adds the data center and all nested worlds to the main selector.
- Unchecking one nested world removes it from the main selector without removing the data center.
- Selecting a region, data center, and world each triggers a refresh.
- Selecting a custom scope triggers a refresh using its expanded world list.
- A custom scope containing the current world plus Europe returns data for both the current world and European worlds.
- A custom scope containing specific worlds from multiple data centers returns only those worlds.
- North America region requests still hit the Universalis `North-America` endpoint.
- Custom scopes use multiple normal Universalis requests and merge the returned data rather than pretending to be a region or data center.
- Unknown saved entries are visible and removable.
