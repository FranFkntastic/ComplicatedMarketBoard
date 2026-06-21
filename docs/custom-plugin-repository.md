# Custom Plugin Repository

ComplicatedMarketBoard can be installed as a normal Dalamud plugin through a custom plugin repository. The custom repository is a JSON file that points Dalamud at a downloadable plugin zip.

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

`latest.zip` contains the plugin DLL, generated Dalamud manifest, dependency manifest, XML docs, and the bundled `Miosuke` helper DLL. `repo.json` is the custom repository manifest that Dalamud reads.

## Publish

Create a GitHub Release and upload both generated files:

```text
dist\latest.zip
dist\repo.json
```

The expected stable custom repository URL is:

```text
https://github.com/FranFkntastic/ComplicatedMarketBoard/releases/latest/download/repo.json
```

In game, open `/xlsettings`, go to Experimental, add that URL under Custom Plugin Repositories, enable it, and save. Then open `/xlplugins` and install `ComplicatedMarketBoard`.

## Update Flow

1. Update `<Version>` in `ComplicatedMarketBoard/ComplicatedMarketBoard.csproj`.
2. Run `.\tools\Build-DalamudRelease.ps1`.
3. Create a new GitHub Release.
4. Upload the generated `latest.zip` and `repo.json`.

Dalamud will use the `AssemblyVersion`, `DalamudApiLevel`, and download URLs from the generated `repo.json`.
