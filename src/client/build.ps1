param(
    [string] $SptRoot = $env:SPT_ROOT,
    [string] $CscPath = '',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

if (-not $SptRoot) {
    $SptRoot = 'SET_PATH_HERE'
}

if (-not $CscPath) {
    $CscPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $CscPath -PathType Leaf) -and (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installPath) {
            $CscPath = Join-Path $installPath 'MSBuild\Current\Bin\Roslyn\csc.exe'
        }
    }
}

$ErrorActionPreference = 'Stop'
$source = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
$outputDir = Join-Path $PSScriptRoot "bin\$Configuration"
$output = Join-Path $outputDir 'Crashsite.Client.dll'
$references = @(
    (Join-Path $SptRoot 'BepInEx\core\BepInEx.dll'),
    (Join-Path $SptRoot 'BepInEx\core\0Harmony.dll'),
    (Join-Path $SptRoot 'BepInEx\plugins\spt\spt-common.dll'),
    (Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed\UnityEngine.dll'),
    (Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed\UnityEngine.CoreModule.dll'),
    (Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed\Newtonsoft.Json.dll'),
    (Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed\netstandard.dll')
)

foreach ($path in @($CscPath) + $source + $references) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing build input: $path"
    }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$arguments = @('/nologo', '/target:library', '/optimize+', '/langversion:latest', "/out:$output")
if ($Configuration -eq 'Debug') { $arguments += '/define:CRASHSITE_DEBUG', '/optimize-', '/debug:portable' }
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $source
& $CscPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler exited with code $LASTEXITCODE"
}

Write-Output $output

