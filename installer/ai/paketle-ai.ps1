# SQLST AI kurulum paketi (v21-S4) - 2 DOSYA: Inno AI installer + GGUF sidecar.
# Windows'ta calisabilir tek .exe 4 GB'i gecemez (PE/loader siniri; model tek basina 4.36 GB) - bu
# yuzden 7-Zip SFX yaklasimi (paketle-ai-sfx.ps1, kur.ps1) BIRAKILDI (gercek cift-tik testi 2026-08-09:
# 6 GB SFX "bu uygulama calisamiyor" verdi). Cozum: ~1.65 GB Inno AI installer + model YANINDA (sidecar).
# ASCII-only (PS 5.1 BOM'suz UTF-8'i ANSI okur; Turkce mojibake). Bu betik DEV'de calisir.
# Onkosul: derle.ps1 publish uretmis; ai-hazirla.ps1 GGUF + OllamaSetup hazirlamis.
$ErrorActionPreference = "Stop"
$ai        = $PSScriptRoot
$installer = Split-Path $ai -Parent
$kok       = Split-Path $installer -Parent
$surum     = (Select-String -Path (Join-Path $installer "sqlst.iss") -Pattern '#define Surum "([\d.]+)"').Matches.Groups[1].Value

Write-Host "SQLST AI kurulum paketi (2 dosya) - surum $surum"

# On kosullar
if (-not (Test-Path (Join-Path $kok "publish\SQLST.App.exe"))) { throw "publish yok - once derle.ps1 calistirin." }
$gguf = Get-ChildItem $ai -Filter "*.gguf" | Select-Object -First 1
if (-not $gguf) { throw "GGUF yok - once ai-hazirla.ps1 calistirin." }
if (-not (Test-Path (Join-Path $ai "OllamaSetup.exe"))) { throw "OllamaSetup.exe yok - once ai-hazirla.ps1 calistirin." }
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup bulunamadi (ISCC.exe)" }

# 1) AI Inno installer derle (AiVar=1): app + Ollama + Modelfile + ai-kur.ps1 + Yerel config (GGUF HARIC)
# NOT: buyuk (~1.6 GB) exe'de Inno'nun son "EndUpdateResource" damgalamasi Windows Defender
# gercek-zamanli taramasi yuzunden ARADA BIR "failed (110)" verir (transient kilit; out klasorunu
# antivirusten haric tutamiyorsak - admin yok). Cozum: 3'e kadar YENIDEN DENE (2026-08-09 gozlemi).
$iss = Join-Path $installer "sqlst.iss"
$denendi = 0
do {
  $denendi++
  Write-Host ("  AI Inno installer derleniyor (ISCC /DAiVar=1) - deneme {0}..." -f $denendi)
  & $iscc "/DAiVar=1" $iss | Select-Object -Last 1
  if ($LASTEXITCODE -eq 0) { break }
  Write-Host "  ISCC basarisiz (Defender EndUpdateResource kilidi olabilir); 3 sn sonra yeniden..."
  Start-Sleep -Seconds 3
} while ($denendi -lt 3)
if ($LASTEXITCODE -ne 0) { throw "AI ISCC $denendi denemede basarisiz (out klasorunu antivirusten haric tutmayi deneyin)" }
$aiExe = Join-Path $installer "out\SQLST-AI-Kurulum-$surum.exe"
if (-not (Test-Path $aiExe)) { throw "AI installer uretilemedi: $aiExe" }
$mb = (Get-Item $aiExe).Length / 1MB
Write-Host ("  AI installer: {0:N0} MB" -f $mb)
if ($mb -gt 4000) { Write-Warning ("AI installer 4 GB'i asti ({0:N0} MB) - Windows'ta CALISMAYABILIR!" -f $mb) }

# 2) Teslim klasoru: exe + GGUF sidecar (kullanici bu klasoru kopyalar, exe'ye cift-tiklar)
$teslim = Join-Path $installer "out\SQLST-AI-Kurulum-$surum"
if (Test-Path $teslim) { Remove-Item $teslim -Recurse -Force }
New-Item -ItemType Directory -Force $teslim | Out-Null
Copy-Item $aiExe $teslim
# GGUF sidecar: hardlink (ayni disk -> aninda, ekstra yer yemez); olmazsa kopyala.
$ggufHedef = Join-Path $teslim $gguf.Name
try { New-Item -ItemType HardLink -Path $ggufHedef -Target $gguf.FullName -ErrorAction Stop | Out-Null; Write-Host "  GGUF sidecar (hardlink) eklendi" }
catch { Write-Host "  GGUF kopyalaniyor (hardlink olmadi, birkac dk)..."; Copy-Item $gguf.FullName $ggufHedef }

# 3) OKU.txt (ASCII - kullaniciya 2-dosya aciklamasi)
$oku = @"
SQLST AI Kurulumu (surum $surum) - 2 DOSYA
==========================================
Bu klasorde IKI dosya var ve IKISI de ayni klasorde durmali:
  1) SQLST-AI-Kurulum-$surum.exe   (kurulum)
  2) $($gguf.Name)   (yerel yapay zeka modeli)

KURULUM: Bu klasoru is makinesine kopyalayin, sonra .exe'ye CIFT-TIKLAYIN.
Kurulum SQLST'yi + yerel yapay zekayi (Ollama + model) kurar; model yanindaki
.gguf'tan alinir. Internet GEREKMEZ. Birkac dakika surer (model iceri alinir).

NEDEN 2 DOSYA: Windows'ta calisabilir tek bir .exe 4 GB'i gecemez; yapay zeka modeli
tek basina 4.36 GB. Bu yuzden model, kurulumun yaninda ayri dosya olarak gelir.
Yapay zekayi bir kez kurmaniz yeterli (degismedikce tekrar gerekmez).
"@
[IO.File]::WriteAllText((Join-Path $teslim "OKU.txt"), $oku)

$topMb = (Get-ChildItem $teslim -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("HAZIR (teslim klasoru): {0}  (toplam {1:N0} MB)" -f $teslim, $topMb)
Get-ChildItem $teslim | ForEach-Object { "  - {0}  ({1:N0} MB)" -f $_.Name, ($_.Length / 1MB) }
