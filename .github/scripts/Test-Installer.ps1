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
    foreach ($file in Get-ChildItem dist/Reepax -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath((Resolve-Path dist/Reepax), $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $relative)).Hash) {
            throw 'Installed payload differs from the published application.'
        }
    }
    $exe = Join-Path $installed 'Reepax.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion.Split('+')[0] -ne $Version) { throw 'Installed version is incorrect.' }
    $session = [Guid]::NewGuid().ToString('N')
    @{ Session = $session; Version = $Version; Executable = $exe; HelperId = $PID;
       HelperStart = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString() } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'update-startup.json') -Encoding UTF8
    $process = Start-Process $exe -ArgumentList "--restart --update-session=$session" -PassThru -WindowStyle Hidden
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $ready = Join-Path $data 'update-startup.ready'
    while (-not (Test-Path -LiteralPath $ready) -and -not $process.HasExited -and $timer.Elapsed.TotalSeconds -lt 60) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ready) -or (Get-Content -LiteralPath $ready -Raw).Trim() -ne $session) {
        throw 'Installed application did not confirm startup.'
    }
    Write-Output 'Installation, reinstallation, user-data preservation, payload integrity and application startup verified.'
} finally {
    if ($install -and -not $install.HasExited) { $install.Kill($true); $install.WaitForExit() }
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
