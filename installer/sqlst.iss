; SQLST kurulum script'i (Inno Setup 6)
; Eski VS "Setup Project"in modern karşılığı — derleme: installer/derle.ps1
;
; Tasarım kararı: kurulum KULLANICI-BAŞINA'dır (PrivilegesRequired=lowest) —
; admin/UAC gerektirmez; "SSMS kuramadığım kısıtlı makine" senaryosunun (00 §1)
; kurulumda da geçerli olması için. Hedef: %LOCALAPPDATA%\Programs\SQLST.
;
; KODLAMA: Bu dosya UTF-8 (BOM'lu) olmalıdır. Inno Setup, BOM yoksa .iss'i sistem ANSI
; kod sayfasıyla okur ve Türkçe karakterler kurulum sihirbazında bozulur (kullanıcı bulgusu
; 2026-07-27: "saçma sapan görünüyor"). derle.ps1 ve düzenleme araçları BOM'u korumalıdır.

#define Uygulama "SQLST"
#define Surum "0.27.0"
#define Yayinci "LST"
#define ExeAdi "SQLST.exe"

[Setup]
; AppId SABİT KALMALI — sürüm yükseltmeleri aynı uygulamanın üzerine kurulsun.
;
; ÜRÜN ADI DEĞİŞTİ (MiniSSMS → SQLST, 2026-07-20) AMA BU GUID DEĞİŞMEDİ — bilerek.
; Inno için uygulamanın kimliği ad değil, AppId'dir. Yeni bir GUID verilseydi
; SQLST, MiniSSMS'in ÜZERİNE değil YANINA kurulurdu: kullanıcıda iki ayrı giriş,
; iki kısayol ve "eskisi neden hâlâ duruyor?" sorusu kalırdı. Bu aynı ürünün yeni
; adıdır, ayrı bir ürün değil.
AppId={{7E1B4A0C-3F5D-4B9E-9C2A-51B7D4E80A16}
AppName={#Uygulama}
AppVersion={#Surum}
AppPublisher={#Yayinci}
DefaultDirName={autopf}\{#Uygulama}
DefaultGroupName={#Uygulama}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=out
; AiVar TANIMLIYSA (paketle-ai.ps1 → ISCC /DAiVar=1) AI kurulumu adiyla cikar; degilse app-only.
#ifdef AiVar
OutputBaseFilename=SQLST-AI-Kurulum-{#Surum}
#else
OutputBaseFilename=SQLST-Kurulum-{#Surum}
#endif
SetupIconFile=..\src\SQLST.App\sqlst.ico
UninstallDisplayIcon={app}\{#ExeAdi}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

; 🏠 Yerel AI paketi (v21-S4, 2026-08-09 — SFX'ten Inno'ya dönüş): AiVar TANIMLIYSA bu script AI
; kurulumu üretir (app + Ollama + Modelfile + ai-kur.ps1 + Yerel config; ~1,65 GB, 4 GB Windows
; sınırı ALTINDA). 7B modeli (4,36 GB) GÖMÜLMEZ — kurulum exe'sinin YANINA "sidecar" olarak konur
; (paketle-ai.ps1 teslim klasörü yapar). Neden: Windows'ta çalışabilir tek .exe 4 GB'ı geçemez
; (PE/loader sınırı; model tek başına 4,36 GB) — 7-Zip SFX de 32-bit stub'la >4 GB çalışmadı
; (gerçek çift-tık testi 2026-08-09). Çözüm: 2 dosya (exe + gguf); exe modeli yanındaki gguf'tan kurar.
#ifdef AiVar
[Components]
Name: "ai"; Description: "🏠 Yerel Yapay Zeka (Ollama + Qwen model — internetsiz, veri makineden çıkmaz)"; Types: full
Name: "cekirdek"; Description: "SQLST uygulaması"; Types: full compact custom; Flags: fixed
[Types]
Name: "full"; Description: "Tam kurulum (uygulama + Yerel AI)"
Name: "compact"; Description: "Yalnız uygulama"
Name: "custom"; Description: "Özel"; Flags: iscustom
#endif

[Tasks]
Name: "masaustu"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek kısayollar:"

[Files]
; publish çıktısı: self-contained tek exe (dotnet publish ... -o publish)
Source: "..\publish\SQLST.App.exe"; DestDir: "{app}"; DestName: "{#ExeAdi}"; Flags: ignoreversion
; kullanım el kitabı (V2-S10 sonrası, kullanıcı isteği)
Source: "..\docs\SQLST-Kullanim-Kilavuzu-{#Surum}.pdf"; DestDir: "{app}"; Flags: ignoreversion
; AI yapılandırması (kullanıcı isteği 2026-07-27: "kurulum config'i otomatik oluştursa"): kurulum
; asistan.config'i kullanıcının %APPDATA%\SQLST'ine koyar → AI kutudan çıkar çıkmaz çalışır.
; onlyifdoesntexist: kullanıcının mevcut (mühürlenmiş) config'ini EZMEZ.
;
; v22-S6: app-only kurulumda GEMİNİ config'i (geliştirici anahtarlı, git'te olmayan
; installer\asistan.config) ARTIK GÖMÜLMÜYOR — Gemini sağlayıcısı üründen kaldırıldı
; (kullanıcı kararı 24 Ağu 2026: "kendi ajanımız ile yapay zeka kullanıyoruz"). Onun yerine HER İKİ
; kurulum da YEREL config'i yazar. App-only'de Ollama kurulu olmayabilir; o durumda AI sekmesi
; "Ollama'ya ulaşılamadı — kurulu mu, çalışıyor mu, model çekili mi" diye ADIM ADIM yol gösterir.
; Bu, config'siz kurulumun verdiği "yapılandırılmamış, şu yola baktım" mesajından çok daha yararlı.
Source: "ai\asistan-yerel.config"; DestDir: "{userappdata}\SQLST"; DestName: "asistan.config"; Flags: onlyifdoesntexist

; 🏠 Yerel AI varlıkları (v21-S4) — AI kurulumunda (AiVar TANIMLI) paketlenir. GGUF GÖMÜLMEZ:
; kurulum exe'sinin YANINDA (sidecar) durur; ai-kur.ps1 onu {src}'den import eder (aşağıdaki [Run]).
; Böylece kurulum ~1,65 GB (Ollama+app) kalır — 4 GB Windows sınırının altında.
#ifdef AiVar
Source: "ai\Modelfile"; DestDir: "{app}\ai"; Components: ai; Flags: ignoreversion
Source: "ai\ai-kur.ps1"; DestDir: "{app}\ai"; Components: ai; Flags: ignoreversion
#if FileExists(AddBackslash(SourcePath) + "ai\OllamaSetup.exe")
; OllamaSetup zaten sıkışık installer — nocompression: lzma2'nin 1,5 GB'ı boşuna sıkmasını önler (hız).
Source: "ai\OllamaSetup.exe"; DestDir: "{app}\ai"; Components: ai; Flags: ignoreversion nocompression
#endif
#endif

[Icons]
Name: "{autoprograms}\{#Uygulama}"; Filename: "{app}\{#ExeAdi}"
Name: "{autoprograms}\{#Uygulama} El Kitabı"; Filename: "{app}\SQLST-Kullanim-Kilavuzu-{#Surum}.pdf"
Name: "{autodesktop}\{#Uygulama}"; Filename: "{app}\{#ExeAdi}"; Tasks: masaustu

[Registry]
; 📂 .sql "Birlikte aç" kaydı (v23-S7, kullanıcı bulgusu 28 Eyl 2026: "var olan bir sql dosyasını
; bizde açamıyoruz"): SQLST, Gezgin'in "Birlikte aç" listesine girer — VARSAYILAN İŞLEYİCİ
; YAPILMAZ (SSMS/kullanıcı tercihi çalınmaz; kullanıcı isterse "Her zaman bu uygulamayı kullan"
; der). Kullanıcı-başına kurulum → HKCU\Software\Classes (admin gerekmez). Uygulama tarafı:
; App.OnStartup argümandaki .sql/.txt'yi alır, bağlantı kurulunca sekmede açar.
Root: HKCU; Subkey: "Software\Classes\SQLST.sql"; ValueType: string; ValueData: "SQL betiği (SQLST)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SQLST.sql\DefaultIcon"; ValueType: string; ValueData: "{app}\{#ExeAdi},0"
Root: HKCU; Subkey: "Software\Classes\SQLST.sql\shell\open\command"; ValueType: string; ValueData: """{app}\{#ExeAdi}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.sql\OpenWithProgids"; ValueName: "SQLST.sql"; ValueType: string; ValueData: ""; Flags: uninsdeletevalue

[InstallDelete]
; Ad değişimi (2026-07-20): AppId aynı olduğu için kurulum eski dizinin üzerine gelir.
; MiniSSMS adıyla bırakılmış dosyalar/kısayollar silinmezse kullanıcıda iki isim yan yana
; görünürdü — eski exe ve el kitabı artık kullanılmıyor.
Type: files; Name: "{app}\MiniSSMS.exe"
Type: files; Name: "{app}\MiniSSMS-Kullanim-Kilavuzu-*.pdf"
Type: files; Name: "{autoprograms}\MiniSSMS.lnk"
Type: files; Name: "{autoprograms}\MiniSSMS El Kitabı.lnk"
Type: files; Name: "{autodesktop}\MiniSSMS.lnk"

[Run]
; 🏠 Yerel AI kurulumu (v21-S4): Ollama'yı sessiz kur + YANDAKI (sidecar) GGUF'tan modeli kaydet.
; Birkaç dakika sürer; StatusMsg kullanıcıyı bilgilendirir. ai-kur.ps1 kendi hatasını yutar →
; AI kurulamasa bile SQLST kurulumu ÇÖKMEZ (asistan sonra yönlendirme gösterir).
#ifdef AiVar
; ai-kur.ps1'e GgufDizin={src} verilir: model, kurulum exe'sinin YANINDAKI (sidecar) .gguf'tan
; import edilir (GGUF kuruluma gömülü değil). Sihirbaz StatusMsg ilerlemeyi gösterir.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\ai\ai-kur.ps1"" -GgufDizin ""{src}"""; \
  StatusMsg: "Yerel yapay zeka kuruluyor (Ollama + model, birkaç dakika sürebilir)…"; \
  Components: ai; Flags: runhidden waituntilterminated
#endif
Filename: "{app}\{#ExeAdi}"; Description: "{#Uygulama} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; tek dosya ayıklama önbelleği kullanıcı profilinde kalabilir — kaldırırken temizlenmez (başka sürümler kullanabilir)

; 🏠 AI kurulumunda (AiVar) kullanıcı-dostu koruma: model .gguf, kurulumun YANINDA olmalı (2 dosya
; birlikte taşınır). Kullanıcı exe'yi tek başına taşırsa AI sessizce kurulmazdı — bunu BAŞTAN, net
; uyarıyla yakalıyoruz (kullanıcı isteği 2026-08-09: "kullanıcılar için zor olmasın").
#ifdef AiVar
[Code]
function InitializeSetup(): Boolean;
var
  BulRec: TFindRec;
  GgufVar: Boolean;
begin
  GgufVar := False;
  if FindFirst(ExpandConstant('{src}\*.gguf'), BulRec) then
  begin
    try
      GgufVar := True;
    finally
      FindClose(BulRec);
    end;
  end;
  if GgufVar then
    Result := True
  else
    Result := (MsgBox(
      'Yerel yapay zeka model dosyası (.gguf) kurulumun yanında bulunamadı.' + #13#10 +
      'Kurulum dosyası (.exe) ile model dosyası (.gguf) AYNI KLASÖRDE olmalı — iki dosya birlikte taşınır.' + #13#10#13#10 +
      'Devam ederseniz uygulama kurulur ama YEREL YAPAY ZEKA kurulmaz.' + #13#10 +
      'İptal edip iki dosyayı aynı klasöre koymanız önerilir.' + #13#10#13#10 +
      'Yine de devam edilsin mi?',
      mbConfirmation, MB_YESNO) = IDYES);
end;
#endif
