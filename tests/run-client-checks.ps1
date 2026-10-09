param(
    [string] $SptRoot = $env:SPT_ROOT,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $CscPath = $env:CSC_PATH
)
$ErrorActionPreference = 'Stop'
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
$outDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$testExe = Join-Path $outDir 'PolishClientChecks.exe'
& $CscPath /nologo /target:exe /langversion:latest "/out:$testExe" "/reference:$SptRoot/BepInEx/core/0Harmony.dll" (Join-Path $PSScriptRoot 'PolishClientChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test harness compilation failed.' }
& $testExe $SptRoot (Join-Path $PSScriptRoot "../src/client/bin/$Configuration/Crashsite.Client.dll")
if ($LASTEXITCODE -ne 0) { throw 'Managed client checks failed.' }
