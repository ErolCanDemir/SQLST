# SQLST Yerel AI kurulum-sonrasi betigi (v21-S3) - kurulumun [Run] adimindan CAGRILIR.
# ASCII-ONLY: install aninda PS 5.1 ile 'powershell -File' calisir; BOM'suz UTF-8'de Turkce/em-dash
# ANSI okunup parse hatasi verir (CLAUDE.md tuzagi) -> bu betik SAF ASCII yazilir.
#
# Gorev: (1) Ollama kurulu degilse gomulu OllamaSetup.exe'yi SESSIZ kur, (2) sunucunun ayaga
# kalkmasini bekle, (3) SIDECAR GGUF'tan (kurulum exe'sinin yanindaki -GgufDizin) modeli
# 'ollama create' ile kaydet. Internet GEREKMEZ. GGUF kuruluma GOMULU DEGIL: Windows'ta calisabilir
# tek .exe 4 GB'i gecemez, model tek basina 4.36 GB -> exe'nin YANINDA ayri dosya (v21-S4, 2-dosya).
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
    if (-not $gguf) { Yaz "HATA: GGUF bulunamadi. Kurulum exe'sinin YANINDA *.gguf olmali. Aranan: '$GgufDizin' ve '$AiDizin'."; exit 0 }
    Yaz "GGUF (sidecar): $($gguf.FullName)"
    # Modelfile'in FROM'unu sidecar gguf'un MUTLAK yoluna cevir (gguf {app}\ai'de DEGIL, kurulumun
    # yaninda; ollama'nin goreli-yol belirsizligini kaldirir). Diger satirlar (PARAMETER/SYSTEM -
    # Turkce icerir) UTF-8 KORUNARAK aynen tasinir (.NET IO; Get/Set-Content PS5.1'de mojibake yapar -
    # CLAUDE.md). Ileri-slash: Windows ollama kabul eder, ters-slash kacis riski yok.
    $utf8 = New-Object Text.UTF8Encoding($false)
    $mfKaynak = Join-Path $AiDizin "Modelfile"
    $mfTemp = Join-Path $env:TEMP ("sqlst-modelfile-" + [Guid]::NewGuid().ToString("N") + ".txt")
    $satirlar = ([IO.File]::ReadAllText($mfKaynak, $utf8)) -split "`r?`n"
    $ggufYol = ($gguf.FullName -replace '\\','/')
    $yeni = New-Object System.Collections.Generic.List[string]
    foreach ($s in $satirlar) {
        if ($s -match '^\s*FROM\s') { $yeni.Add('FROM "' + $ggufYol + '"') }
        else { $yeni.Add($s) }
    }
    [IO.File]::WriteAllText($mfTemp, ($yeni -join "`r`n"), $utf8)
    Yaz "Model olusturuluyor ($($gguf.Name)) -> $Model ..."
    & $ollama create $Model -f $mfTemp 2>&1 | ForEach-Object { Yaz $_ }
    Remove-Item $mfTemp -Force -ErrorAction SilentlyContinue
    Yaz "AI kurulumu tamamlandi."
} catch {
    Yaz "BEKLENMEYEN HATA: $($_.Exception.Message)"  # kurulumu cokertme - SQLST yine acilir
}
exit 0
