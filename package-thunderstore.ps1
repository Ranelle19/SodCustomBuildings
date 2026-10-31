# Makes the Thunderstore package: Release\Ranelle-CustomBuildings-<version>.zip
#   manifest.json, icon.png, README.md, CHANGELOG.md   (from the Thunderstore folder)
#   plugins\CustomBuildings\CustomBuildings.dll         (the plugin just built)
#   plugins\CustomBuildings\Edge\BoundaryCoastal03\...  (the example wall piece)
# Upload that zip at thunderstore.io (Shadows of Doubt community), team "Ranelle". build.bat runs this.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dll = Join-Path $root 'Plugin\bin\Release\net6.0\CustomBuildings.dll'
if (-not (Test-Path $dll)) { throw "The plugin isn't built yet ($dll). Run build.bat first." }
$manifest = Get-Content -Raw (Join-Path $root 'Thunderstore\manifest.json') | ConvertFrom-Json
$version = $manifest.version_number
$stage = Join-Path $env:TEMP "CustomBuildings-thunderstore"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$stage = (Get-Item $stage).FullName   # long form, like the files' paths below
Copy-Item (Join-Path $root 'Thunderstore\*') $stage -Recurse -Force
Copy-Item $dll (Join-Path $stage 'plugins\CustomBuildings\CustomBuildings.dll') -Force
$out = Join-Path $root "Release\Ranelle-CustomBuildings-$version.zip"
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Release') | Out-Null
if (Test-Path $out) { Remove-Item $out -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# entries added one by one with forward slashes (Thunderstore and mod managers want them; some zip tools write backslashes)
$zip = [System.IO.Compression.ZipFile]::Open($out, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
  }
} finally { $zip.Dispose() }
Write-Host "  Thunderstore: $out"
