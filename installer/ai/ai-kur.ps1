# SQLST Yerel AI kurulum-sonrasi betigi (v21-S3) - kurulumun [Run] adimindan CAGRILIR.
# ASCII-ONLY: install aninda PS 5.1 ile 'powershell -File' calisir; BOM'suz UTF-8'de Turkce/em-dash
# ANSI okunup parse hatasi verir (CLAUDE.md tuzagi) -> bu betik SAF ASCII yazilir.
#
# Gorev: (1) Ollama kurulu degilse gomulu OllamaSetup.exe'yi SESSIZ kur, (2) sunucunun ayaga
# kalkmasini bekle, (3) modeli kaydet: SIDECAR GGUF varsa (kurulum exe'sinin yanindaki -GgufDizin)
# ondan - internet GEREKMEZ (kapali ag, USB dagitimi); YOKSA (v23-S15, GitHub'dan indirilen tek exe)
# 'ollama pull' ile internetten. GGUF kuruluma GOMULU DEGIL: Windows'ta calisabilir tek .exe 4 GB'i
# gecemez, model tek basina 4.36 GB -> exe'nin YANINDA ayri dosya (v21-S4, 2-dosya).
# Her adim kendi hatasini yutup IZ birakir (ai-kur.log) - kurulum AI yuzunden COKMEZ; AI kurulamazsa
# SQLST yine acilir, asistan "Ollama'ya ulasilamadi" yonlendirmesini gosterir (v21-S1).
param(
    [string]$AiDizin = $PSScriptRoot,
    [string]$GgufDizin = "",
    [string]$Model = "qwen2.5-coder:7b"
)

$log = Join-Path $AiDizin "ai-kur.log"
function Yaz([string]$m) { "$([DateTime]::Now.ToString('HH:mm:ss')) $m" | Tee-Object -FilePath $log -Append | Out-Null }

function OllamaExe {
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe",
        "$env:ProgramFiles\Ollama\ollama.exe")) {
        if (Test-Path $p) { return $p }
    }
    $c = Get-Command ollama -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

