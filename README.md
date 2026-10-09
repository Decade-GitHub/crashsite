<div align="center">

<img src="crashsite-logo.png" width=300 alt="Escape from Tarkov: Crashsite">

A hardcore survival location for **SPT4.1.6**, inspired by *The Division: Survival*.

Crashsite adds a separate, selectable raid into Customs: permanent night, abundant loot, all twelve bosses, and an incurable toxin that steadily damages you.

</div>

## Survive by any means necessary

- **PMC Level 21 required to infil.**
- **Always 01:00**.
- **All twelve regular bosses** and their escorts arrive by the eight-minute mark at max.
- **Dense loot and unlocked doors.** Loot is abundant and keys are not required.
- **Permanent toxin.** Healing and Immunity still help; antidotes do not.
- **No starting equipment.** You get your knife, a loaner M9 and your secure container. Good luck.

## Installation

Extract a matching [Release](https://github.com/Decade-GitHub/crashsite/releases) package into your SPT installation root. It contains:

```text
BepInEx/plugins/Crashsite.Client.dll
SPT_Runtime/user/mods/Crashsite.Server.dll
```
Fika is not currently supported.

## Building

The supported build workflow uses **Windows, PowerShell, Visual Studio's Roslyn C# compiler, and an installed SPT 4.1.6**. The server also needs the .NET 10 and ASP.NET Core 10 shared runtimes required by SPT.

Set your installation path before building:

```powershell
$env:SPT_ROOT = 'C:\Games\SPT'
.\package.ps1
```

This builds matching Debug and Release ZIPs under `dist/`. To build one variant:

```powershell
.\package.ps1 -SptRoot 'D:\Games\SPT' -Configuration Release
```

### Local paths and project settings

| File | Setting | Value |
| --- | --- | --- |
| `src/Crashsite.Client/Crashsite.Client.csproj` | `SPT_ROOT` | Your SPT installation root. An environment variable supplies it; remove or replace the local fallback. |
| `src/Crashsite.Server/Crashsite.Server.csproj` | `SptRuntimePath` | Your installation's `SPT_Runtime` directory. It is derived automatically from `SPT_ROOT` when set. |
| Both `build.ps1` scripts | `SptRoot` | Your SPT root, supplied through `SPT_ROOT` or `-SptRoot`. Remove or replace the local fallback. |
| Both `build.ps1` scripts | `CscPath` | Visual Studio's `MSBuild\Current\Bin\Roslyn\csc.exe`. Scripts try `vswhere` if their default path is unavailable. |
| Server `build.ps1` | `DotnetRoot` | The .NET installation containing `shared/`. Defaults to `DOTNET_ROOT`, then `Program Files\dotnet`. |

If compiler discovery fails, set the compiler fallback in both build scripts before using `package.ps1`. Individual builds also accept explicit paths:

```powershell
.\src\Crashsite.Server\build.ps1 -SptRoot $env:SPT_ROOT -CscPath 'D:\Tools\Roslyn\csc.exe' -DotnetRoot 'C:\Program Files\dotnet'
.\src\Crashsite.Client\build.ps1 -SptRoot $env:SPT_ROOT -CscPath 'D:\Tools\Roslyn\csc.exe'
```

Keep the existing target frameworks, assembly names, reference filenames, and `CRASHSITE_DEBUG` configuration. **Both** `.csproj` versions must match.