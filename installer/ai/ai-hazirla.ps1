# SQLST tam (AI'li) kurulum icin varliklari HAZIRLAR (v21-S3) - derlemeden ONCE bir kez calistirilir.
# ASCII-ONLY (PS 5.1 .ps1'i BOM'suz UTF-8'i ANSI okur -> Turkce/em-dash mojibake + parse hatasi;
# bu betik ve ai-kur.ps1 install-zamani calistigindan SAF ASCII yazilir - CLAUDE.md tuzagi).
#
# Gorev: (1) qwen2.5-coder:7b GGUF'unu bu klasore koy, (2) OllamaSetup.exe'yi bu klasore koy.
# Ikisi de installer\ai altina gelince derle.ps1 otomatik "AI'li tek kurulum" uretir (~5,5 GB);
# yoksa normal ~144 MB kurulum cikar. Bu iki dosya GIT'TE DEGILDIR (buyuk).
#
# Kullanim (internetli hazirlik makinesinde):  powershell -File installer\ai\ai-hazirla.ps1
$ErrorActionPreference = "Stop"
$ai = $PSScriptRoot
$model = "qwen2.5-coder:7b"
$ggufAd = "qwen2.5-coder-7b-q4_k_m.gguf"

Write-Host "1/2 GGUF hazirlaniyor ($model -> $ggufAd)..."
$hedefGguf = Join-Path $ai $ggufAd
if (Test-Path $hedefGguf) {
    Write-Host "  zaten var, atlaniyor."
} else {
    $manifest = Join-Path $env:USERPROFILE ".ollama\models\manifests\registry.ollama.ai\library\qwen2.5-coder\7b"
    if (-not (Test-Path $manifest)) { throw "Model manifesti yok - once 'ollama pull qwen2.5-coder:7b' calistirin." }
    $j = Get-Content $manifest -Raw | ConvertFrom-Json
    $katman = $j.layers | Where-Object { $_.mediaType -like "*model*" } | Select-Object -First 1
    if (-not $katman) { throw "GGUF katmani manifest'te bulunamadi." }
    $blob = Join-Path $env:USERPROFILE (".ollama\models\blobs\" + ($katman.digest -replace ":", "-"))
    if (-not (Test-Path $blob)) { throw "GGUF blob'u yok: $blob" }
    Write-Host ("  blob kopyalaniyor ({0:N2} GB)..." -f ((Get-Item $blob).Length/1GB))
    Copy-Item $blob $hedefGguf
    Write-Host "  tamam."
}

Write-Host "2/2 OllamaSetup.exe kontrolu..."
$setup = Join-Path $ai "OllamaSetup.exe"
if (Test-Path $setup) {
    Write-Host "  zaten var, atlaniyor."
} else {
    Write-Host "  YOK - OllamaSetup.exe'yi https://ollama.com/download/OllamaSetup.exe adresinden"
    Write-Host "  indirip '$setup' konumuna koyun (internetli makinede, bir kez)."
}

Write-Host ""
Write-Host "Hazir. Simdi: powershell -File installer\derle.ps1  -> AI'li tek kurulum uretir."
