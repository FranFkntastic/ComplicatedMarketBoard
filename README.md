# ComplicatedMarketBoard

ComplicatedMarketBoard is a Dalamud market board helper for FFXIV players who want faster item lookup, richer Universalis data, and more control over which markets they compare.

It is distributed through a custom Dalamud plugin repository.

## Installation

1. Open `/xlsettings` in game.
2. Go to `Experimental`.
3. Add this custom plugin repository URL:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

4. Save and close settings.
5. Open `/xlplugins`.
6. Search for `ComplicatedMarketBoard`.
7. Install the plugin.

Future updates will appear through Dalamud as long as this custom repository remains enabled.

## Highlights

- Search item prices from hover, hotkey, clipboard, or the built-in item search bar.
- Compare market data by world, data center, region, or custom mixed market scope.
- Create custom scopes such as a home world plus another region, or selected worlds across multiple data centers.
- Sort current listings and sale history by table column.
- Resize table columns and rows to fit your preferred layout.
- View retainer names on active listings and buyer names in sale history.
- Show market freshness with precise min, average, and max age details.
- See market velocity as a quick signal for recent sales activity.
- Filter displayed rows to HQ items, and optionally request HQ-only Universalis data.
- Keep a local search history cache for recently checked items.

## Basic Use

The plugin window can be opened from `/xlplugins`, the configured window hotkey, or automatically when a search is triggered.

Common search flows:

1. Hover an item.
2. Hold or press the configured search hotkey while hovering an item.
3. Use the item search bar in the plugin window.
4. Copy an item name, then use the item icon action to start a search from clipboard.

The current item name and icon appear at the top of the window. A progress bar appears while market data is refreshing.

## Market Scope

The world menu controls where market data is fetched from.

Supported scope types:

- World
- Data center
- Region
- Custom scope

The settings window lets you choose additional regions, data centers, and worlds from a collapsible list. Checking a data center also checks its worlds, and individual worlds can be unchecked afterward.

Custom scopes let you build reusable comparisons from multiple markets. Examples:

- Your current world plus Europe.
- Selected worlds from Aether and Primal.
- A full data center plus a few individual worlds elsewhere.

Custom scopes may require multiple Universalis requests, so very large custom scopes can take longer to refresh.

## Tables

Current listings and sale history support:

- Clickable column sorting.
- Resizable columns.
- Adjustable row height.
- Optional tax-inclusive totals.
- HQ highlighting.
- Vendor-price highlighting when applicable.

Active listings include retainer names when Universalis provides them. Sale history includes buyer names when available.

## Market Freshness

The freshness table shows how old the market data is for each world in the selected scope.

Hover the market status bar for exact details:

- Fetch time.
- Newest upload time.
- Min, average, and max freshness.
- Freshest and stalest markets.
- Listing and recent sale counts.

Freshness values depend on public Universalis uploads. If a world looks stale, visiting that market board with an uploader enabled can help refresh public data.

## Market Velocity

Market velocity is a Universalis sales activity value. Higher velocity generally means more recent sales activity for the item in the selected market scope.

It is useful as a quick signal, not a guarantee of profit.

## Controls

Top-left buttons:

- Refresh: fetch fresh market data for the current item.
- HQ filter: toggle HQ-only display.
- Config: open settings.

Right-side buttons:

- List: switch between history and stats.
- Delete: remove the selected cache entry.
- Ctrl + Delete: clear cached entries.

Item icon actions:

- Click: copy the current item name.
- Ctrl + Click: search from the current clipboard item name.

## Configuration

The config window includes options for:

- Search hotkeys and hover delay.
- Whether search can happen while the window is hidden.
- Home world override.
- Additional market scopes.
- Custom market scopes.
- Chat and toast notifications.
- Table layout, spacing, column widths, and row height.
- Main window and world menu sizing.
- Request timeout and Universalis listing/history limits.
- Theme options.

## Notes

ComplicatedMarketBoard uses Universalis public market data. Data quality and freshness depend on public uploads.

This plugin is a fork of Elypha's Simple Market Board and keeps the same spirit of a compact market helper while adding more power-user controls.
