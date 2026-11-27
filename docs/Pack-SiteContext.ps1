param(
    [Parameter(Mandatory)] [string] $SkillDirectory,
    [Parameter(Mandatory)] [string] $PluginPackage,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $WebappManifestPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = (Resolve-Path -LiteralPath $SkillDirectory).Path
$plugin = Get-Item -LiteralPath $PluginPackage
$version = ([xml](Get-Content -Raw -LiteralPath (Join-Path $source 'dotnet/src/TransketchMcpPlugin/TransketchMcpPlugin.csproj'))).Project.PropertyGroup.Version
if ($plugin.Name -ne "TransketchMcpPlugin.$version.nupkg") { throw "Expected plugin version $version." }
foreach ($relative in 'SKILL.md','dotnet/tools/SiteCheck/Program.cs','runtime/SiteMapRenderer/MapRenderer.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $source $relative) -PathType Leaf)) { throw "Missing $relative" }
}
$renderer = Join-Path $source 'runtime/SiteMapRenderer'
$out = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
if ($out.TrimEnd('\') -eq $source -or $out.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output directory outside the skill.'
}

function Write-Archive([string]$base, [string]$prefix, [string]$target) {
    $archive = [IO.Compression.ZipFile]::Open($target, 'Create')
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $base -File -Recurse -Force | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($base.Length + 1).Replace('\','/')
            if ($relative -match '^dotnet/(?:.*/)?(?:obj|bin|artifacts)/') { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "$prefix/$relative", 'Optimal')
        }
    } finally { $archive.Dispose() }
}

$temp = [IO.Directory]::CreateDirectory((Join-Path $out ([Guid]::NewGuid().ToString('N')))).FullName
$skillZip = "site-context-ppt-$version.zip"
$rendererZip = 'SiteMapRenderer-win-x64.zip'
try {
    Write-Archive $source 'site-context-ppt' (Join-Path $temp $skillZip)
    Write-Archive $renderer 'SiteMapRenderer' (Join-Path $temp $rendererZip)
    if ($WebappManifestPath) {
        $prefix = 'site-context-ppt/assets/webapp/'
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $temp $skillZip))
        try {
            $files = @(foreach ($entry in ($archive.Entries | Sort-Object FullName)) {
                if ($entry.Name -and $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) {
                    $stream = $entry.Open()
                    try { $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant() }
                    finally { $stream.Dispose() }
                    [ordered]@{file=$entry.FullName.Substring($prefix.Length); sha256=$hash}
                }
            })
            if ($files.Count -eq 0) { throw 'No webapp resources found in release archive.' }
            $manifest = [ordered]@{
                version=$version
                archive=$skillZip
                directory=$prefix
                upstreamVersion='v25_20261113_临时目录统一释放'
                files=$files
            }
        } finally { $archive.Dispose() }
    }
    $packages = [IO.Directory]::CreateDirectory((Join-Path $out 'packages')).FullName
    $pluginTarget = Join-Path $packages $plugin.Name
    if ($plugin.FullName -ne $pluginTarget) { Copy-Item -LiteralPath $plugin.FullName -Destination $pluginTarget }
    foreach ($name in $skillZip,$rendererZip) {
        $path = Join-Path $temp $name
        Copy-Item -LiteralPath $path -Destination (Join-Path $out $name)
        Remove-Item -LiteralPath $path
    }
    if ($WebappManifestPath) {
        [IO.File]::WriteAllText($WebappManifestPath, ($manifest | ConvertTo-Json -Depth 4) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $out $skillZip),(Join-Path $out $rendererZip),$pluginTarget
} finally {
    if (!(Get-ChildItem -LiteralPath $temp -Force)) { [IO.Directory]::Delete($temp) }
}
