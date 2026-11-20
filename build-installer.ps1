param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.2.0',
    [string]$DotNetPath = 'dotnet',
    [string]$InnoCompiler,
    [string]$SignToolCommand
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($SignToolCommand -and -not $SignToolCommand.Contains('$f')) {
    throw 'SignToolCommand must include the literal $f placeholder for the file to sign.'
}

if (-not $InnoCompiler) {
    $compilerPaths = @(
        (Join-Path $PSScriptRoot 'obj\inno-setup\ISCC.exe'),
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    $InnoCompiler = $compilerPaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'Install Inno Setup 6.3 or newer, or pass -InnoCompiler with the path to ISCC.exe.'
}

$staging = Join-Path $PSScriptRoot 'obj\installer'
$publish = Join-Path $staging 'publish'
$output = Join-Path $PSScriptRoot 'dist\installer'
New-Item -ItemType Directory -Path $staging, $publish, $output -Force | Out-Null

# Pin the publisher's original installer to the hash in Microsoft's winget manifest:
# https://github.com/microsoft/winget-pkgs/blob/master/manifests/v/ViGEm/ViGEmBus/1.22.0/ViGEm.ViGEmBus.installer.yaml
$driverName = 'ViGEmBus_1.22.0_x64_x86_arm64.exe'
$driver = Join-Path $staging $driverName
$driverHash = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
if (-not (Test-Path -LiteralPath $driver)) {
    Invoke-WebRequest "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/$driverName" -OutFile $driver
}
if ((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash -ne $driverHash) {
    throw "Driver checksum mismatch. Remove $driver and rebuild."
}

& $DotNetPath publish (Join-Path $PSScriptRoot 'TailwindRemapper.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:RestoreLockedMode=true "-p:BaseOutputPath=$staging\build\" "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Remapper publish failed.' }

$testReport = Join-Path $publish 'self-test.txt'
if (Test-Path -LiteralPath $testReport) { Remove-Item -LiteralPath $testReport }
$test = Start-Process -FilePath (Join-Path $publish 'TailwindRemapper.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $test.WaitForExit(30000)) {
    $test.Kill()
    throw 'Remapper self-test timed out.'
}
if ($test.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $testReport)) { throw 'Remapper self-test failed.' }
Get-Content -LiteralPath $testReport

$compilerArguments = @("/DAppVersion=$Version")
if ($SignToolCommand) {
    $compilerArguments += '/DSignedBuild'
    $compilerArguments += "/STailwindSigning=$SignToolCommand"
}
& $InnoCompiler @compilerArguments (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $output "Sharklet-Setup-$Version.exe"
if ($SignToolCommand) {
    foreach ($file in @((Join-Path $publish 'TailwindRemapper.exe'), $installer)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {
            throw "A trusted, timestamped signature is required before packaging: $file"
        }
        Write-Host "Verified publisher: $($signature.SignerCertificate.Subject)"
    }
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Host "Installer: $installer"

$instructions = Join-Path $output 'START HERE.txt'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sharing-notes.txt') -Destination $instructions -Force
$package = Join-Path $output "Sharklet-$Version-Windows.zip"
Compress-Archive -LiteralPath $installer, "$installer.sha256", $instructions -DestinationPath $package -CompressionLevel NoCompression -Force
Write-Host "Shareable package: $package"
