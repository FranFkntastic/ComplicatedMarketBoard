# ComplicatedMarketBoard

![2024-02-05_01-30-52_337](https://github.com/Elypha/SimpleMarketBoard/assets/30290883/767325f5-a9d9-4cdd-bb70-25ea8c1818da)

## Introduction

A less simple, even more ultimate market board plugin caters for ME at present, and power users who have the same requirements as me.

## Installation

ComplicatedMarketBoard is distributed through a custom Dalamud plugin repository, not the default Dalamud plugin list.

In game:

1. Open `/xlsettings`.
2. Go to `Experimental`.
3. Add this URL under `Custom Plugin Repositories`:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

4. Save and close settings.
5. Open `/xlplugins`.
6. Search for `ComplicatedMarketBoard` and install it.

Future updates, and any future FranFkntastic plugins published through the same repository, will appear through Dalamud as long as the custom repository remains enabled. For release packaging details, see [docs/custom-plugin-repository.md](docs/custom-plugin-repository.md).

### Features

- **Compact UI design**, that won't eat out your screen even on your grandma's 800x600 CRT
- **Search history**, and with cache for review when you work with multiple items and a button to delete any entry
- **Flexible Keybindings**, so long as you can press it on your keyboard
- **More data, more money**
    - Universalis Last Updated Time (hrs)
      - You'll find this tiny table sitting in the bottom right corner.
      - It shows you how outdated the data from each world. You are encouraged to checkout yourself and the public data gets updated
    - Universalis Velocity
      - It is right above `Universalis Last Updated Time`
      - The velocity exhibits a monotonically increasing correlation with the aggregate volume of goods sold within a specified time interval
    - (TBD) Data from other DC
- **HQ filter**, to display HQ only if you wish
- **Colour highlight**, so you tell HQ items visually instead of textually, and this can also work for price higher than vendor NPC, etc.

## Manual

Please feel free to send a request if any feature you want is not yet available via:
- GitHub Issues
- Discord (@franfkntastic)

**Data**

Listings
- Price per unit in Selling and History are tax *excluded*, the same as what you see from the in-game marketboard & the number you will fill to sell via retainers.
- An option is provided to include tax in the total price column, to give you an idea about how much you'll actually pay.
- An option is provided to colour the record in red, if vendor NPC sells cheaper. This takes priority over HQ.

Popularity
- The Universalis Velocity.
- The higher the number, the more items sold in recent time.

World Updated
- How many hours has passed since the market data of this item was last updated for each world in the selected Region/DC/World.
- You may want to visit the most outdated worlds to update and upload the public data, so that your overall result will be more accurate.

**Search**

Please note that you can trigger multiple search without waiting for previous ones to finish. A loading icon will show after the item name if there's any on-going request at the moment.

There are 3 typical ways to trigger a search:

1. Hover over an item.
2. When the hotkey is pressed, hover over an item.
3. Hover over an item, then press the hotkey.
4. Optionally, you can wait for a configurable delay starting from 0 ms, together with any of the above.

If the hotkey is disabled, only 1 will work. If enabled, 2 & 3 will work. If you use a delay, you don't need to long-hold the hotkey but just press the hotkey and keep your mouse on that item for the configured time.

You can set a hotkey to open the plugin window. If you set it as the same hotkey as search, when you press the hotkey, the window will show up + do the search.

**Region**

The contents in the world dropdown menu is populated based on the information of your current character.

Apart from the worlds your character can directly travel to, the plugin also adds options for the whole Datacentre and the region (which may contain multiple DCs).

You can manually configure more candidates that will be additionally added to the menu, for example, when you are playing in Mana/JP but want to know the price in OCE to prepare your cross-region DC travel.

**Icon**

The item icon is interactive in the following ways:

- Click: Copy the name to clipboard.
- Ctrl + Click: Set the name from clipboard.
  - This will trigger a new search using the item name, and can be useful if you don't have the item right now but have its name in your clipboard from somewhere else.

**Buttons**

On the left:

- Refresh
  - Click: Force refresh the market data for the current item.
- HQ Filter
  - Click: Toggle to show only HQ data records. When enabled, the button will be in orange.
  - Ctrl + Click: Toggle to request only HQ data records from the server. When enabled, the button will be in cyan.
    - It can be helpful when you are looking for HQ items but the table is flooded with low-priced NQs, e.g., Commanding Craftsman's Draught.
    - Use the Refresh button to trigger a refresh.
- Worlds
  - Select the Region/DC/World for later search requests.
  - Use the Refresh button to trigger a refresh.

On the right:

- List
  - Click: Switch between History and Stats.
- Delete
  - Click: Delete this record.
  - Ctrl + Click: Delete all records.
- Config
  - Click: Toggle to show config window.

**History**

A list for all items and results in your cache.

Click to review without making another web request.

Use the refresh button if you need to.

## Acknowledgement

This project is a fork of Elypha's Simple Market Board, who in itself was inspired by many different plugins you can find on their repo. I'm only further toying with the idea in the hopes that some of you might share my frustrations with the base functionality. 
