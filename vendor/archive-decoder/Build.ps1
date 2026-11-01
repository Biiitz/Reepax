[CmdletBinding()]
param(
    [string] $SourceArchivePath,
    [string] $BasePackagePath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$packageVersion = '0.50.4-reepax.2'
$sourceVersion = '0.50.4'
$packageHash = '045ac4886eaad54fcb0e9785cda312178e7140579fa4bebd3e5238456ce2970b'
$sourceHash = '1b190735922358dfaa27acfe6562ee0513b4f0e363db32cb128a6b17e40ba801'
$sourceCommit = 'c083c6efd843a844b0c8f7878787360e815be781'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporary = Join-Path $temporaryParent ('archive-decoder-build-' + [Guid]::NewGuid().ToString('N'))
$packageDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../packages'))

function Assert-Hash([string] $Path, [string] $Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw 'The downloaded source does not match the pinned revision.'
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    foreach ($command in @('dotnet', 'git', 'tar')) {
        Get-Command $command -ErrorAction Stop | Out-Null
    }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or [int]($sdk.Split('.')[0]) -lt 10) {
        throw 'Rebuilding the decoder requires .NET SDK 10 or later.'
    }

    $originalPackage = Join-Path $temporary 'original.nupkg'
    if ($BasePackagePath) {
        Copy-Item -LiteralPath $BasePackagePath -Destination $originalPackage
    } else {
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/sharpcompress/$sourceVersion/sharpcompress.$sourceVersion.nupkg" -OutFile $originalPackage -UseBasicParsing -TimeoutSec 60
    }
    Assert-Hash $originalPackage $packageHash
    $inputPackage = [IO.Compression.ZipFile]::OpenRead($originalPackage)
    try {
        $nuspecEntry = $inputPackage.Entries | Where-Object { $_.FullName.EndsWith('.nuspec') } | Select-Object -First 1
        $reader = [IO.StreamReader]::new($nuspecEntry.Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $inputPackage.Dispose() }
    $repository = $manifest.SelectSingleNode("//*[local-name()='repository']")
    $repositoryUri = [Uri] $repository.url
    if ($repositoryUri.Host -ne 'github.com' -or $repository.commit -ne $sourceCommit) {
        throw 'Unexpected source repository metadata.'
    }

    $sourceArchive = Join-Path $temporary 'source.tar.gz'
    $sourceUri = 'https://codeload.github.com' + $repositoryUri.AbsolutePath.TrimEnd('/') + '/tar.gz/' + $sourceCommit
    if ($SourceArchivePath) {
        Copy-Item -LiteralPath $SourceArchivePath -Destination $sourceArchive
    } else {
        Invoke-WebRequest $sourceUri -OutFile $sourceArchive -UseBasicParsing -TimeoutSec 60
    }
    Assert-Hash $sourceArchive $sourceHash
    & tar -xzf $sourceArchive -C $temporary
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
    $sourceRoot = Join-Path $temporary ('sharpcompress-' + $sourceCommit)
    $patch = Join-Path $PSScriptRoot 'Decoder.patch'
    & git -C $sourceRoot apply --check --whitespace=error $patch
    if ($LASTEXITCODE -ne 0) { throw 'The decoder patch does not match the pinned source.' }
    & git -C $sourceRoot apply --whitespace=error $patch
    if ($LASTEXITCODE -ne 0) { throw 'Applying the decoder patch failed.' }

    $buildDirectory = Join-Path $temporary 'build'
    New-Item -ItemType Directory -Path $buildDirectory | Out-Null
    $project = Join-Path $buildDirectory 'ArchiveDecoder.csproj'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ArchiveDecoder.csproj') -Destination $project
    & dotnet build $project --configuration Release --verbosity minimal "-p:DecoderSourceRoot=$sourceRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Building the corrected decoder failed.' }

    $manifest.SelectSingleNode("//*[local-name()='version']").InnerText = $packageVersion
    foreach ($group in @($manifest.SelectNodes("//*[local-name()='dependencies']/*[local-name()='group']"))) {
        if ($group.targetFramework -ne 'net8.0') { $group.ParentNode.RemoveChild($group) | Out-Null }
    }
    $packagePath = Join-Path $temporary "SharpCompress.$packageVersion.nupkg"
    $inputPackage = [IO.Compression.ZipFile]::OpenRead($originalPackage)
    $outputPackage = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in ($inputPackage.Entries | Sort-Object FullName)) {
            $name = $entry.FullName
            if ($name -eq '.signature.p7s' -or ($name.StartsWith('lib/') -and -not $name.StartsWith('lib/net8.0/'))) { continue }
            if ($name.EndsWith('.pdb')) { continue }
            $destination = $outputPackage.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $destination.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $stream = $destination.Open()
            try {
                if ($name.EndsWith('.nuspec')) {
                    $manifest.Save($stream)
                } elseif ($name -eq 'lib/net8.0/SharpCompress.dll') {
                    $binary = [IO.File]::OpenRead((Join-Path $buildDirectory 'bin/Release/net8.0/SharpCompress.dll'))
                    try { $binary.CopyTo($stream) } finally { $binary.Dispose() }
                } else {
                    $original = $entry.Open()
                    try { $original.CopyTo($stream) } finally { $original.Dispose() }
                }
            } finally { $stream.Dispose() }
        }
    } finally {
        $outputPackage.Dispose()
        $inputPackage.Dispose()
    }
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $packageDirectory "SharpCompress.$packageVersion.nupkg") -Force
    Write-Output 'Corrected decoder package rebuilt successfully.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $temporaryParent -or
        -not [IO.Path]::GetFileName($resolved).StartsWith('archive-decoder-build-')) {
        throw 'Temporary cleanup target is invalid.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
