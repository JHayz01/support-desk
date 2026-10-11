param([string]$Dotnet='dotnet',[switch]$Publish)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$version=([xml](Get-Content (Join-Path $root "src/SupportDesk.Desktop/SupportDesk.Desktop.csproj") -Raw)).Project.PropertyGroup.Version
& $Dotnet build (Join-Path $root 'tests/SupportDesk.Tests/SupportDesk.Tests.csproj') -c Release --nologo
if($LASTEXITCODE){throw 'Build failed'}
$testOutput=& $Dotnet (Join-Path $root 'tests/SupportDesk.Tests/bin/Release/net10.0-windows/SupportDesk.Tests.dll')
$testOutput | Write-Output
if($LASTEXITCODE){throw 'Tests failed'}
$fixture=($testOutput | Where-Object {$_ -like 'Integration fixture: *'}).Substring(21)
$env:SUPPORT_DOTNET=$Dotnet
try { python (Join-Path $root 'tests/run_integration.py') $fixture } finally { Remove-Item Env:SUPPORT_DOTNET }
if($LASTEXITCODE){throw 'API integration failed'}
& $Dotnet publish (Join-Path $root 'tests/UpdateProbe/UpdateProbe.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o (Join-Path $root 'artifacts/update-probe') --nologo
if($LASTEXITCODE){throw 'Updater probe build failed'}
$previousDotnetRoot=$env:DOTNET_ROOT_X64
$env:DOTNET_ROOT_X64=Split-Path (Get-Command $Dotnet).Source
try { & $Dotnet (Join-Path $root 'tests/SupportDesk.Tests/bin/Release/net10.0-windows/SupportDesk.Tests.dll') --update-probe (Join-Path $root 'artifacts/update-probe/CristalixSupport.exe') } finally { $env:DOTNET_ROOT_X64=$previousDotnetRoot }
if($LASTEXITCODE){throw 'Updater tests failed'}
if($Publish){
 & $Dotnet publish (Join-Path $root 'src/SupportDesk.Desktop/SupportDesk.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $root 'artifacts/windows') --nologo
 if($LASTEXITCODE){throw 'Desktop publish failed'}
 & $Dotnet publish (Join-Path $root 'src/SupportDesk.Api/SupportDesk.Api.csproj') -c Release -r linux-x64 --self-contained true -o (Join-Path $root 'artifacts/api') --nologo
 if($LASTEXITCODE){throw 'API publish failed'}
 Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $root 'artifacts/windows/README.md') -Force
 $files=@('CristalixSupport.exe','connection.json','release.json','README.md') | ForEach-Object {Join-Path $root "artifacts/windows/$_"}
 $archive=Join-Path $root "artifacts/support-desk-windows-v$version.zip"
 Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
 $version | Set-Content -LiteralPath (Join-Path $root "artifacts/release-version.txt") -Encoding ascii
 $digest=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
 "$digest  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath (Join-Path $root 'artifacts/SHA256SUMS.txt') -Encoding ascii
}
