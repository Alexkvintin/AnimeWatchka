# Run the self-contained Windows build; no system-wide .NET installation is required.
$exe = Join-Path $PSScriptRoot 'artifacts\win-x64\AnimeApp.exe'
if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe }
else { $dotnet = if (Test-Path (Join-Path $PSScriptRoot '.dotnet/dotnet.exe')) { Join-Path $PSScriptRoot '.dotnet/dotnet.exe' } else { 'dotnet' }; & $dotnet run --project (Join-Path $PSScriptRoot 'AnimeApp/AnimeApp.csproj') }
