param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PayloadDirectory = 'dist/Reepax',
    [string]$OutputDirectory = 'dist/updates',
    [string]$InstallerDirectory = 'dist/InstallerPayload'
)
$ErrorActionPreference = 'Stop'
$packageId = 'Reepax.Desktop'
if ($Version -notmatch '^\d{2}\.(?:[1-9]|1[0-2])\.\d{1,4}(?:\.\d{1,4})?$') { throw 'Invalid application version.' }
$parsedVersion = [Version]$Version
$packageVersion = if ($parsedVersion.Revision -gt 0) {
    "$($parsedVersion.Major).$($parsedVersion.Minor).$($parsedVersion.Build + 1)-patch.$($parsedVersion.Revision)"
} else { $parsedVersion.ToString(3) }
if (Test-Path -LiteralPath $InstallerDirectory) { throw 'Installer staging must use a new directory.' }
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$inventory = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/')
})
$inventory | Set-Content -LiteralPath (Join-Path $payload 'update-files.txt') -Encoding utf8BOM
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Update packaging tool restoration failed.' }
dotnet tool run vpk pack --packId $packageId --packTitle Reepax --packVersion $packageVersion --packDir $PayloadDirectory --mainExe Reepax.exe --runtime win-x64 --framework webview2 --shortcuts None --icon Reepax/Assets/app.ico --outputDir $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Update packaging failed.' }
$archive = Join-Path $OutputDirectory "$packageId-win-Portable.zip"
[IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $archive), [IO.Path]::GetFullPath($InstallerDirectory))
Remove-Item -LiteralPath (Join-Path $InstallerDirectory '.portable')
# The legacy helper checks both root-level version resources before confirming the transition.
Copy-Item -LiteralPath (Join-Path $InstallerDirectory 'current/Reepax.dll') -Destination (Join-Path $InstallerDirectory 'Reepax.dll')
$package = Join-Path $OutputDirectory "$packageId-$packageVersion-full.nupkg"
$contents = [ordered]@{}
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $package))
try {
    foreach ($entry in $zip.Entries) {
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $contents.Add($entry.FullName, @{ Size = $entry.Length; SHA256 = $hash })
    }
} finally { $zip.Dispose() }
$contents | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$packageId-$Version-files.json") -Encoding utf8NoBOM
$cache = Join-Path $InstallerDirectory 'packages'
New-Item -ItemType Directory -Path $cache | Out-Null
Copy-Item -LiteralPath $package -Destination $cache
$feedPath = Join-Path $OutputDirectory 'releases.win.json'
$feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
$feed.Assets = @($feed.Assets | Where-Object { $_.Version -eq $packageVersion -and $_.Type -in @('Full', 'Delta') })
if (@($feed.Assets | Where-Object Type -EQ 'Full').Count -ne 1) { throw 'The release requires exactly one full package.' }
foreach ($asset in $feed.Assets) {
    $file = Join-Path $OutputDirectory $asset.FileName
    if ($asset.PackageId -ne $packageId -or $asset.Size -ne (Get-Item -LiteralPath $file).Length -or
        $asset.SHA256 -ine (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) { throw 'Update feed verification failed.' }
}
$feed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $feedPath -Encoding utf8NoBOM
# Only the compatible installer and package feed are distributed.
Remove-Item -LiteralPath $archive, (Join-Path $OutputDirectory "$packageId-win-Setup.exe")
foreach ($name in @('Update.exe', 'Reepax.exe', 'Reepax.dll', 'current/Reepax.exe', 'current/sq.version')) {
    if (-not (Test-Path -LiteralPath (Join-Path $InstallerDirectory $name) -PathType Leaf)) { throw 'The installer payload is incomplete.' }
}
if ($env:GITHUB_ACTIONS -eq 'true') { "VELOPACK_PACKAGE_VERSION=$packageVersion" >> $env:GITHUB_ENV }
