param(
    [string] $SptRoot = $env:SPT_ROOT,
    [string] $CscPath = $env:CSC_PATH,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

if (-not $SptRoot) {
    throw 'Set SPT_ROOT or pass -SptRoot to an SPT 4.1.6 installation.'
}

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

$ErrorActionPreference = 'Stop'
$source = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
$projectFile = Join-Path $PSScriptRoot 'Crashsite.Client.csproj'
$version = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Crashsite.Client.csproj has no <Version>.' }
$generatedDir = Join-Path $PSScriptRoot "obj\build\$Configuration"
New-Item -ItemType Directory -Force -Path $generatedDir | Out-Null
$assemblyInfo = Join-Path $generatedDir 'AssemblyInfo.g.cs'
@(
    'using System.Reflection;'
    "[assembly: AssemblyVersion(""$version.0"")]"
    "[assembly: AssemblyFileVersion(""$version.0"")]"
    "[assembly: AssemblyInformationalVersion(""$version"")]"
) | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8
$source += $assemblyInfo
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

