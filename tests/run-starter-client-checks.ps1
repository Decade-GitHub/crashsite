param(
    [string] $SptRoot = $env:SPT_ROOT,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $CscPath = $env:CSC_PATH
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
$out=Join-Path $PSScriptRoot 'bin/StarterClientChecks.exe'
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
& $CscPath /nologo /target:exe /langversion:latest "/out:$out" "/reference:$SptRoot/BepInEx/core/0Harmony.dll" "/reference:$SptRoot/EscapeFromTarkov_Data/Managed/netstandard.dll" "/reference:$SptRoot/EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll" (Join-Path $PSScriptRoot 'StarterClientChecks.cs')
if($LASTEXITCODE){throw 'Client starter checks compilation failed'}
& $out $SptRoot (Join-Path $PSScriptRoot "../src/client/bin/$Configuration/Crashsite.Client.dll") (Join-Path $PSScriptRoot 'bin/starter-grant.json')
if($LASTEXITCODE){throw 'Client starter checks failed'}

