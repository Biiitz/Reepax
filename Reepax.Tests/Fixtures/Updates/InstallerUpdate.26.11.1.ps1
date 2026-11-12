$setup = $source
$sessionDir = Split-Path -Parent $setup
$destination = Split-Path -Parent $exe
$requestPath = Join-Path (Split-Path -Parent $resultPath) 'update-startup.json'
$readyPath = Join-Path (Split-Path -Parent $resultPath) 'update-startup.ready'
$updateSession = [Guid]::NewGuid().ToString('N')
$mutexBytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($destination).TrimEnd('\').ToUpperInvariant())
$sha = [Security.Cryptography.SHA256]::Create()
$mutexHash = [BitConverter]::ToString($sha.ComputeHash($mutexBytes)).Replace('-', '')
$sha.Dispose()
$mutex = New-Object Threading.Mutex($false, "Local\ReepaxUpdate_$mutexHash")
$owned = $false
function Write-Result($status) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
    @{ Status = $status } | ConvertTo-Json | Set-Content -LiteralPath "$resultPath.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$resultPath.tmp" -Destination $resultPath -Force
}
try {
    try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another update is running.' }
    if ($expectedHash -notmatch '\A[0-9a-fA-F]{64}\z' -or $expectedVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Missing update verification metadata.' }
    $currentHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($currentHash -ne $expectedHash) { throw 'Installer integrity check failed.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $requestPath) -Force | Out-Null
    Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
    @{ Session = $updateSession; Version = $expectedVersion; Executable = $exe; HelperId = $PID;
       HelperStart = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString() } |
        ConvertTo-Json | Set-Content -LiteralPath "$requestPath.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$requestPath.tmp" -Destination $requestPath -Force
    Set-Content -LiteralPath (Join-Path $sessionDir 'helper.ready') -Value 'ready'
    $approvalTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $sessionDir 'helper.approved'))) {
        if ((Test-Path -LiteralPath (Join-Path $sessionDir 'helper.cancel')) -or $approvalTimer.Elapsed.TotalSeconds -gt 20) { throw 'Update was not approved.' }
        Start-Sleep -Milliseconds 50
    }
    if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
        Wait-Process -Id $procId -Timeout 60 -ErrorAction Stop
    }
    if ($procId -gt 0 -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { throw 'Application is still running.' }
    if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe }) { throw 'Another application instance is running.' }
    $currentHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($currentHash -ne $expectedHash) { throw 'Installer integrity check failed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'NotSigned' -and $signature.Status -ne 'Valid') { throw 'Invalid installer signature.' }
    $installer = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /SP- /DIR=`"$destination`" /UPDATEHELPER=1" -PassThru -Wait
    if ($installer.ExitCode -eq 3010) { Write-Result 'restart-required'; exit 1 }
    if ($installer.ExitCode -ne 0) { throw 'Installer did not complete successfully.' }
    foreach ($file in @($exe, (Join-Path $destination 'Reepax.dll'))) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Installed application is incomplete.' }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion
        if (-not $version -or $version.Split('+')[0] -ne $expectedVersion) { throw 'Installed version differs from the expected update.' }
    }
    $process = Start-Process -FilePath $exe -WorkingDirectory $destination -ArgumentList "$restartArgs --update-session=$updateSession" -PassThru
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $readyPath) -and -not $process.HasExited -and $timer.Elapsed.TotalSeconds -lt 60) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $readyPath) -or (Get-Content -LiteralPath $readyPath -Raw).Trim() -ne $updateSession) {
        throw 'Updated application startup was not confirmed.'
    }
    Write-Result 'success'
    Remove-Item -LiteralPath $setup -Force
    exit 0
} catch {
    if ($owned) { Write-Result 'failed' }
    exit 1
} finally {
    if ($owned) {
        if (Test-Path -LiteralPath $requestPath) {
            try {
                $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
                if ($request.Session -eq $updateSession) {
                    Remove-Item -LiteralPath $requestPath -Force
                    Remove-Item -LiteralPath $readyPath -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
        $mutex.ReleaseMutex()
        Remove-CompletedWorkspace $sessionDir
    }
    $mutex.Dispose()
}
