$ErrorActionPreference = "Stop"
$version = "3.0.21"
$pkgRoot = Join-Path $env:USERPROFILE (".nuget\packages\videolan.libvlc.windows\" + $version + "\build")
$outDir = Join-Path $PSScriptRoot "vlc-data"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

foreach ($arch in @("x64", "x86")) {
  $zipPath = Join-Path $outDir ("vlc-" + $arch + ".zip")
  if (Test-Path $zipPath) { Write-Output ("exists: $zipPath"); continue }

  $src = Join-Path $pkgRoot $arch
  if (-not (Test-Path $src)) { throw "нет папкиvlc $src" }

  $stage = Join-Path $env:TEMP ("vlcstage-" + $arch)
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory -Path $stage | Out-Null

  Copy-Item (Join-Path $src "libvlc.dll") $stage -Force
  Copy-Item (Join-Path $src "libvlccore.dll") $stage -Force
  foreach ($d in @("plugins", "lua", "hrtfs")) {
    $sd = Join-Path $src $d
    if (Test-Path $sd) { Copy-Item $sd (Join-Path $stage $d) -Recurse -Force }
  }

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
  Remove-Item $stage -Recurse -Force
  $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
  Write-Output ("packed: $zipPath ($size MB)")
}
