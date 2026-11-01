[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$cargo = (Get-Command cargo -ErrorAction Stop).Source
$oldFlags = $env:CARGO_ENCODED_RUSTFLAGS
$separator = [char]31
$flags = @()
if ($oldFlags) { $flags += $oldFlags.Split($separator) }
elseif ($env:RUSTFLAGS) { throw 'Use CARGO_ENCODED_RUSTFLAGS when supplying custom build flags.' }

$profileDirectory = [Environment]::GetFolderPath('UserProfile')
$cargoDirectory = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $profileDirectory '.cargo' }
$remappings = @(
    @{ Source = $profileDirectory; Target = '/_/user' },
    @{ Source = $cargoDirectory; Target = '/_/cargo' },
    @{ Source = $PSScriptRoot; Target = '/_/native' }
)
foreach ($mapping in $remappings) {
    $source = [IO.Path]::GetFullPath($mapping.Source)
    foreach ($variant in @($source, $source.Replace('\', '/')) | Select-Object -Unique) {
        $flags += "--remap-path-prefix=$variant=$($mapping.Target)"
    }
}

Push-Location $PSScriptRoot
try {
    $env:CARGO_ENCODED_RUSTFLAGS = $flags -join $separator
    & $cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    $buildDirectory = if ($env:CARGO_TARGET_DIR) { [IO.Path]::GetFullPath($env:CARGO_TARGET_DIR) } else { Join-Path $PSScriptRoot 'target' }
    $binary = Join-Path $buildDirectory 'release/reepax_adblock.dll'
    $bytes = [IO.File]::ReadAllBytes($binary)
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        if ($encoding.GetString($bytes) -match '(?i)(?:[A-Z]:[\\/]+Users[\\/]+|/Users/|/home/)') {
            throw 'Native build still contains a personal source path.'
        }
    }
    $project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Reepax'))
    foreach ($relative in @('reepax_adblock.dll', 'runtimes/win-x64/native/reepax_adblock.dll')) {
        Copy-Item -LiteralPath $binary -Destination (Join-Path $project $relative) -Force
    }
    Write-Output 'Native binaries rebuilt with anonymized source paths.'
}
finally {
    $env:CARGO_ENCODED_RUSTFLAGS = $oldFlags
    Pop-Location
}
