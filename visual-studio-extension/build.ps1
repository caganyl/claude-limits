<#
.SYNOPSIS
  Builds the Claude Limits Visual Studio extension (VSIX) and runs the Core unit tests.

.DESCRIPTION
  1. Restores NuGet packages (msbuild /restore; works for SDK-style and classic PackageReference projects).
  2. Builds ClaudeLimits.sln in the requested configuration with MSBuild (found via vswhere when not on PATH).
  3. Runs `dotnet test` for ClaudeLimits.Core.Tests (skip with -SkipTests).
  4. Copies the .vsix to artifacts\ClaudeLimits.vsix.

.EXAMPLE
  ./build.ps1
  ./build.ps1 -Configuration Debug -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    # VSIX version. Visual Studio refuses to install a .vsix whose version is already installed, so every
    # CI build gets its own number (1.0.<run>). Local builds keep the manifest's version unless given.
    [string] $Version = $(if ($env:GITHUB_RUN_NUMBER) { "1.0.$($env:GITHUB_RUN_NUMBER)" } else { '' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$solution = Join-Path $root 'ClaudeLimits.sln'
$artifacts = Join-Path $root 'artifacts'

function Find-MSBuild {
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    throw 'MSBuild bulunamadı. Visual Studio 2022 (Visual Studio extension development iş yükü) veya Build Tools kurun.'
}

function Invoke-Step([string] $name, [scriptblock] $block) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$name başarısız (çıkış kodu $LASTEXITCODE)" }
}

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Geçersiz sürüm: $Version (ör. 1.0.7)" }
    $manifest = Join-Path $root 'ClaudeLimits.VisualStudio\source.extension.vsixmanifest'
    $xml = Get-Content $manifest -Raw
    $xml = [regex]::Replace($xml, '(<Identity\b[^>]*\bVersion=")[^"]*(")', "`${1}$Version`${2}")
    Set-Content -Path $manifest -Value $xml -Encoding UTF8 -NoNewline
    $info = Join-Path $root 'ClaudeLimits.VisualStudio\Properties\AssemblyInfo.cs'
    (Get-Content $info -Raw) -replace 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(`"$Version.0`")" |
        Set-Content -Path $info -Encoding UTF8 -NoNewline
    Write-Host "VSIX sürümü: $Version"
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild"

Invoke-Step 'NuGet restore' {
    & $msbuild $solution /t:Restore /p:Configuration=$Configuration /nologo /v:minimal
}

Invoke-Step "Build ($Configuration)" {
    & $msbuild $solution /t:Build /p:Configuration=$Configuration /p:DeployExtension=false /m /nologo /v:minimal
}

if (-not $SkipTests) {
    Invoke-Step 'Unit tests' {
        & dotnet test (Join-Path $root 'ClaudeLimits.Core.Tests\ClaudeLimits.Core.Tests.csproj') -c $Configuration --nologo
    }
}

$vsix = Join-Path $root "ClaudeLimits.VisualStudio\bin\$Configuration\ClaudeLimits.VisualStudio.vsix"
if (-not (Test-Path $vsix)) { throw "VSIX bulunamadı: $vsix" }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$target = Join-Path $artifacts 'ClaudeLimits.vsix'
Copy-Item $vsix $target -Force

# Sanity check: Core.dll must be in the package, Newtonsoft.Json must not.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($target)
try {
    $names = $zip.Entries | ForEach-Object { $_.FullName }
    if (-not ($names -contains 'ClaudeLimits.Core.dll')) { throw 'ClaudeLimits.Core.dll VSIX içinde yok.' }
    if ($names -contains 'Newtonsoft.Json.dll') { Write-Warning 'Newtonsoft.Json.dll VSIX içinde; Visual Studio zaten içeriyor.' }
    Write-Host ('VSIX içeriği: ' + ($names -join ', '))
}
finally {
    $zip.Dispose()
}

Write-Host "Tamam: $target" -ForegroundColor Green
