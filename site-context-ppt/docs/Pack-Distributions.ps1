param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$Version,
    [switch]$DeveloperOnly
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$dist = Join-Path $root 'dist'
$prefix = 'site-context-ppt/'
$runtime = $prefix + 'runtime/'
$userName = "site-context-ppt-$Version-用户安装包.zip"
$devName = "site-context-ppt-$Version-开发交接包.zip"
$stage = [IO.Directory]::CreateDirectory((Join-Path $dist ([guid]::NewGuid().ToString('N')))).FullName

function Copy-ZipEntry($entry, $zip, [string]$name) {
    $item = $zip.CreateEntry($name, 'Optimal')
    $outStream = $item.Open()
    try {
        $inStream = $entry.Open()
        try { $inStream.CopyTo($outStream) } finally { $inStream.Dispose() }
    } finally { $outStream.Dispose() }
}

try {
    $source = [IO.Compression.ZipFile]::OpenRead((Join-Path $dist "site-context-ppt-$Version.zip"))
    try {
        if (!$DeveloperOnly) {
            if (!$source.GetEntry($runtime + 'SiteMapRenderer/MapRenderer.exe')) { throw 'Missing renderer in versioned release.' }
            $user = [IO.Compression.ZipFile]::Open((Join-Path $stage $userName), 'Create')
            try {
                foreach ($entry in $source.Entries) {
                    if (!$entry.Name) { continue }
                    if (!$entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)) { throw "Unexpected path: $($entry.FullName)" }
                    $rel = $entry.FullName.Substring($prefix.Length)
                    if ($rel.StartsWith('runtime/SiteMapRenderer/',[StringComparison]::Ordinal) -or
                        $rel -match '^(SKILL\.md|references/(acceptance|environment|inputs|map-capture|pages|planning-overlay|template-to-config)\.md|assets/(周边场地分析模板\.pptx|default-template-extraction/[^/]+|webapp/site\.config(?:\.schema)?\.json))$') {
                        Copy-ZipEntry $entry $user $entry.FullName
                    }
                }
            } finally { $user.Dispose() }
        }
    } finally { $source.Dispose() }

    $dev = [IO.Compression.ZipFile]::Open((Join-Path $stage $devName), 'Create')
    try {
        $pkg = "TransketchMcpPlugin.$Version.nupkg"
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($dev,(Join-Path $root 'docs/开发交接入口.md'),'MCP与SKILL接入说明.md','Optimal')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($dev,(Join-Path "$dist/packages" $pkg),$pkg,'NoCompression')
    } finally { $dev.Dispose() }
    if (!$DeveloperOnly) { [IO.File]::Move((Join-Path $stage $userName),(Join-Path $dist $userName),$true) }
    [IO.File]::Move((Join-Path $stage $devName),(Join-Path $dist $devName),$true)
} finally {
    foreach ($name in $userName,$devName) { [IO.File]::Delete((Join-Path $stage $name)) }
    [IO.Directory]::Delete($stage)
}
