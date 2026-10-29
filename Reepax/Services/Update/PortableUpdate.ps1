$stateDir = Join-Path $dest '.reepax-update'
$journalPath = Join-Path $stateDir 'journal.json'
$backupDir = Join-Path $stateDir 'backup'
$sessionDir = Split-Path -Parent $source
$mutexBytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($dest).TrimEnd('\').ToUpperInvariant())
$sha = [Security.Cryptography.SHA256]::Create()
$mutexHash = [BitConverter]::ToString($sha.ComputeHash($mutexBytes)).Replace('-', '')
$sha.Dispose()
$mutex = New-Object Threading.Mutex($false, "Local\ReepaxUpdate_$mutexHash")
$owned = $false
$canModify = $false
function Write-Result($status) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
    @{ Status = $status } | ConvertTo-Json | Set-Content -LiteralPath "$resultPath.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$resultPath.tmp" -Destination $resultPath -Force
}
function Save-Journal {
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$journalPath.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$journalPath.tmp" -Destination $journalPath -Force
}
function Resolve-SafePath($root, $name) {
    if ([IO.Path]::IsPathRooted($name) -or $name -match '(^|[\\/])(\.|\.\.)([\\/]|$)' -or $name -match '^(Data|\.reepax-update)([\\/]|$)' -or
        $name -match '[:*?"<>|]' -or $name -ieq 'portable.txt') { throw 'Unsafe update path.' }
    $root = [IO.Path]::GetFullPath($root).TrimEnd('\')
    $path = [IO.Path]::GetFullPath((Join-Path $root $name))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe update path.' }
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked update destination.' }
        }
        $current = Split-Path -Parent $current
    }
    return $path
}
function Install-File($path, $target, $hash) {
    $pending = Join-Path $stateDir 'pending.bin'
    try {
        [IO.File]::Copy($path, $pending, $false)
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $hash) { throw 'Staged file verification failed.' }
        if (Test-Path -LiteralPath $target -PathType Leaf) { [IO.File]::Replace($pending, $target, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($pending, $target) }
    } finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    }
}
function Restore-Files {
    $failed = $false
    $pending = Join-Path $stateDir 'pending.bin'
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    foreach ($file in $state.Files) {
      try {
        $target = Resolve-SafePath $dest $file.Name
        if ($file.Existed) {
            $backup = Resolve-SafePath $backupDir $file.Name
            if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $file.OldHash) { throw 'Invalid update backup.' }
            if ((Test-Path -LiteralPath $target -PathType Leaf) -and
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $file.OldHash) { continue }
            Install-File $backup $target $file.OldHash
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.OldHash) { throw 'Restored file verification failed.' }
        } elseif (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
      } catch { $failed = $true }
    }
    if ($failed) { $state.Phase = 'recovery-required'; Save-Journal; throw 'Some files could not be restored. Backups are preserved.' }
    $state.Phase = 'restored'
    Save-Journal
}
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another update is running.' }
    if (Test-Path -LiteralPath $stateDir) {
        if ((Get-Item -LiteralPath $stateDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid recovery directory.' }
        $state = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
        if ($state.Phase -notin @('preparing', 'installing', 'awaiting', 'recovery-required', 'confirmed', 'restored')) { throw 'Invalid recovery state.' }
        if (-not $Recover) {
            if ($state.Phase -notin @('confirmed', 'restored')) { throw 'Recovery is required.' }
            Remove-Item -LiteralPath $stateDir -Recurse -Force
        }
    }
    if ($Recover) {
        $procId = $RecoveryProcessId
    } else {
        $manifest = Get-Content -LiteralPath (Join-Path $sessionDir 'manifest.json') -Raw | ConvertFrom-Json
        $files = @()
        foreach ($property in $manifest.PSObject.Properties) {
            $path = Resolve-SafePath $source $property.Name
            $target = Resolve-SafePath $dest $property.Name
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $property.Value) { throw 'Prepared update changed.' }
            $existed = Test-Path -LiteralPath $target -PathType Leaf
            if ((Test-Path -LiteralPath $target) -and -not $existed) { throw 'Update destination is not a file.' }
            $files += @{ Name = $property.Name; Hash = $property.Value; Existed = $existed; OldHash = '' }
        }
        if ($files.Count -eq 0) { throw 'Empty update manifest.' }
        $required = 104857600L
        foreach ($file in $files) {
            $required += (Get-Item -LiteralPath (Join-Path $source $file.Name)).Length
            if ($file.Existed) { $required += (Get-Item -LiteralPath (Join-Path $dest $file.Name)).Length }
        }
        if ((New-Object IO.DriveInfo([IO.Path]::GetPathRoot($dest))).AvailableFreeSpace -lt $required) { throw 'Insufficient update space.' }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'Reepax.exe')).ProductVersion.Split('+')[0]
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = New-Object Security.AccessControl.DirectorySecurity
        $security.SetOwner($identity)
        $security.SetAccessRuleProtection($true, $false)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $security.AddAccessRule($rule)
        Set-Acl -LiteralPath $stateDir -AclObject $security
        Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $stateDir 'recover.ps1')
        '@echo off', 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0recover.ps1" -Recover -RecoveryProcessId 0' |
            Set-Content -LiteralPath (Join-Path $stateDir 'recover.cmd') -Encoding ASCII
        $state = @{ Phase = 'preparing'; Session = [Guid]::NewGuid().ToString('N'); Version = $version; Files = $files }
        Save-Journal
        Set-Content -LiteralPath (Join-Path $sessionDir 'helper.ready') -Value 'ready'
    }
    if (-not $Recover) {
        $approvalTimer = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath (Join-Path $sessionDir 'helper.approved'))) {
            if ((Test-Path -LiteralPath (Join-Path $sessionDir 'helper.cancel')) -or $approvalTimer.Elapsed.TotalSeconds -gt 20) { throw 'Update was not approved.' }
            Start-Sleep -Milliseconds 50
        }
    }
    if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
        Wait-Process -Id $procId -Timeout 60 -ErrorAction Stop
    }
    if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { throw 'Application is still running.' }
    if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe }) { throw 'Another application instance is running.' }
    $canModify = $true
    if ($Recover) {
        if ($state.Phase -in @('installing', 'awaiting', 'recovery-required')) { Restore-Files }
        else { $state.Phase = 'restored'; Save-Journal }
        Write-Result 'restored'
    } else {
        foreach ($file in $state.Files) {
            $target = Resolve-SafePath $dest $file.Name
            $file.Existed = Test-Path -LiteralPath $target -PathType Leaf
            if ((Test-Path -LiteralPath $target) -and -not $file.Existed) { throw 'Update destination is not a file.' }
            if ($file.Existed) {
                $target = Resolve-SafePath $dest $file.Name
                $backup = Resolve-SafePath $backupDir $file.Name
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $target -Destination $backup
                $file.OldHash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
                if ($file.OldHash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw 'Backup verification failed.' }
            }
        }
        $state.Phase = 'installing'
        Save-Journal
        foreach ($file in $state.Files) {
            $path = Resolve-SafePath $source $file.Name
            $target = Resolve-SafePath $dest $file.Name
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Hash) { throw 'Prepared update changed.' }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Install-File $path $target $file.Hash
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.Hash) { throw 'Installed file verification failed.' }
        }
        $state.Phase = 'awaiting'
        Save-Journal
        $process = Start-Process -FilePath $exe -WorkingDirectory $dest -ArgumentList "$restartArgs --update-session=$($state.Session)" -PassThru
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $ready = Join-Path $stateDir 'startup.ready'
        while (-not (Test-Path -LiteralPath $ready) -and -not $process.HasExited -and $timer.Elapsed.TotalSeconds -lt 60) {
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath $ready) -or (Get-Content -LiteralPath $ready -Raw).Trim() -ne $state.Session) {
            if (-not $process.HasExited) { $state.Phase = 'recovery-required'; Save-Journal; throw 'Startup was not confirmed.' }
            throw 'Updated application did not start.'
        }
        $state.Phase = 'confirmed'
        Save-Journal
        Write-Result 'success'
        Remove-Item -LiteralPath $stateDir -Recurse -Force
        exit 0
    }
    Start-Process -FilePath $exe -WorkingDirectory $dest -ArgumentList $restartArgs | Out-Null
    exit 0
} catch {
    try {
        if ($owned -and $canModify -and $null -ne $state -and $state.Phase -in @('installing', 'awaiting')) {
            Restore-Files
            Write-Result 'restored'
            Start-Process -FilePath $exe -WorkingDirectory $dest -ArgumentList $restartArgs | Out-Null
        } else {
            if ($owned -and $null -ne $state -and $state.Phase -eq 'preparing') { $state.Phase = 'restored'; Save-Journal }
            Write-Result 'failed'
        }
    } catch { Write-Result 'recovery-required' }
    exit 1
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    if ($owned -and $null -ne $state -and $state.Phase -in @('confirmed', 'restored')) {
        Remove-CompletedWorkspace $sessionDir
    }
}
