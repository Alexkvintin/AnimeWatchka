param([string]$Compiler = "$PSScriptRoot\..\.local\tools\innosetup\ISCC.exe")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = if (Test-Path "$root\.dotnet\dotnet.exe") { "$root\.dotnet\dotnet.exe" } else { "dotnet" }
& $dotnet publish "$root\AnimeApp\AnimeApp.csproj" -c Release -r win-x64 --self-contained true -o "$root\artifacts\installer-payload"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$privateFiles = Get-ChildItem "$root\artifacts\installer-payload" -Recurse -File | Where-Object { $_.Name -match '^(credentials|tokens)\.dat$|\.db($|-)|\.log$' }
if ($privateFiles) { throw 'Personal data found in installer payload' }
if (Test-Path "$PSScriptRoot\oauth-public.json") {
    $publicConfig = Get-Content "$PSScriptRoot\oauth-public.json" -Raw | ConvertFrom-Json
    if ($publicConfig.PSObject.Properties.Name | Where-Object { $_ -notin @("ClientId", "TokenBrokerUrl", "RedirectUri") }) { throw "Unexpected OAuth config fields" }
    Copy-Item "$PSScriptRoot\oauth-public.json" "$root\artifacts\installer-payload\oauth-public.json"
} elseif (Test-Path "$root\artifacts\installer-payload\oauth-public.json") {
    Remove-Item -LiteralPath "$root\artifacts\installer-payload\oauth-public.json"
}
& $Compiler "$PSScriptRoot\AnimeWatchka.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$version = ([xml](Get-Content "$root\AnimeApp\AnimeApp.csproj")).Project.PropertyGroup.Version
Get-FileHash "$root\artifacts\installer\AnimeWatchka-Setup-$version-win-x64.exe" -Algorithm SHA256 | Format-List

