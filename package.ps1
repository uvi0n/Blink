param(
    [Parameter(Mandatory=$true)][string]$ToolsDirectory,
    [Parameter(Mandatory=$true)][string]$OutputZip
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tools = (Resolve-Path -LiteralPath $ToolsDirectory).Path
$output = [IO.Path]::GetFullPath($OutputZip)
$stage = "$output.$([Guid]::NewGuid().ToString('N')).tmp"
Push-Location (Join-Path $root 'native')
try {
    & cargo build --locked --release
    if ($LASTEXITCODE) { throw 'Rust build failed' }
} finally { Pop-Location }
if ($env:BLINK_MANAGED_ONLY -eq '1') { throw 'Release packages require the Rust core' }
& (Join-Path $root 'source/build.cmd')
if ($LASTEXITCODE) { throw 'Blink build failed' }
$versionSource = Get-Content (Join-Path $root 'source/Boot.cs') -Raw
$match = [regex]::Match($versionSource,'const string Current="(\d+\.\d+\.\d+)"')
if (!$match.Success) { throw 'Missing release version' }
$version = $match.Groups[1].Value
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
function Add-File($archive, $from, $to) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $from, $to, [IO.Compression.CompressionLevel]::Optimal)
}
try {
    $zip = [IO.Compression.ZipFile]::Open($stage, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-File $zip (Join-Path $root 'BlinkLauncher.exe') 'Blink/Blink.exe'
        foreach ($name in 'Enable-SendTo.cmd','QRCoder-LICENSE.txt','Cloudflare-LICENSE.txt','age-LICENSE.txt') {
            Add-File $zip (Join-Path $root $name) "Blink/$name"
        }
        Add-File $zip (Join-Path $root 'docs/download.md') 'Blink/Инструкция.txt'
        Add-File $zip (Join-Path $root 'native/THIRD-PARTY.txt') 'Blink/Rust-LICENSES.txt'
        foreach ($name in 'Blink.exe','Blink.exe.config','QRCoder.dll','lhr_hosts') {
            Add-File $zip (Join-Path $root $name) "Blink/builds/$version/$name"
        }
        foreach ($name in 'cloudflared.exe','age.exe') {
            Add-File $zip (Join-Path $tools $name) "Blink/builds/$version/$name"
        }
        $entry = $zip.CreateEntry('Blink/version.txt')
        $writer = New-Object IO.StreamWriter($entry.Open())
        try { $writer.Write($version) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    if ([IO.File]::Exists($output)) { [IO.File]::Replace($stage,$output,[System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($stage,$output) }
} finally { if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) } }
Get-FileHash -LiteralPath $output

