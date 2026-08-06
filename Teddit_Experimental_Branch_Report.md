# Teddit Experimental Branch Report

## Current Repo Output

- Repo root: `E:\[GITHUB]\solar-expanse-teddit`
- Game root used by the project file: `E:\STEAM\steamapps\common\Solar Expanse`
- Repo replacement payload: `E:\[GITHUB]\solar-expanse-teddit\Teddit`
- Staged replacement payload: `E:\[GITHUB]\solar-expanse-teddit\_teddit_work\replacement`

The staged replacement binaries and the repo payload binaries currently match by SHA-256:

- `Teddit\Teddit.dll`
- `Teddit\Teddit.pdb`
- `Teddit\YamlDotNet.dll`

## Confirmed Original Failure

The installed game copy was Teddit 1.11 and failed while patching:

`Game.UI.UIManager.UpdateUpkeepAndIncome(long balance, long totalCost, long totalUpkeep, long facilityCost, long licenseCost, long SCCost, long LVCost, long researchCost, long totalIncome, Dictionary<Facility.EIncomeType, double> sumIncomes)`

Teddit 1.11 still patched the obsolete `colonistsIncome` parameter. The repo payload is Teddit 1.12 and removes that obsolete parameter.

## Source Repairs Applied

- `Source\Teddit\GameplayPatches.cs` now targets `EnergyProductionModule.ProductEnergy(double days)` explicitly, avoiding the experimental branch overload ambiguity.
- `Source\Teddit\Patches.cs` now targets `PMTabSchedule.CalculateCostStart(PMMissionParameter, double, ref bool)` when present, falls back to the older `CalculateCostStart(double, ref bool)` overload, and reads `PMMissionParameter` from Harmony's argument array before trying the older `planMissionWindow` field route.
- `Source\Teddit\Teddit.csproj` now uses `E:\STEAM\steamapps\common\Solar Expanse` for game references, writes repo output to this repository, and only deploys to BepInEx when `DeployTeddit=true` is passed.

## YAML Loading Preserved

The Teddit 1.12 source keeps the multi-directory YAML loading flow:

- Root plugin directory first.
- Then each subdirectory under `plugins\Teddit\mods`, sorted alphabetically.
- Later mod directories can override earlier object ID and field combinations.

The repo payload includes `YamlDotNet.dll`.

## Required Categories Present

The candidate package contains YAML data for:

- Facilities: `facilities.yaml`
- Research: `research.yaml`
- Spacecraft: `spacecraft.yaml`
- Launch vehicles: `launch_vehicles.yaml`
- Icons and custom resources through Teddit resource/icon patch code and mod icon folders.

## Remaining Blockers

- This machine currently has no modern .NET SDK installed. `dotnet build -c Release Source\Teddit\Teddit.csproj` fails with `No .NET SDKs were found`.
- Because the repaired source could not be rebuilt, `Teddit\Teddit.dll` is still the previously staged Teddit 1.12 binary, not a new binary containing the two source repairs above.
- Runtime verification still requires rebuilding, installing the rebuilt files into `E:\STEAM\steamapps\common\Solar Expanse\BepInEx\plugins\Teddit`, launching Solar Expanse, and checking `BepInEx\LogOutput.log`.

## Recommended Next Step

Install a .NET SDK capable of building `net471`, then run:

```powershell
dotnet build -c Release E:\[GITHUB]\solar-expanse-teddit\Source\Teddit\Teddit.csproj
```

To also deploy into the game plugin folder after building, run with:

```powershell
dotnet build -c Release E:\[GITHUB]\solar-expanse-teddit\Source\Teddit\Teddit.csproj -p:DeployTeddit=true
```
