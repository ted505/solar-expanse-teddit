# Teddit Experimental Branch Report

## Replacement Candidate

- Staged DLL: `E:\[GITHUB]\PF1\Absalom Adventure\_teddit_work\replacement\Teddit.dll`
- Source candidate: `E:\[GITHUB]\solar-expanse-teddit\Teddit\Teddit.dll`
- Candidate plugin version: Teddit 1.12
- Installed game plugin version from log: Teddit 1.11

## Confirmed Original Failure

The game log reports Teddit 1.11 failing to patch:

`Game.UI.UIManager.UpdateUpkeepAndIncome(long balance, long totalCost, long totalUpkeep, long facilityCost, long licenseCost, long SCCost, long LVCost, long researchCost, long totalIncome, Dictionary<Facility.EIncomeType, double> sumIncomes)`

The installed Teddit 1.11 DLL still has a prefix parameter named `colonistsIncome`, which no longer exists in the experimental branch method signature.

The staged Teddit 1.12 DLL removes that obsolete `colonistsIncome` parameter and patches the current `totalIncome`/`balance` signature.

## YAML Loading

The Teddit 1.12 source keeps the multi-directory YAML loading flow:

- Root plugin directory first.
- Then each subdirectory under `plugins\Teddit\mods`, sorted alphabetically.
- Later mod directories can override earlier object ID and field combinations.

The staged candidate includes `YamlDotNet.dll`.

## Required Categories Present

The candidate package contains YAML data for:

- Facilities: `facilities.yaml`
- Research: `research.yaml`
- Spacecraft: `spacecraft.yaml`
- Launch vehicles: `launch_vehicles.yaml`
- Icons and custom resources through Teddit resource/icon patch code and mod icon folders

Runtime verification still requires installing the candidate into the game plugin folder and launching Solar Expanse.

## Additional Incompatibilities Found

Offline Harmony validation against the experimental game assemblies found these likely branch changes:

- `EnergyProductionModule.ProductEnergy` now has two overloads:
  - `ProductEnergy(double days)`
  - `ProductEnergy(bool dryRun, double days, bool suppressNotifications)`
- Teddit 1.12 targets `ProductEnergy` by name only, so Harmony reports an ambiguous match. The patch should target the one-argument `double` overload explicitly.
- `PMTabSchedule.CalculateCostStart` now has overloads:
  - `CalculateCostStart(double fuelNoOnOrbit, ref bool launchCostZero)`
  - `CalculateCostStart(PMMissionParameter PMMissionParameter, double fuelNoOnOrbit, ref bool launchCostZero)`
- Teddit targets `CalculateCostStart` by name only and also looks for a `planMissionWindow` field on `PMTabSchedule`. The field is not present on the experimental branch type. The patch should either target the overload that receives `PMMissionParameter`, or find `PlanMissionWindow.pmTabSchedule` / `pmMissionParameter` by another route.

Several other offline IL compile failures appeared while patching Unity methods inside PowerShell. Those are not proven runtime incompatibilities because this is not the normal Unity/BepInEx runtime. The two overload/field issues above are the actionable findings.

## Blockers

- The installed machine has no modern .NET SDK or Roslyn C# compiler available to rebuild the SDK-style project from source.
- The .NET Framework compiler is present, but only supports C# 5 and cannot compile the current Teddit source.
- Write permission to the game plugin folder was not granted, so the staged DLL was not installed or runtime-tested.
