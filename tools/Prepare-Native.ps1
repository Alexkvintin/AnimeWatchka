param([string]$SevenZip = 'C:/Program Files/7-Zip/7z.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'AnimeApp/Assets/Native/libmpv-2.dll'
$expected = '34780746A0273A4FCAE42DFE262A28984A426A238939474AFD3A1561B450BBEF'
if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $expected) { Write-Host 'mpv is ready (SHA256 verified).'; exit 0 }
if (-not (Test-Path $SevenZip)) { $command = Get-Command 7z -ErrorAction SilentlyContinue; if ($command) { $SevenZip = $command.Source } else { throw 'Install 7-Zip or pass -SevenZip with the path to 7z.exe.' } }
$stage = Join-Path $root ('.local/native-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
$archive = Join-Path $stage 'mpv.7z'
Invoke-WebRequest 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261010/mpv-dev-x86_64-20261010-git-b2c255c13e.7z' -OutFile $archive
& $SevenZip x $archive "-o$stage/extracted" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'mpv extraction failed.' }
$candidates = @(Get-ChildItem (Join-Path $stage 'extracted') -Recurse -File -Filter 'libmpv-2.dll')
if ($candidates.Count -ne 1 -or (Get-FileHash $candidates[0].FullName -Algorithm SHA256).Hash -ne $expected) { throw 'Unexpected mpv binary; SHA256 verification failed.' }
New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
Copy-Item -LiteralPath $candidates[0].FullName -Destination $target
Write-Host 'mpv is ready (SHA256 verified). Download cache is in .local.'
