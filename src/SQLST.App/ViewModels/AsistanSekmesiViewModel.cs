using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.ViewModels;

/// <summary>
/// 🤖 Asistan sekmesi (v11-S1 — "müşteri için en can alıcı nokta", kullanıcı 2026-07-25).
/// S1 iskeleti: serbest soru + ŞEMA bağlamı + isteğe bağlı "editördeki sorgu" eklentisi.
/// GİZLİLİK RAYI: modele giden istem <see cref="IstemOnizleme"/>'de AYNEN görülebilir
/// ("Show Prompt" — Show Script kültürünün AI karşılığı); satır verisi asla gitmez.
/// Üretilen sorgular ASLA otomatik çalıştırılmaz — kullanıcı kopyalar/sekmeye alır.
/// </summary>
public sealed partial class AsistanSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    private readonly IAsistanServisi _servis;
    private readonly Func<AsistanAyarlari> _ayarlarGetir;
    private readonly Func<Task<SemaOnbellegi?>> _semaGetir;
    private readonly Func<MotorTuru?> _motorGetir;
    private readonly Func<string?> _editorMetniGetir;
    private readonly Func<string?> _hataGetir;
    private CancellationTokenSource? _cts;

    public AsistanSekmesiViewModel(
        IAsistanServisi servis,
        Func<AsistanAyarlari> ayarlarGetir,
        Func<Task<SemaOnbellegi?>> semaGetir,
        Func<MotorTuru?> motorGetir,
        Func<string?> editorMetniGetir,
        Func<string?>? hataGetir = null,
        Func<string?>? planMetniGetir = null)
    {
        _servis = servis;
        _ayarlarGetir = ayarlarGetir;
        _semaGetir = semaGetir;
        _motorGetir = motorGetir;
        _editorMetniGetir = editorMetniGetir;
        _hataGetir = hataGetir ?? (() => null);
        _planMetniGetir = planMetniGetir ?? (() => null);
    }

    private readonly Func<string?> _planMetniGetir;

    /// <summary>v11-S5b: son açılan PLAN sekmesini yorumlat — neden yavaş, hangi index yardım eder.</summary>
    [RelayCommand(CanExecute = nameof(Sorabilir))]
    private async Task PlaniYorumlat()
    {
        string? planMetni = _planMetniGetir();
        if (string.IsNullOrWhiteSpace(planMetni))
        {
            Bilgi = "Yorumlanacak plan yok — önce bir sorgunun execution plan'ını al (sol ray → Plan).";
            return;
        }

        await IstemKurVeGonderAsync(_ => AsistanIstemleri.PlanYorumla(
            planMetni, AsistanIstemleri.MotorAdi(_motorGetir())),
            baglamMetni: null); // PlanYorumla şemayı kullanmaz (ozet '_' ile atılır)
    }

    /// <summary>Sohbet balonu (v11-S7): kullanıcı sağda, asistan solda; Sorgu doluysa balonda "Sekmede aç".</summary>
    /// <param name="Dusunuyor">Bekleme balonu (2026-07-28): cevap gelene kadar AI logomuzla "düşünüyor"
    /// gösterir; metin/sorgu taşımaz, cevap gelince koleksiyondan çıkarılır.</param>
    public sealed record AsistanMesaji(bool KullaniciMi, string Metin, string? Sorgu = null, bool Dusunuyor = false);

    /// <summary>Konuşma geçmişi (S7 sohbet arayüzü) — oturum içi, kalıcı değil.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AsistanMesaji> Mesajlar { get; } = [];

    /// <summary>Balondaki "Sekmede aç": o mesajın sorgusunu yeni sekmeye alır (çalıştırmaz).</summary>
    [RelayCommand]
    private void MesajSorgusunuAc(AsistanMesaji? mesaj)
    {
        if (mesaj?.Sorgu is { Length: > 0 } sorgu && SekmeyeAc is not null)
        {
            SekmeyeAc(_motorGetir() == MotorTuru.Mongo ? "asistan.json" : "asistan.sql", sorgu);
            Bilgi = "Sorgu yeni sekmede açıldı — inceleyip F5 ile çalıştır.";
        }
    }

    public string Baslik => "🤖 Asistan";
    public SekmeDurumu Durum => Mesgul ? SekmeDurumu.Calisiyor : SekmeDurumu.Tamamlandi;

    /// <summary>Ayıklanan sorguyu yeni sorgu sekmesinde açan köprü (başlık, sorgu) — MainViewModel bağlar (S3).</summary>
    public Action<string, string>? SekmeyeAc { get; set; }

    /// <summary>Cevaptan ayıklanan sorgu (S3) — null ise "Sekmede aç" düğmesi görünmez.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SekmedeAcGorunur))]
    private string? _cevaptakiSorgu;

    public bool SekmedeAcGorunur => !string.IsNullOrWhiteSpace(CevaptakiSorgu);

    /// <summary>S3: ayıklanan sorguyu yeni sekmede açar — ÇALIŞTIRMAZ (kullanıcı inceleyip F5 der).</summary>
    [RelayCommand]
    private void SekmedeAc()
    {
        if (CevaptakiSorgu is { Length: > 0 } sorgu && SekmeyeAc is not null)
        {
            SekmeyeAc(_motorGetir() == MotorTuru.Mongo ? "asistan.json" : "asistan.sql", sorgu);
            Bilgi = "Sorgu yeni sekmede açıldı — inceleyip F5 ile çalıştır.";
        }
    }

    [ObservableProperty] private string _soru = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Durum))]
    [NotifyCanExecuteChangedFor(nameof(SorCommand))]
    [NotifyCanExecuteChangedFor(nameof(SorguyuDegerlendirCommand))]
    [NotifyCanExecuteChangedFor(nameof(HataCozdurCommand))]
    [NotifyCanExecuteChangedFor(nameof(SorguAciklaCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlaniYorumlatCommand))]
    private bool _mesgul;

    /// <summary>Model cevabı (düz metin; içindeki sorgu kullanıcı tarafından kopyalanır).</summary>
    [ObservableProperty] private string _cevap = "";

    /// <summary>Show Prompt: modele gidecek/giden istem metni — AYNEN (gölge kopya yok).</summary>
    [ObservableProperty] private string _istemOnizleme = "";

    /// <summary>
    /// Editördeki sorgu isteme eklensin mi. VARSAYILAN KAPALI (v22-S7, kullanıcı kararı
    /// 24 Ağu 2026): açıkken başka bir sekmenin sorgusu — üstelik "en son uğranan" sekmenin —
    /// kullanıcının yazdığı metne karışıyordu. Bilinçli bir tercih olmalı, sessiz varsayılan değil.
    /// Bedeli de var: ölçümde editör sorgusu eklendiğinde istem 1.604 token → 131 sn'ye çıkıyor.
    /// </summary>
    [ObservableProperty] private bool _editordekiSorguyuEkle;

    /// <summary>
    /// ⏱ Bekleme sayacı (v22-S6, kullanıcı bulgusu: "merhaba yazıyorum CEVAP VERMİYOR, çok uzun").
    /// Eskiden bekleme balonu + sabit "Yanıt hazırlanıyor…" vardı; 1 dakika boyunca ekranda HİÇBİR
    /// ŞEY değişmediği için çalışan istek ile takılmış istek ayırt edilemiyordu — kullanıcının
    /// gördüğü "cevap vermiyor" tam olarak buydu. KDS'te aynı sorun saniye sayacıyla çözülmüştü.
    /// Boş dizge = beklemiyoruz (arayüz sayacı gizler).
    /// </summary>
    [ObservableProperty] private string _bekleyenSure = "";

    /// <summary>
    /// Sayaç metni — saf ve test edilebilir (zamanlayıcıdan bağımsız). 60 sn'ye kadar "12 sn",
    /// sonrası "1 dk 05 sn": dakikaya geçince salt saniye ("83 sn") okunması zorlaşıyor ve uzun
    /// bekleme algısını gizliyor.
    /// </summary>
    public static string SureMetni(TimeSpan gecen)
    {
        int saniye = (int)gecen.TotalSeconds;
        return saniye < 60 ? $"{saniye} sn" : $"{saniye / 60} dk {saniye % 60:00} sn";
    }

    /// <summary>
    /// Bekleme süresini saniyede bir tazeler. Havuz iş parçacığında döner (UI'ı meşgul etmez);
    /// WPF bağlama motoru düz özellik değişimini kendi thread'ine taşıdığı için güvenlidir —
    /// <see cref="Mesajlar"/> gibi KOLEKSİYONA buradan dokunulmaz.
    /// </summary>
    private async Task SureSayaciAsync(CancellationToken ct)
    {
        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        BekleyenSure = SureMetni(TimeSpan.Zero); // ilk saniye boyunca boş görünmesin
        using var tik = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // ⚠ Token'ı WaitForNextTickAsync'e VERMİYORUZ: aşırı yük iptalde OperationCanceledException
        // FIRLATIR (denendi — sayacın normal bitişi tüm asistan testlerini kırdı). Zamanlayıcıyı
        // dispose etmek ise bekleyen çağrıyı sessizce false'a düşürür; iptal burada bir hata değil,
        // beklenen son olduğu için doğru mekanizma bu.
        using CancellationTokenRegistration _ = ct.Register(tik.Dispose);
        while (await tik.WaitForNextTickAsync().ConfigureAwait(false))
            BekleyenSure = SureMetni(kronometre.Elapsed);
    }

    /// <summary>
    /// 🔥 ISITMA (v22-S8): sekme açılır açılmaz istem ÖNEKİNİ (sistem + kompakt şema) modele bir kez
    /// işletir; kullanıcı sorusunu yazarken önek önbelleğe girer ve asıl soruda ~70 sn yerine ~1 sn
    /// eder. Isıtma modeli de yüklediğinden v22-S6'nın ön yüklemesinin YERİNE geçer.
    /// Gerekçe/ölçüm <see cref="IAsistanServisi.IsitAsync"/> ve
    /// <see cref="AsistanIstemleri.IstemOnegi"/> belgelerinde. Bulut sağlayıcıda no-op.
    /// </summary>
    public async Task IsitAsync(CancellationToken ct = default)
    {
        SemaOnbellegi? onbellek = await _semaGetir();
        string onek = AsistanIstemleri.IstemOnegi(
            AsistanIstemleri.SemaOzetiKompakt(onbellek),
            AsistanIstemleri.MotorAdi(_motorGetir()),
            _motorGetir());
        await _servis.IsitAsync(onek, _ayarlarGetir(), ct);
    }

    [ObservableProperty] private string _bilgi =
        "Sorunu yaz, Sor'a bas. Modele yalnız ŞEMA (tablo/kolon adları) gider — satır verisi asla. "
        + "Gönderileni \"İstem\" bölümünden aynen görebilirsin.";

    private bool Sorabilir => !Mesgul;

    [RelayCommand(CanExecute = nameof(Sorabilir))]
    private async Task Sor()
    {
        if (string.IsNullOrWhiteSpace(Soru))
        {
            Bilgi = "Önce bir soru yaz.";
            return;
        }

        string soru = Soru;
        Mesajlar.Add(new AsistanMesaji(KullaniciMi: true, soru)); // sohbet: soru balonu hemen düşer
        Soru = "";

        // 🔒 GÖNDERDİĞİN METNE SADIK KAL (v22-S7, kullanıcı kararı 24 Ağu 2026: "başka bir sekmenin
        // etkilemesine gerek yok, benim gönderdiğim metne sadık kalmalı").
        // ESKİDEN İKİ SIZINTI VARDI:
        //  1) EditordekiSorguyuEkle VARSAYILAN AÇIKTI → o sorgu istemin içine giriyordu.
        //  2) Daha sinsisi: baglamMetni HER ZAMAN editör metnini içeriyordu — anahtar KAPALIYKEN
        //     bile. Yani hangi tabloların TAM KOLON DÖKÜMÜNÜN gönderileceğine başka bir sekme karar
        //     veriyordu. Üstelik o metin "en son uğradığın sorgu sekmesinden" gelir; kullanıcı
        //     Asistan sekmesindeyken baktığı sekme bile değildir.
        // Artık ikisi de yalnız kullanıcı anahtarı AÇTIYSA devrede.
        string? editorMetni = EditordekiSorguyuEkle ? _editorMetniGetir() : null;
        await IstemKurVeGonderAsync(
            p => AsistanIstemleri.SerbestSoru(
                soru, p.Kompakt, AsistanIstemleri.MotorAdi(_motorGetir()), editorMetni,
                _motorGetir(), ilgiliKolonlar: p.Kolonlar),
            baglamMetni: string.IsNullOrWhiteSpace(editorMetni) ? soru : soru + " " + editorMetni,
            semaKapisi: soru); // veriyle ilgisi yoksa şema HİÇ gönderilmez (bkz. SemaIsteniyorMu)
    }

    /// <summary>
    /// v11-S2 (kullanıcı isteği 1): TEK TIK — soru yazmaya gerek yok; editördeki (son sorgu
    /// sekmesindeki) sorgu + şema + motor değerlendirme istemiyle modele gider.
    /// </summary>
    [RelayCommand(CanExecute = nameof(Sorabilir))]
    private async Task SorguyuDegerlendir()
    {
        string? sorgu = _editorMetniGetir();
        if (string.IsNullOrWhiteSpace(sorgu))
        {
            Bilgi = "Değerlendirilecek sorgu yok — önce bir sorgu sekmesinde sorgunu yaz.";
            return;
        }

        await IstemKurVeGonderAsync(p => AsistanIstemleri.SorguDegerlendir(
            sorgu, p.Tumu, AsistanIstemleri.MotorAdi(_motorGetir()), _motorGetir()),
            baglamMetni: sorgu);
    }

    /// <summary>v11-S4: son sorgu sekmesi hata verdiyse TEK TIK — neden + düzeltilmiş sorgu.</summary>
    [RelayCommand(CanExecute = nameof(Sorabilir))]
    private async Task HataCozdur()
    {
        string? sorgu = _editorMetniGetir();
        string? hata = _hataGetir();
        if (string.IsNullOrWhiteSpace(sorgu) || string.IsNullOrWhiteSpace(hata))
        {
            Bilgi = "Çözülecek hata yok — son sorgu sekmesinde kırmızı bir hata alınmış olmalı.";
            return;
        }

        await IstemKurVeGonderAsync(p => AsistanIstemleri.HataCozdur(
            sorgu, hata, p.Tumu, AsistanIstemleri.MotorAdi(_motorGetir()), _motorGetir()),
            baglamMetni: sorgu + " " + hata);
    }

    /// <summary>v11-S5: devralınan sorgu ne yapıyor — adım adım düz Türkçe (bakım senaryosu).</summary>
    [RelayCommand(CanExecute = nameof(Sorabilir))]
    private async Task SorguAcikla()
    {
        string? sorgu = _editorMetniGetir();
        if (string.IsNullOrWhiteSpace(sorgu))
        {
            Bilgi = "Açıklanacak sorgu yok — önce bir sorgu sekmesinde sorgunu yaz.";
            return;
        }

        await IstemKurVeGonderAsync(p => AsistanIstemleri.SorguAcikla(
            sorgu, p.Tumu, AsistanIstemleri.MotorAdi(_motorGetir())),
            baglamMetni: sorgu);
    }

    /// <summary>Ortak koşu: şema özeti → istem (Show Prompt) → model → cevap/bilgi. İki komutun tek yolu.</summary>
    /// <summary>
    /// Şemanın iki parçası (v22-S8). <see cref="Kompakt"/> ısıtılan ÖNEKTE durur ve bağlantı
    /// boyunca değişmez; <see cref="Kolonlar"/> soruya göre değişir ve önekten SONRA eklenir.
    /// </summary>
    /// <param name="Kompakt">Yalnız tablo/görünüm ADLARI — sabit.</param>
    /// <param name="Kolonlar">Soruda geçen tabloların tam kolon dökümü — değişken, boş olabilir.</param>
    private readonly record struct SemaParcalari(string Kompakt, string Kolonlar)
    {
        /// <summary>Önek ayrımı gerekmeyen (bağlam komutu) istemler için tek metin: varsa ayrıntı,
        /// yoksa kompakt liste — v22-S8 öncesi davranışın aynısı.</summary>
        public string Tumu => Kolonlar.Length > 0 ? Kolonlar : Kompakt;
    }

    /// <param name="semaKapisi">
    /// Doluysa ŞEMA KAPISI uygulanır: metin veriyle ilgili bir işaret taşımıyorsa (selamlaşma,
    /// sohbet) şema HİÇ gönderilmez. Yalnız serbest soru bunu verir — bağlam komutları
    /// (Değerlendir/Açıkla/Hata çözdür) tanım gereği bir sorgu üzerinde çalışır, onlarda şema şart.
    /// </param>
    private async Task IstemKurVeGonderAsync(
        Func<SemaParcalari, string> istemKur, string? baglamMetni, string? semaKapisi = null)
    {
        Mesgul = true;
        Cevap = "";
        CevaptakiSorgu = null;
        // Kullanıcı bulgusu 2026-08-08: "Şema özeti hazırlanıyor…" bir selamlaşmada kafa karıştırıyordu
        // (iç işleyiş terimi — kullanıcının şemayla alakası yok). Sade, tarafsız bekleme metni:
        Bilgi = "Hazırlanıyor…";
        _cts = new CancellationTokenSource();

        // Sohbette "düşünüyor" balonu (kullanıcı isteği 2026-07-28): cevap gelene kadar cevabın
        // çıkacağı yerde AI logomuzla bir bekleme balonu durur; cevap/iptal/hata olunca kaldırılır.
        var dusunuyorBalonu = new AsistanMesaji(KullaniciMi: false, "", Dusunuyor: true);
        Mesajlar.Add(dusunuyorBalonu);

        // ⏱ Sayaç isteğe DEĞİL, tüm bekleme akışına bağlı: şema okuma + istem kurma + model + SQL
        // düzeltme turu dahil — kullanıcının beklediği süre budur.
        using var sayacCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task sayac = SureSayaciAsync(sayacCts.Token);
        try
        {
            SemaOnbellegi? onbellek = await _semaGetir();
            // v21 (2026-08-09): AI'a TÜM şema yerine yalnız İLGİLİ tabloların şeması → istem küçülür
            // (büyük DB'de 6374 token modelin bağlamını aşıyordu). baglamMetni (sorgu+soru) boşsa ya da
            // hiçbir tablo adı geçmiyorsa tam şemaya düşülür (genel soru davranışı korunur).
            IReadOnlyCollection<string> ilgili = AsistanIstemleri.IlgiliTablolar(baglamMetni, onbellek);

            // 🚪 Şema kapısı: tablo adı geçmiyor VE soru veriyle ilgili değilse şema HİÇ gitmez.
            // Tablo adı geçiyorsa kapı devre dışıdır — kullanıcı zaten bir tabloyu işaret etmiştir.
            bool semaGonder = ilgili.Count > 0
                || semaKapisi is null
                || AsistanIstemleri.SemaIsteniyorMu(semaKapisi);

            // v22-S8: şema İKİYE ayrılır — KOMPAKT liste ısıtılan önekte kalır (değişmez), ilgili
            // tabloların kolon dökümü önekten SONRA eklenir. Eskiden kolon dökümü kompakt listenin
            // YERİNE geçiyordu; o zaman önek her soruda başkalaşır ve ısıtma işlevsiz kalırdı.
            var parcalar = semaGonder
                ? new SemaParcalari(
                    AsistanIstemleri.SemaOzetiKompakt(onbellek),
                    ilgili.Count > 0 ? AsistanIstemleri.SemaOzeti(onbellek, ilgili) : "")
                : new SemaParcalari("", "");

            string istem = istemKur(parcalar);
            IstemOnizleme = istem; // Show Prompt: giden metnin TA KENDİSİ

            Bilgi = "Yanıt hazırlanıyor…";
            AsistanCevabi cevap = await _servis.SorAsync(istem, _ayarlarGetir(), _cts.Token);

            Cevap = cevap.Metin;
            string? sorgu = cevap.Basarili ? AsistanIstemleri.CevaptanSorguAyikla(cevap.Metin) : null;

            // 🛡 ScriptDom kapısı (v21-S2): üretilen SQL geçersizse TEK düzeltme turu; hâlâ bozuksa uyar.
            string? dogrulamaNotu = null;
            if (sorgu is { Length: > 0 })
                (sorgu, dogrulamaNotu) = await SqlDogrulaVeDuzeltAsync(sorgu, _cts.Token);

            CevaptakiSorgu = sorgu;
            // Bulgu 3 (v21 review): düzeltme yapıldıysa NOTU sohbet balonuna da ekle — yoksa balon eski
            // (bozuk) SQL'i gösterirken "Sekmede aç" düzeltilmişi açıyor, tutarsız görünüyordu.
            string balonMetni = dogrulamaNotu is not null ? $"{cevap.Metin}\n\n{dogrulamaNotu}" : cevap.Metin;
            Mesajlar.Add(new AsistanMesaji(KullaniciMi: false, balonMetni, CevaptakiSorgu)); // asistan balonu
            Bilgi = cevap switch
            {
                { LimitAsildi: true } => "⏳ Ücretsiz katman limiti — birkaç saniye sonra tekrar dene.",
                { Basarili: false } => "⚠ Cevap alınamadı — ayrıntı yukarıda.",
                _ when dogrulamaNotu is not null => dogrulamaNotu,
                _ when SekmedeAcGorunur =>
                    "Cevap geldi — sorgu bulundu: \"↗ Sekmede aç\" ile İNCELEYEREK çalıştır.",
                _ => "Cevap geldi.",
            };
        }
        catch (OperationCanceledException)
        {
            Bilgi = "İstek iptal edildi.";
        }
        finally
        {
            // Sayacı önce durdur: bekleme balonu kalkarken sayaç bir tik daha atıp "hayalet süre"
            // bırakmasın. İptal zamanlayıcıyı dispose eder, döngü sessizce biter (bkz. SureSayaciAsync).
            sayacCts.Cancel();
            await sayac.ConfigureAwait(true);
            BekleyenSure = "";

            Mesajlar.Remove(dusunuyorBalonu); // bekleme balonunu her durumda kaldır (cevap/iptal/hata)
            Mesgul = false;
            _cts = null;
        }
    }

    /// <summary>
    /// 🛡 v21-S2: AI'ın ürettiği SQL'i SQLST'nin kendi çözümleyicisiyle (SqlDogrulayici) dener.
    /// Geçersizse modele TEK düzeltme turu yaptırır (SqlDuzeltmeTuru); düzelen SQL yine sınanır.
    /// Döner: (kullanılacak sorgu, kullanıcıya not — null ise sorgu temiz). Doğrulanamayan motorda
    /// (PG/MySQL/Oracle — parser'ımız yok) sessizce olduğu gibi bırakılır (sahte "geçerli" demeyiz).
    /// </summary>
    private async Task<(string Sorgu, string? Not)> SqlDogrulaVeDuzeltAsync(string sorgu, CancellationToken ct)
    {
        // Bulgu 4 (v21 review): motor BİLİNMİYORSA (bağlantısız) doğrulama yapma — hangi lehçe
        // olduğunu bilmeden Mssql varsayıp PG/MySQL/Oracle SQL'ini yanlışlıkla "bozuk" işaretlemeyelim.
        if (_motorGetir() is not { } motor)
            return (sorgu, null);

        SqlDogrulamaSonucu ilk = SqlDogrulayici.Dogrula(sorgu, motor);
        if (!ilk.Dogrulandi || ilk.Gecerli)
            return (sorgu, null); // doğrulanamadı (parser'ımız yok) ya da zaten geçerli → dokunma

        // Tek düzeltme turu
        Bilgi = "⚙ Üretilen SQL söz dizimini geçmedi — düzeltiliyor…";
        string duzeltmeIstemi = AsistanIstemleri.SqlDuzeltmeTuru(sorgu, ilk.Hata ?? "", motor);
        AsistanCevabi duzeltme = await _servis.SorAsync(duzeltmeIstemi, _ayarlarGetir(), ct);
        string? duzeltilmis = duzeltme.Basarili
            ? AsistanIstemleri.CevaptanSorguAyikla(duzeltme.Metin)
            : null;

        if (duzeltilmis is { Length: > 0 }
            && SqlDogrulayici.Dogrula(duzeltilmis, motor).Gecerli)
            return (duzeltilmis, "🛡 Üretilen SQL söz dizim hatası içeriyordu, düzeltildi — yine de inceleyerek çalıştır.");

        // Düzeltme de tutmadı: en iyi adayı bırak ama AÇIKÇA uyar (sessiz yanlış yasak)
        return (duzeltilmis is { Length: > 0 } ? duzeltilmis : sorgu,
            "⚠ Model geçerli SQL üretemedi (söz dizim hatası sürüyor) — çalıştırmadan DİKKATLE incele/düzelt.");
    }

    [RelayCommand]
    private void Iptal() => IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda

    public Task KapatAsync()
    {
        IptalYardimcisi.ArkaPlandaIptal(_cts); // m.15: Cancel UI'da bloklayabilir — havuzda
        return Task.CompletedTask;
    }
}
