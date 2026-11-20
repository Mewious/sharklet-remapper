# Builds the installer and publishes it as a GitHub release.
# Usage: .\release.ps1 1.2.1 "What changed"
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$Notes = '',
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot

# git and gh print progress to stderr, which Windows PowerShell would treat as an error; trust the exit code instead.
function Run([string]$what, [scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed." }
}

$tag = "v$Version"

if (git status --porcelain) { throw 'Commit or discard your changes first, so the release matches the code on GitHub.' }
Run 'git push' { git push origin HEAD }
$commit = git rev-parse HEAD

$ErrorActionPreference = 'Continue'
gh release view $tag *> $null
$exists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($exists) { throw "Release $tag already exists. Pick a new version." }

if (-not $DotNetPath) {
    $DotNetPath = 'dotnet'
    $hasNet8 = (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((dotnet --list-sdks) -match '^8\.')
    $local = Join-Path $env:LOCALAPPDATA 'Temp\tailwind-dotnet8\dotnet.exe'
    if (-not $hasNet8 -and (Test-Path -LiteralPath $local)) { $DotNetPath = $local }
}

& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -DotNetPath $DotNetPath

$output = Join-Path $PSScriptRoot 'dist\installer'
$installer = Join-Path $output "Sharklet-Setup-$Version.exe"
$files = @($installer, "$installer.sha256", (Join-Path $output "Sharklet-$Version-Windows.zip"))

$releaseArguments = @('release', 'create', $tag) + $files + @('--target', $commit, '--title', "Sharklet $Version")
if ($Notes) { $releaseArguments += @('--notes', $Notes) } else { $releaseArguments += '--generate-notes' }
Run 'gh release create' { gh @releaseArguments }

Write-Host "Released Sharklet $Version"
