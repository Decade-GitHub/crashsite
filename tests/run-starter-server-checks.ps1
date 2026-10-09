param(
    [string] $SptRoot = $env:SPT_ROOT,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $CscPath = $env:CSC_PATH,
    [string] $DotnetRoot = $env:DOTNET_ROOT
)
$ErrorActionPreference='Stop'
if (-not $SptRoot) { throw 'Set SPT_ROOT or pass -SptRoot to an SPT 4.1.6 installation.' }
if (-not $CscPath) {
    $compiler = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($compiler) { $CscPath = $compiler.Source }
    else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
            if ($installPath) { $CscPath = Join-Path $installPath 'MSBuild\Current\Bin\Roslyn\csc.exe' }
        }
    }
}
if (-not $CscPath -or -not (Test-Path -LiteralPath $CscPath -PathType Leaf)) {
    throw 'Set CSC_PATH or pass -CscPath to a Roslyn csc.exe compiler.'
}
if (-not $DotnetRoot) { $DotnetRoot = Join-Path $env:ProgramFiles 'dotnet' }
$repo=Split-Path $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'bin') -Force | Out-Null
$server=Join-Path $repo "src/server/bin/$Configuration/net10.0/Crashsite.Server.dll"
$out=Join-Path $PSScriptRoot "bin/StarterServerChecks-$Configuration.dll"
$refs=@(Get-Content (Join-Path $repo "src/server/obj/build/$Configuration/csc.rsp") | Where-Object {$_ -match '^/reference:'})
$lines=@('/nologo','/nostdlib+','/target:exe','/langversion:latest','/nullable:enable',"/out:$out")+$refs+@("/reference:$server",(Join-Path $PSScriptRoot 'StarterServerChecks.cs'))
$rsp=Join-Path $PSScriptRoot "bin/StarterServerChecks-$Configuration.rsp"
$lines | Set-Content $rsp -Encoding utf8
& $CscPath "@$rsp"
if($LASTEXITCODE){throw 'Server checks compilation failed'}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.AspNetCore.App","version":"10.0.9"}}}' | Set-Content ([IO.Path]::ChangeExtension($out,'.runtimeconfig.json')) -Encoding utf8
Copy-Item $server (Join-Path $PSScriptRoot 'bin/Crashsite.Server.dll') -Force
& (Join-Path $DotnetRoot 'dotnet.exe') $out $SptRoot (Join-Path $PSScriptRoot 'bin/Crashsite.Server.dll')
if($LASTEXITCODE){throw 'Server kit checks failed'}

