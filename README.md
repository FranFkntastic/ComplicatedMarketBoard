# ComplicatedMarketBoard

ComplicatedMarketBoard is a compact Dalamud market board helper for FFXIV.

It builds on the idea of Simple Market Board with a few extra conveniences: quick item lookup, sortable tables, configurable market scopes, and clearer Universalis freshness information.

## Installation

ComplicatedMarketBoard is distributed through a custom Dalamud plugin repository.

1. Open `/xlsettings` in game.
2. Go to `Experimental`.
3. Add this custom plugin repository URL:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

4. Save the settings.
5. Open `/xlplugins`.
6. Search for `ComplicatedMarketBoard` and install it.

Updates will appear through Dalamud as long as the custom repository remains enabled.

## Features

- Search market data from hovered items, hotkeys, clipboard, or the built-in item search bar.
- View current listings and sale history in a compact in-game window.
- Sort and resize market tables.
- Compare prices by world, data center, or region.
- Add extra worlds, data centers, and regions to the market selector.
- See retainer names, buyer names, market freshness, and market velocity when available.
- Filter displayed rows to HQ items.
- Keep a local history of recently checked items.

## Basic Use

Open the plugin from `/xlplugins`, a configured window hotkey, or by triggering an item search.

Common ways to search:

- Hover an item.
- Use the configured search hotkey while hovering an item.
- Type an item name in the plugin search bar.
- Use the item icon action to search from clipboard.

Use the world menu to choose where market data should come from. Additional worlds, data centers, and regions can be enabled from the config window.

## Notes

ComplicatedMarketBoard uses public Universalis market data, so results depend on public upload freshness.

This plugin is a fork of Elypha's Simple Market Board.

## License

ComplicatedMarketBoard is licensed under GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).