try {
    Yaz "AI kurulumu basladi. Dizin=$AiDizin Model=$Model"

    # 1) Ollama kurulu mu?
    $ollama = OllamaExe
    if (-not $ollama) {
        $setup = Join-Path $AiDizin "OllamaSetup.exe"
        if (-not (Test-Path $setup)) { Yaz "HATA: OllamaSetup.exe yok, Ollama kurulamiyor."; exit 0 }
        Yaz "Ollama sessiz kuruluyor (admin gerekmez)..."
        # NOT: -Wait KULLANMA. Ollama kurulumu biter bitmez kalici 'ollama app' (tray) baslatir;
        # PowerShell Start-Process -Wait bir JOB OBJECT ile TUM surec agacini bekler -> tray hic
        # kapanmadigindan SONSUZA takilir (v21 GERCEK temiz-makine testi 2026-08-09: 77 dk asili
        # kaldi, model olusmadi, kurulum bitmedi). Cozum: -PassThru ile setup PROCESSINI sinirli
        # sure bekle (tray'i DEGIL), sonra ollama.exe olusana kadar poll et.
        $sp = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART" -PassThru
        try { $sp.WaitForExit(180000) | Out-Null } catch {}
        $ollama = $null
        for ($j = 0; $j -lt 60; $j++) {
            $ollama = OllamaExe
            if ($ollama) { break }
            Start-Sleep -Seconds 2
        }
        if (-not $ollama) { Yaz "HATA: Ollama kurulumdan sonra bulunamadi (180 sn + 120 sn poll)."; exit 0 }
    }
    Yaz "Ollama: $ollama"

    # 2) Sunucu ayakta mi? (Ollama kurulunca arka plan uygulamasi otomatik baslar; yine de bekle)
    $sunucu = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            Invoke-WebRequest -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3 -UseBasicParsing | Out-Null
            $sunucu = $true; break
        } catch {
            if ($i -eq 0) { Start-Process -FilePath $ollama -ArgumentList "serve" -WindowStyle Hidden }
            Start-Sleep -Seconds 2
        }
    }
    Yaz ("Sunucu hazir: {0}" -f $sunucu)

    # 3) Model zaten kayitli mi? Degilse gomulu GGUF'tan olustur.
    $mevcut = & $ollama list 2>$null | Select-String -SimpleMatch $Model
    if ($mevcut) {
        Yaz "Model zaten kayitli: $Model - atlaniyor."
        exit 0
    }
    # GGUF: once GgufDizin (kurulum exe'sinin yani - SIDECAR), sonra AiDizin (geriye donuk uyum)
    $gguf = $null
    foreach ($d in @($GgufDizin, $AiDizin)) {
        if ($d -and (Test-Path $d)) {
            $g = Get-ChildItem -Path $d -Filter "*.gguf" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($g) { $gguf = $g; break }
        }
    }
    # v23-S15 (GitHub yayini, kullanici karari 5 Eki 2026): GGUF YANINDA YOKSA internetten indir.
    # GitHub Releases dosya basina 2 GB kabul eder; 4.36 GB'lik GGUF oraya konamaz -> AI kurulum exe'si
    # tek basina dagitilir, model resmi Ollama kutuphanesinden 'ollama pull' ile gelir. Kutuphanedeki
    # qwen2.5-coder:7b varsayilan nicemlemesi Q4_K_M = sidecar GGUF ile AYNI agirliklar. Internet yoksa
    # iz birakip cikar (SQLST yine acilir; asistan yonlendirmesi devrede). Kapali ag: sidecar yolu aynen.
    if (-not $gguf) {
        Yaz "GGUF yaninda yok -> model internetten indiriliyor: ollama pull $Model (~4.7 GB, birkac dakika - yarim saat)..."
        # Ilerleme animasyonu (ANSI) gunluge YAZILMAZ - tek satira 80+ KB birikiyordu (gercek test
        # 5 Eki 2026); yalniz hata/sonuc satirlari kalir.
        & $ollama pull $Model 2>&1 | ForEach-Object { "$_" } |
            Where-Object { $_ -match '^\s*(Error|success)' } | ForEach-Object { Yaz $_.Trim() }
        if (-not (& $ollama list 2>$null | Select-String -SimpleMatch $Model)) {
            Yaz "HATA: model indirilemedi (internet/proxy?). SQLST kuruldu; AI sekmesi yonlendirme gosterir."
            exit 0
        }
        Yaz "Model indirildi: $Model"
    }
    # Modelfile'in FROM satirini cevir: sidecar varsa GGUF'un MUTLAK yolu (gguf {app}\ai'de DEGIL,
    # kurulumun yaninda; ollama'nin goreli-yol belirsizligini kaldirir), yoksa indirilen kutuphane modeli
    # (FROM <model> -> SQLST ayarlari - sicaklik/baglam/SYSTEM - ayni ada islenir; iki yolda da sonuc ayni
    # ayarli model). Diger satirlar (PARAMETER/SYSTEM - Turkce icerir) UTF-8 KORUNARAK aynen tasinir
    # (.NET IO; Get/Set-Content PS5.1'de mojibake yapar - CLAUDE.md). Ileri-slash: Windows ollama kabul eder.
    $utf8 = New-Object Text.UTF8Encoding($false)
    $mfKaynak = Join-Path $AiDizin "Modelfile"
    $mfTemp = Join-Path $env:TEMP ("sqlst-modelfile-" + [Guid]::NewGuid().ToString("N") + ".txt")
    $satirlar = ([IO.File]::ReadAllText($mfKaynak, $utf8)) -split "`r?`n"
    if ($gguf) {
        Yaz "GGUF (sidecar): $($gguf.FullName)"
        $kaynak = 'FROM "' + ($gguf.FullName -replace '\\','/') + '"'
    } else {
        $kaynak = "FROM $Model"
    }
    $yeni = New-Object System.Collections.Generic.List[string]
    foreach ($s in $satirlar) {
        if ($s -match '^\s*FROM\s') { $yeni.Add($kaynak) }
        else { $yeni.Add($s) }
    }
    [IO.File]::WriteAllText($mfTemp, ($yeni -join "`r`n"), $utf8)
    Yaz "Model ayarlariyla kaydediliyor ($kaynak) -> $Model ..."
    & $ollama create $Model -f $mfTemp 2>&1 | ForEach-Object { Yaz $_ }
    Remove-Item $mfTemp -Force -ErrorAction SilentlyContinue
    Yaz "AI kurulumu tamamlandi."
} catch {
    Yaz "BEKLENMEYEN HATA: $($_.Exception.Message)"  # kurulumu cokertme - SQLST yine acilir
}
exit 0
