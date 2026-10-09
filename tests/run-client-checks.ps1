param([string] $SptRoot = 'C:\Programas\Steam\steamapps\common\SPT', [ValidateSet('Debug','Release')] [string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$csc = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$testExe = Join-Path $outDir 'PolishClientChecks.exe'
& $csc /nologo /target:exe /langversion:latest "/out:$testExe" "/reference:$SptRoot/BepInEx/core/0Harmony.dll" (Join-Path $PSScriptRoot 'PolishClientChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test harness compilation failed.' }
& $testExe $SptRoot (Join-Path $PSScriptRoot "../src/Crashsite.Client/bin/$Configuration/Crashsite.Client.dll")
if ($LASTEXITCODE -ne 0) { throw 'Managed client checks failed.' }
