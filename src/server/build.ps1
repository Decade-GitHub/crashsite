param(
    [string] $SptRoot = $env:SPT_ROOT,
    [string] $CscPath = $env:CSC_PATH,
    [string] $DotnetRoot = $env:DOTNET_ROOT,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not $SptRoot) {
    throw 'Set SPT_ROOT or pass -SptRoot to an SPT 4.1.6 installation.'
}

if (-not $DotnetRoot) {
    $DotnetRoot = Join-Path $env:ProgramFiles 'dotnet'
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

function Get-LatestFrameworkDir([string] $name) {
    $root = Join-Path $DotnetRoot "shared\$name"
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Missing .NET shared framework folder: $root"
    }

    $dir = Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
        Select-Object -First 1
    if (-not $dir) {
        throw "No .NET 10 $name runtime found under $root"
    }

    return $dir.FullName
}

function Get-ManagedAssemblies([string] $dir) {

    Get-ChildItem -LiteralPath $dir -Filter '*.dll' -File | Where-Object {
        try {
            [void][System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
            $true
        }
        catch {
            $false
        }
    } | ForEach-Object { $_.FullName }
}

$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir 'Crashsite.Server.csproj'
$version = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw "Crashsite.Server.csproj has no <Version>."
}

$sptRuntime = Join-Path $SptRoot 'SPT_Runtime'
$sptReferences = @('SPTarkov.Common.dll', 'SPTarkov.DI.dll', 'SPTarkov.Server.Core.dll', 'SemanticVersioning.dll', '0Harmony.dll') |
    ForEach-Object { Join-Path $sptRuntime $_ }

foreach ($path in @($CscPath) + $sptReferences) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing build input: $path"
    }
}

$frameworkReferences = @(
    Get-ManagedAssemblies (Get-LatestFrameworkDir 'Microsoft.NETCore.App')
    Get-ManagedAssemblies (Get-LatestFrameworkDir 'Microsoft.AspNetCore.App')
)

$generatedDir = Join-Path $projectDir "obj\build\$Configuration"
$outputDir = Join-Path $projectDir "bin\$Configuration\net10.0"
New-Item -ItemType Directory -Force -Path $generatedDir, $outputDir | Out-Null

$globalUsings = Join-Path $generatedDir 'GlobalUsings.g.cs'
@(
    'global using System;'
    'global using System.Collections.Generic;'
    'global using System.IO;'
    'global using System.Linq;'
    'global using System.Net.Http;'
    'global using System.Threading;'
    'global using System.Threading.Tasks;'
) | Set-Content -LiteralPath $globalUsings -Encoding UTF8

$assemblyInfo = Join-Path $generatedDir 'AssemblyInfo.g.cs'
@(
    'using System.Reflection;'
    "[assembly: AssemblyVersion(""$version.0"")]"
    "[assembly: AssemblyFileVersion(""$version.0"")]"
    "[assembly: AssemblyInformationalVersion(""$version"")]"
    '[assembly: AssemblyProduct("Crashsite.Server")]'
    '[assembly: AssemblyTitle("Crashsite.Server")]'
) | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8

$sources = @(Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$output = Join-Path $outputDir 'Crashsite.Server.dll'
$arguments = @(
    '/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/deterministic+',
    '/langversion:latest', '/nullable:enable', '/debug:portable',
    "/out:$output", "/pdb:$([System.IO.Path]::ChangeExtension($output, '.pdb'))"
)
$arguments += ($sptReferences + $frameworkReferences) | ForEach-Object { "/reference:$_" }
if ($Configuration -eq 'Debug') { $arguments += '/define:CRASHSITE_DEBUG', '/optimize-' }
$arguments += $globalUsings, $assemblyInfo
$arguments += $sources

$responseFile = Join-Path $generatedDir 'csc.rsp'
$responseLines = [string[]]($arguments | ForEach-Object {
    if ($_ -notmatch '\s') { $_ }
    elseif ($_ -match '^(/[a-z]+:)(.+)$') { $Matches[1] + '"' + $Matches[2] + '"' }
    else { '"' + $_ + '"' }
})

[System.IO.File]::WriteAllLines($responseFile, $responseLines)

& $CscPath "@$responseFile"
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler exited with code $LASTEXITCODE"
}

Write-Output $output
