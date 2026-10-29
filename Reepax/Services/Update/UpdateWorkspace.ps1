function Remove-CompletedWorkspace($workspace) {
    try {
        $full = [IO.Path]::GetFullPath($workspace).TrimEnd('\')
        $root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'Reepax\Update')).TrimEnd('\')
        $id = [Guid]::Empty
        if ((Split-Path -Parent $full) -ine $root -or
            -not [Guid]::TryParseExact((Split-Path -Leaf $full), 'N', [ref]$id)) { return }
        $current = $full
        while ($current) {
            if ((Test-Path -LiteralPath $current) -and
                ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
            $current = Split-Path -Parent $current
        }
        if (Test-Path -LiteralPath $full) { [IO.Directory]::Delete($full, $true) }
    } catch { }
}
