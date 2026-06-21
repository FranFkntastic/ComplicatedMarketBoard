# Sortable Market Tables

## Goal

Make the two primary market grids sortable by column:

- Current listings
- Recent sale history

The change should keep the current compact layout and existing defaults while making column headers clickable.

## Current State

Both grids are rendered with the older `ImGui.Columns` API. They look like tables, but they do not have real table headers or native sorting behavior.

Current hardcoded sorting:

- Listings: price per unit ascending
- History: sale timestamp descending

HQ filtering happens before sorting and should continue to do so.

## Proposed Shape

Convert only the listings and history grids to `ImGui.BeginTable`.

Keep sort state local to `MainWindow`:

- Listing sort column and direction
- History sort column and direction

Do not persist sort state in plugin config for the first version. The default sort should reset on plugin reload and match today's behavior.

## Sortable Columns

Listings:

- Selling: `PricePerUnit`
- Q: `Quantity`
- Total: computed row total, respecting the `TotalIncludeTax` setting
- World: displayed world name

History:

- Sold: `PricePerUnit`
- Q: `Quantity`
- Date: `Timestamp`
- World: displayed world name

## Defaults

- Listings default to `Selling` ascending.
- History defaults to `Date` descending.

Clicking an active header flips its direction. Clicking another header selects that column and starts with its natural default direction.

Natural defaults:

- Numeric listing columns: ascending
- World columns: ascending
- History date: descending

## Known Tradeoffs

Selection remains index-based. After changing sort order, a highlighted row may no longer represent the same underlying listing/history entry. That is acceptable for this pass because selection is currently visual only.

The table header row may differ slightly from the old cyan text headers. This is acceptable if the compact layout remains intact.

The world freshness mini-table is left unchanged.

## Verification

Build Debug and let Dalamud auto-reload:

```powershell
dotnet build ComplicatedMarketBoard.sln -c Debug -p:UseSharedCompilation=false
```

In game:

1. Open `/cmb`.
2. Load an item with listing and history data.
3. Click each listing header twice.
4. Click each history header twice.
5. Toggle HQ filtering and verify filtering still happens before sorting.
6. Toggle tax inclusion and verify listing `Total` sort follows the visible total.
