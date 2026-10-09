param([string]$SptRoot='C:\Programas\Steam\steamapps\common\SPT',[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'bin') -Force | Out-Null
$server=Join-Path $repo "src/Crashsite.Server/bin/$Configuration/net10.0/Crashsite.Server.dll"
$out=Join-Path $PSScriptRoot "bin/StarterServerChecks-$Configuration.dll"
$refs=@(Get-Content (Join-Path $repo "src/Crashsite.Server/obj/build/$Configuration/csc.rsp") | Where-Object {$_ -match '^/reference:'})
$lines=@('/nologo','/nostdlib+','/target:exe','/langversion:latest','/nullable:enable',"/out:$out")+$refs+@("/reference:$server",(Join-Path $PSScriptRoot 'StarterServerChecks.cs'))
$rsp=Join-Path $PSScriptRoot "bin/StarterServerChecks-$Configuration.rsp"
$lines | Set-Content $rsp -Encoding utf8
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe' "@$rsp"
if($LASTEXITCODE){throw 'Server checks compilation failed'}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.AspNetCore.App","version":"10.0.9"}}}' | Set-Content ([IO.Path]::ChangeExtension($out,'.runtimeconfig.json')) -Encoding utf8
Copy-Item $server (Join-Path $PSScriptRoot 'bin/Crashsite.Server.dll') -Force
& 'C:\Program Files\dotnet\dotnet.exe' $out $SptRoot (Join-Path $PSScriptRoot 'bin/Crashsite.Server.dll')
if($LASTEXITCODE){throw 'Server kit checks failed'}

