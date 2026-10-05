# SQLST kurulum paketini uretir: once publish, sonra Inno Setup derlemesi.
# Kullanim: powershell -File installer\derle.ps1
$ErrorActionPreference = "Stop"
$kok = Split-Path $PSScriptRoot -Parent

Write-Host "1/2 publish (self-contained tek exe)..."
dotnet publish "$kok\src\SQLST.App" -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o "$kok\publish" -v q
if ($LASTEXITCODE -ne 0) { throw "publish basarisiz" }

Write-Host "2/2 Inno Setup derlemesi..."
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup bulunamadi (winget install -e --id JRSoftware.InnoSetup --scope user)" }

& $iscc "$PSScriptRoot\sqlst.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC basarisiz" }

Get-ChildItem "$PSScriptRoot\out\*.exe" | ForEach-Object {
  Write-Host ("HAZIR: {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}
