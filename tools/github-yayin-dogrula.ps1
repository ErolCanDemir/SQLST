# GitHub yayin dogrulamasi (SQLST) - her surumden SONRA zorunlu son adim.
# Kullanim: powershell -File tools\github-yayin-dogrula.ps1 -Surum 0.30.0
# Neden: 0.30.0'da surum sayfasi dogruydu ama README 0.29.0 linkleriyle kaldi (kullanici fark etti, 14 Eki 2026).
# Bir madde bile KIRMIZIysa yayin BITMEMIS sayilir. ASCII-only (PS 5.1 BOM'suz UTF-8'i ANSI okur).
param([Parameter(Mandatory = $true)][string]$Surum)
$ErrorActionPreference = "Stop"
$gh = "$env:LOCALAPPDATA\gh-cli\bin\gh.exe"
$repo = "ErolCanDemir/SQLST"
$kok = Split-Path $PSScriptRoot -Parent
$yayin = Join-Path (Split-Path $kok -Parent) "SQLST-github"
$hata = 0
function Kontrol($ad, $ok, $ayrinti) {
  if ($ok) { Write-Host ("[OK]   " + $ad + "  " + $ayrinti) -ForegroundColor Green }
  else { Write-Host ("[HATA] " + $ad + "  " + $ayrinti) -ForegroundColor Red; $script:hata++ }
}

# 1) Latest surum bu mu
$latest = & $gh release view -R $repo --json tagName -q .tagName
Kontrol "Latest surum" ($latest -eq "v$Surum") "github=$latest beklenen=v$Surum"

# 2) Uc dosya yuklu ve boyutlari yerelle ayni
$yerel = @{
  "SQLST-Kurulum-$Surum.exe"            = Join-Path $kok "installer\out\SQLST-Kurulum-$Surum.exe"
  "SQLST-AI-Kurulum-$Surum.exe"         = Join-Path $kok "installer\out\SQLST-AI-Kurulum-$Surum.exe"
  "SQLST-Kullanim-Kilavuzu-$Surum.pdf"  = Join-Path $kok "docs\SQLST-Kullanim-Kilavuzu-$Surum.pdf"
}
# PS 5.1 native arguman gecisi jq ifadesindeki cift tirnaklari yutar -> ham JSON alip PowerShell'de ayristir
$uzak = (& $gh release view "v$Surum" -R $repo --json assets | Out-String | ConvertFrom-Json).assets
foreach ($ad in $yerel.Keys) {
  $a = $uzak | Where-Object { $_.name -eq $ad }
  $yb = if (Test-Path $yerel[$ad]) { (Get-Item $yerel[$ad]).Length } else { -1 }
  if (-not $a) { Kontrol "Dosya $ad" $false "GitHub'da yok"; continue }
  Kontrol "Dosya $ad" (($a.size -eq $yb) -and ($a.state -eq "uploaded")) "uzak=$($a.size) yerel=$yb durum=$($a.state)"
}

# 3) README (GitHub'daki canli hali) yeni surumu gosteriyor, eski surum linki yok
$b64 = (& $gh api "repos/$repo/readme" | Out-String | ConvertFrom-Json).content -replace "\s", ""
$readme = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
$linkler = [regex]::Matches($readme, "releases/download/v([0-9.]+)/[^)\s]+") | ForEach-Object { $_.Value }
$eski = $linkler | Where-Object { $_ -notmatch "/v$([regex]::Escape($Surum))/" }
Kontrol "README linkleri" (($linkler.Count -gt 0) -and (-not $eski)) ("$($linkler.Count) link; eski surume giden: " + ($(if ($eski) { $eski -join ', ' } else { 'yok' })))

# 4) README'deki her indirme linki aciliyor (HTTP 200)
foreach ($l in $linkler | Select-Object -Unique) {
  $kod = & curl.exe -sIL -o NUL -w "%{http_code}" "https://github.com/$repo/$l"
  Kontrol "Link $l" ($kod -eq "200") "HTTP $kod"
}

# 5) Yayin klasoru: PDF commit'te, eski PDF yok, csproj surumu dogru, origin ile esit
Push-Location $yayin
try {
  $pdfler = git ls-files docs
  Kontrol "Yayin repo PDF" (($pdfler -contains "docs/SQLST-Kullanim-Kilavuzu-$Surum.pdf") -and ($pdfler.Count -eq 1)) ("docs: " + ($pdfler -join ', '))
  $csproj = Get-Content "src\SQLST.App\SQLST.App.csproj" -Raw
  Kontrol "Yayin repo csproj" ($csproj -match "<Version>$([regex]::Escape($Surum))</Version>") ""
  git fetch -q origin
  $yerelHead = git rev-parse HEAD; $uzakHead = git rev-parse origin/main
  Kontrol "Yayin repo push" ($yerelHead -eq $uzakHead) "yerel=$($yerelHead.Substring(0,7)) origin=$($uzakHead.Substring(0,7))"
  Kontrol "Yayin repo temiz" (-not (git status --porcelain)) ""
} finally { Pop-Location }

if ($hata -eq 0) { Write-Host "`nYAYIN TAMAM: v$Surum GitHub'da eksiksiz." -ForegroundColor Green; exit 0 }
Write-Host "`nYAYIN EKSIK: $hata madde kirmizi - duzeltmeden 'bitti' denmez." -ForegroundColor Red; exit 1
