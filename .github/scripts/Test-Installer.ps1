param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$TemporaryRoot
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer verification requires a disposable CI runner.' }
$root = Join-Path $TemporaryRoot ('InstallerVerification_' + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $root "application's [directory]"
$dataParent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
$data = [IO.Path]::GetFullPath((Join-Path $dataParent 'Reepax'))
$savedData = Join-Path $root 'user-data-backup'
$ownsData = $false
$process = $null
$install = $null
$legacy = Join-Path $root "previous application's [directory]"
try {
    $fullRoot = [IO.Path]::GetFullPath($root)
    $temporaryParent = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd('\') + '\'
    if (-not $fullRoot.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($data) -ine $dataParent -or [IO.Path]::GetFileName($data) -cne 'Reepax') {
        throw 'Invalid verification directories.'
    }
    New-Item -ItemType Directory -Path $root | Out-Null
    if (Test-Path -LiteralPath $data) { Move-Item -LiteralPath $data -Destination $savedData }
    New-Item -ItemType Directory -Path $data | Out-Null
    $ownsData = $true
    & $Compiler /Q /DUpdateSmokeTest=1 "/DMyAppVersion=$Version" "/O$root" installer/Reepax.iss
    if ($LASTEXITCODE -ne 0) { throw 'Verification installer compilation failed.' }
    $setup = Join-Path $root 'setup.exe'
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /NOICONS /UPDATEHELPER=1 /DIR=`"$installed`""
    $install = Start-Process $setup -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $install.WaitForExit(180000) -or $install.ExitCode -ne 0) { throw 'Clean installation failed.' }
    $settings = Join-Path $data 'settings.json'
    Set-Content -LiteralPath $settings -Value '{"Language":"en","MinimizeToTrayOnClose":false}' -Encoding UTF8
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    $marker = Join-Path $data 'update-preservation.txt'
    Set-Content -LiteralPath $marker -Value 'preserve'
    $library = Join-Path $installed 'Reepax.dll'
    Set-Content -LiteralPath $library -Value 'incomplete previous payload'
    $install = Start-Process $setup -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $install.WaitForExit(180000) -or $install.ExitCode -ne 0) { throw 'Reinstallation failed.' }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or (Get-Content -LiteralPath $marker).Trim() -ne 'preserve') {
        throw 'Installation changed user data.'
    }
    foreach ($file in Get-ChildItem dist/InstallerPayload -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath((Resolve-Path dist/InstallerPayload), $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $relative)).Hash) {
            throw 'Installed payload differs from the published application.'
        }
    }
    $exe = Join-Path $installed 'current/Reepax.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0] -ne $Version) { throw 'Installed version is incorrect.' }
    New-Item -ItemType Directory -Path $legacy | Out-Null
    Copy-Item -LiteralPath 'dist/Reepax/Reepax.exe', 'dist/Reepax/Reepax.dll' -Destination $legacy
    $unrelated = Join-Path $legacy 'user-file.bin'
    [IO.File]::WriteAllBytes($unrelated, [byte[]](0, 17, 255))
    $helperPath = Join-Path $root 'apply-update.ps1'
    $legacyExe = Join-Path $legacy 'Reepax.exe'
    dotnet Reepax.Tests/bin/Release/net8.0-windows/Reepax.Tests.dll --run-update-helper $data $setup $legacyExe $Version $helperPath
    if ($LASTEXITCODE -ne 0) {
        $resultPath = Join-Path $data 'update-result.json'
        if (Test-Path -LiteralPath $resultPath) {
            $diagnostic = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $stage = if ($diagnostic.Stage -in @('verification', 'installation', 'installed-files', 'startup-confirmation', 'cleanup')) { $diagnostic.Stage } else { 'unknown' }
            $code = if ($diagnostic.InstallerExitCode -is [long] -or $diagnostic.InstallerExitCode -is [int]) { $diagnostic.InstallerExitCode } else { 'unavailable' }
            Write-Output "Update failure stage: $stage. Installer exit code: $code."
        }
        throw 'Legacy installer update failed.'
    }
    $result = Get-Content -LiteralPath (Join-Path $data 'update-result.json') -Raw | ConvertFrom-Json
    if ($result.Status -ne 'success') { throw 'The legacy update helper did not confirm startup.' }
    $exe = Join-Path $legacy 'current/Reepax.exe'
    $process = Get-Process -Name Reepax -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe | Select-Object -First 1
    if (-not $process -or $process.HasExited -or [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0] -ne $Version) {
        throw 'The managed application did not start at the expected version.'
    }
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($unrelated)) -ne '0011FF' -or (Get-Content -LiteralPath $marker).Trim() -ne 'preserve') {
        throw 'The transition changed an unrelated file.'
    }
    Write-Output 'Clean installation, repair, legacy update handoff, managed startup and data preservation verified.'
} finally {
    if ($install -and -not $install.HasExited) { $install.Kill($true); $install.WaitForExit() }
    if (-not $process) {
        $expectedExecutable = Join-Path $legacy 'current/Reepax.exe'
        $process = Get-Process -Name Reepax -ErrorAction SilentlyContinue | Where-Object Path -EQ $expectedExecutable | Select-Object -First 1
    }
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
    }
    $full = [IO.Path]::GetFullPath($root)
    $parent = [IO.Path]::GetFullPath($TemporaryRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^InstallerVerification_[0-9a-f]{32}$') { throw 'Invalid verification workspace.' }
    if ([IO.Path]::GetDirectoryName($data) -ine $dataParent -or [IO.Path]::GetFileName($data) -cne 'Reepax') {
        throw 'Invalid application data directory.'
    }
    if ($ownsData -and (Test-Path -LiteralPath $data)) { Remove-Item -LiteralPath $data -Recurse -Force }
    if (Test-Path -LiteralPath $savedData) {
        if (Test-Path -LiteralPath $data) { throw 'Cannot restore application data over an existing directory.' }
        Move-Item -LiteralPath $savedData -Destination $data
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
