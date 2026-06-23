# AGENTS.md

Guidance for coding agents working in this repository.

## Repository Purpose

ComplicatedMarketBoard is a Dalamud plugin for FFXIV market board power users. It is distributed through GitHub Releases and the shared custom Dalamud repository at:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

## Branches

- `master` is the public release branch.
- `local-dev` is the local integration branch.
- Keep `local-dev` fast-forwardable to `master` unless the user explicitly asks for a different branch flow.
- Do not recreate old upstream contribution branches unless the user asks.

## Build And Verification

Use PowerShell on Windows.

Common checks:

```powershell
dotnet build "ComplicatedMarketBoard.sln" -c Debug --no-incremental
dotnet build "ComplicatedMarketBoard.sln" -c Release --no-incremental
git diff --check
```

The release package script is:

```powershell
.\tools\Build-DalamudRelease.ps1
```

It builds Release, creates `dist\latest.zip`, and creates `dist\repo.json`.

## Release Flow

Before publishing a release:

1. Check existing tags/releases.
2. Bump `<Version>` in `ComplicatedMarketBoard\ComplicatedMarketBoard.csproj`.
3. Run Debug and Release builds.
4. Run `git diff --check`.
5. Commit source changes.
6. Run `.\tools\Build-DalamudRelease.ps1`.
7. Create a GitHub Release tagged `v<version>` and upload `dist\latest.zip`.
8. Run `.\tools\Update-DalamudPluginmaster.ps1`.
9. Commit and push the resulting change in the sibling `DalamudPlugins` repo.
10. Verify the raw pluginmaster URL points at the new versioned release ZIP.

## Code Notes

- `MarketScopeCatalog` is the central place for region, data center, world, and custom scope canonicalization.
- `CustomMarketScopes` stores mixed user-defined market comparisons.
- `selectedCustomScopeId` tracks the active custom scope separately from normal `selectedWorld`.
- Universalis does not accept comma-separated world targets in the current request path. Custom scopes use multiple normal Universalis requests and merge results client-side.
- Keep UI changes compact. This is an in-game utility, not a landing page.

## Working Tree Hygiene

- Do not stage `.idea`, `.vs`, `bin`, `obj`, or `dist` output.
- Be careful with generated packaging artifacts. `dist` is release output, not source.
- If build or packaging dirties generated files, inspect before staging.
- Do not revert user changes unless explicitly requested.
