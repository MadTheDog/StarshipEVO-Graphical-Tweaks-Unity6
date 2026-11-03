function Get-BuildPath([string]$Root,[string]$Path) {
    $full = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    } else {
        [IO.Path]::GetFullPath((Join-Path $Root $Path))
    }
    $prefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build path must be inside this checkout: $full"
    }
    for ($itemPath = $full; $itemPath; $itemPath = [IO.Path]::GetDirectoryName($itemPath)) {
        $item = Get-Item -LiteralPath $itemPath -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Build paths cannot traverse a junction or symbolic link: $itemPath"
        }
    }
    return $full
}
