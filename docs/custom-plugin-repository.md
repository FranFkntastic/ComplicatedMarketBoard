# Custom Plugin Repository

ComplicatedMarketBoard can be installed as a normal Dalamud plugin through a custom plugin repository. The public custom repository is a JSON file that can list multiple plugins and points Dalamud at each plugin's downloadable zip.

## Build The Release Assets

Run:

```powershell
.\tools\Build-DalamudRelease.ps1
```

This creates:

```text
dist\latest.zip
dist\repo.json
```

`latest.zip` contains the plugin DLL, generated Dalamud manifest, dependency manifest, XML docs, and the bundled `Miosuke` helper DLL. `repo.json` is a single-plugin repository manifest entry that can be copied into the general plugin repository.

## Publish

Create a GitHub Release and upload the generated package:

```text
dist\latest.zip
```

The package URL in the repository manifest should be versioned:

```text
https://github.com/FranFkntastic/ComplicatedMarketBoard/releases/download/v1.12.0.0/latest.zip
```

The expected stable custom repository URL users should add in Dalamud is:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

In game, open `/xlsettings`, go to Experimental, add that URL under Custom Plugin Repositories, enable it, and save. Then open `/xlplugins` and install `ComplicatedMarketBoard`.

## Update Flow

1. Update `<Version>` in `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`.
2. Run `.\tools\Build-DalamudRelease.ps1`.
3. Create a new GitHub Release.
4. Upload the generated `latest.zip`.
5. Run `.\tools\Update-DalamudPluginmaster.ps1`.
6. Commit and push `FranFkntastic/DalamudPlugins`.

`Update-DalamudPluginmaster.ps1` copies the generated `dist\repo.json` entry into `FranFkntastic/DalamudPlugins\pluginmaster.json`, validates the manifest JSON, and checks that the versioned `latest.zip` URL is reachable. Dalamud will use the `AssemblyVersion`, `DalamudApiLevel`, and download URLs from `pluginmaster.json`.
