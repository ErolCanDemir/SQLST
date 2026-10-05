# SQLST çökme kanıtı toplayıcı (v22-S4, çok ajanlı çökme denetiminin çıktısı).
#
# NEDEN: dört tur boyunca çökmenin NEREDE olduğunu bilemedik, çünkü çöken makinenin
# kanıtına hiç bakmadık. Bu betik o boşluğu kapatır. YÖNETİCİ HAKKI GEREKTİRMEZ.
#
# Kullanım:
#   1) Çökmeden ÖNCE bir kez:   powershell -ExecutionPolicy Bypass -File kanit-topla.ps1 -Hazirla
#   2) Çöktükten SONRA:         powershell -ExecutionPolicy Bypass -File kanit-topla.ps1
#      → Masaüstüne sqlst-kanit.zip bırakır.
param(
    [switch]$Hazirla
)

$ErrorActionPreference = "Continue"
$logKlasoru  = Join-Path $env:APPDATA "SQLST\logs"
$dumpKlasoru = Join-Path $env:LOCALAPPDATA "CrashDumps"

if ($Hazirla) {
    # WER yerel dump: sert ölümde (StackOverflow / native / FailFast) geriye kalan TEK artefakt.
    # HKCU altında olduğu için yönetici gerekmez.
    $k = "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\SQLST.exe"
    New-Item -Path $k -Force | Out-Null
    New-ItemProperty -Path $k -Name DumpFolder -PropertyType ExpandString `
        -Value "%LOCALAPPDATA%\CrashDumps" -Force | Out-Null
    New-ItemProperty -Path $k -Name DumpType  -PropertyType DWord -Value 2 -Force | Out-Null
    New-ItemProperty -Path $k -Name DumpCount -PropertyType DWord -Value 5 -Force | Out-Null
    New-Item -ItemType Directory -Force -Path $dumpKlasoru | Out-Null

    Write-Host "HAZIR. Cokme dokumu acildi -> $dumpKlasoru"
    Write-Host "Not: 1 GB'lik surecte dokum dosyasi 1 GB'i asabilir, disk alani birakin."
    Write-Host "Simdi SQLST'yi normal kullanin; coktukten SONRA bu betigi PARAMETRESIZ calistirin."
    return
}

$hedef = Join-Path $env:TEMP "sqlst-kanit"
Remove-Item $hedef -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $hedef | Out-Null

# 1) Faz izi + donma izi — çökmenin HANGİ FAZDA olduğunu tek satırda söyleyen kayıt.
foreach ($ad in @("faz.txt", "donma.txt", "son-cokme.txt")) {
    $p = Join-Path $logKlasoru $ad
    if (Test-Path $p) { Copy-Item $p $hedef -Force }
}

# 2) Uçuş kaydedici (son 3 gün yeter; hepsi gereksiz yere büyük olabilir)
if (Test-Path $logKlasoru) {
    Get-ChildItem $logKlasoru -Filter "sqlst-*.log" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 3 |
        ForEach-Object { Copy-Item $_.FullName $hedef -Force }
}

# 3) Çökme dökümleri
if (Test-Path $dumpKlasoru) {
    Get-ChildItem $dumpKlasoru -Filter "*.dmp" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 2 |
        ForEach-Object { Copy-Item $_.FullName $hedef -Force }
}

# 4) Windows olay kayıtları — olum SINIFINI tek basina veren kayit
#    (1000/0xC00000FD = StackOverflow · 0xC0000005 = native · 0xE0434352 = yonetilen · 1002 = Hang)
try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-4) } -ErrorAction Stop |
        Where-Object { $_.ProviderName -match 'Application Error|Application Hang|\.NET Runtime|Resource-Exhaustion' } |
        Format-List TimeCreated, ProviderName, Id, Message |
        Out-File (Join-Path $hedef "olay-gunlugu.txt") -Encoding utf8
} catch {
    "Olay gunlugu okunamadi: $_" | Out-File (Join-Path $hedef "olay-gunlugu.txt") -Encoding utf8
}

# 5) Ortam özeti — hangi SÜRÜMÜN çöktüğü bu turda kritik çıktı
$ozet = @()
$ozet += "Toplama zamani : $(Get-Date -Format 'dd.MM.yyyy HH:mm:ss')"
$ozet += "Makine / kullanici: $env:COMPUTERNAME / $env:USERNAME"
$ozet += "Windows        : $([Environment]::OSVersion.VersionString)"
$ozet += "RAM (GB)       : $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,1))"
foreach ($yol in @("$env:LOCALAPPDATA\Programs\SQLST\SQLST.exe", "$env:ProgramFiles\SQLST\SQLST.exe")) {
    if (Test-Path $yol) {
        $v = (Get-Item $yol).VersionInfo
        $ozet += "SQLST          : $yol"
        $ozet += "SQLST surum    : $($v.FileVersion) / $($v.ProductVersion)"
    }
}
$fazDosyasi = Join-Path $logKlasoru "faz.txt"
$ozet += "faz.txt        : " + $(if (Test-Path $fazDosyasi) { Get-Content $fazDosyasi -Raw } else { "YOK (bu surumde faz izi henuz yok)" })
$ozet | Out-File (Join-Path $hedef "ozet.txt") -Encoding utf8

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "sqlst-kanit.zip"
Compress-Archive -Path "$hedef\*" -DestinationPath $zip -Force

Write-Host ""
Write-Host "HAZIR -> $zip"
Write-Host ""
Write-Host "--- ozet.txt ---"
Get-Content (Join-Path $hedef "ozet.txt")
