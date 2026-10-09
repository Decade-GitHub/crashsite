param([string]$SptRoot='C:\Programas\Steam\steamapps\common\SPT',[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot 'bin/StarterClientChecks.exe'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe' /nologo /target:exe /langversion:latest "/out:$out" "/reference:$SptRoot/BepInEx/core/0Harmony.dll" "/reference:$SptRoot/EscapeFromTarkov_Data/Managed/netstandard.dll" "/reference:$SptRoot/EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll" (Join-Path $PSScriptRoot 'StarterClientChecks.cs')
if($LASTEXITCODE){throw 'Client starter checks compilation failed'}
& $out $SptRoot (Join-Path $PSScriptRoot "../src/Crashsite.Client/bin/$Configuration/Crashsite.Client.dll") (Join-Path $PSScriptRoot 'bin/starter-grant.json')
if($LASTEXITCODE){throw 'Client starter checks failed'}

