namespace SQLST.Infrastructure;

/// <summary>
/// 💾 Fiziksel bellek nöbetçisi (v20-S15, kullanıcı 2026-08-08: "PROJE HİÇBİR ZAMAN PATLAMAMALI").
/// Satır/bayt bütçeleri TAHMİNDİR ve iç yollar (karşılaştırma hash'i: 1M satır) daha yüksek
/// sınırlarla okur — tahmin yanılırsa OutOfMemory süreci kurtarılamaz biçimde öldürür (yönetilen
/// güvenlik ağları OOM'da çoğu kez log bile yazamaz). Bu nöbetçi GERÇEĞE bakar: GC'nin gördüğü
/// makine bellek yükü kritik eşiğin %90'ına dayandıysa okuma kesilir — ayar/bütçe ne derse desin.
/// </summary>
public static class BellekNobetcisi
{
    /// <summary>Makinenin bellek yükü GC'nin kritik eşiğine (HighMemoryLoadThreshold ≈ %90 RAM) ulaştı mı.</summary>
    public static bool KritikMi()
    {
        GCMemoryInfo bilgi = GC.GetGCMemoryInfo();
        return KritikMi(bilgi.MemoryLoadBytes, bilgi.HighMemoryLoadThresholdBytes);
    }

    /// <summary>
    /// Saf eşik kuralı (testlenebilir): yük eşiğe ULAŞTIYSA kritiktir. Marj bilerek yok — bu
    /// makinelerde boştaki yük bile eşiğin %90'ına yakın seyrediyor (VS + servisler; testte
    /// görüldü 2026-08-08); marjlı kural her okumayı keserdi (yanlış pozitif = hiç veri gelmez).
    /// GC eşiği zaten "OOM yakın" çizgisidir — orada kesmek doğru, öncesinde değil.
    /// </summary>
    public static bool KritikMi(long yukBayt, long esikBayt) =>
        esikBayt > 0 && yukBayt >= esikBayt;

    /// <summary>
    /// Makine BASKI altında mı (v20-S16 donma teşhisi: Application Hang, "stopped interacting"):
    /// kritik eşiğe varılmadan da, yükü eşiğin %85'ini aşan makinede büyük materyalizasyon GC
    /// duraklamaları + takasla UI'ı dakikalarca kilitleyebiliyor. Bu durumda okuma bütçeleri
    /// KÜÇÜLTÜLÜR (kesilmez) — sonuç yine gelir, donma zemini oluşmaz.
    /// </summary>
    public static bool BaskiAltindaMi()
    {
        GCMemoryInfo bilgi = GC.GetGCMemoryInfo();
        return BaskiAltindaMi(bilgi.MemoryLoadBytes, bilgi.HighMemoryLoadThresholdBytes);
    }

    /// <summary>Saf kural: yük, eşiğin %85'ini geçtiyse baskı vardır.</summary>
    public static bool BaskiAltindaMi(long yukBayt, long esikBayt) =>
        esikBayt > 0 && yukBayt >= esikBayt - (esikBayt * 15 / 100);

    /// <summary>
    /// 🧱 SÜREÇ TAVANI (v22-S1; kullanıcı 2026-08-17 ÜÇÜNCÜ kez "büyük tabloya select atınca
    /// patlıyor"). Neden gerekiyor — <see cref="KritikMi"/> ve <see cref="BaskiAltindaMi"/>
    /// MAKİNE yüküne bakar: 16-32 GB RAM'li bir makinede bu ikisi HİÇ ateşlemez, ama SÜRECİN
    /// kendisi yine ölebilir. Çünkü bayt bütçesi HAM veriyi sayar; bellekteki NESNE GRAFİĞİ
    /// (string başlıkları, object?[] satırlar, DataRow/DataTable, Mongo'da BsonDocument) ham
    /// veriden 3-6x büyüktür — 96 MB "ham" bütçe pekâlâ 500 MB+ yığın demek olabilir.
    /// Bu tavan tahmine değil GC'nin gördüğü GERÇEK yönetilen yığına bakar: aşılırsa okuma
    /// kesilir (eldeki veri gösterilir, süreç yaşar). Tek amaç: uygulama ASLA çökmesin.
    /// </summary>
    public const long SurecTavaniBayt = 1_200L * 1024 * 1024;

    /// <summary>Bu sürecin yönetilen yığını (GC'nin gördüğü) — koleksiyon TETİKLEMEZ, ucuzdur.</summary>
    public static long YiginBayt() => GC.GetTotalMemory(false);

    /// <summary>Süreç tavanı aşıldı mı (canlı ölçüm).</summary>
    public static bool SurecTavaniAstiMi() => SurecTavaniAstiMi(YiginBayt(), SurecTavaniBayt);

    /// <summary>Saf kural (testlenebilir): yığın tavana ULAŞTIYSA aşılmıştır.</summary>
    public static bool SurecTavaniAstiMi(long yiginBayt, long tavanBayt) =>
        tavanBayt > 0 && yiginBayt >= tavanBayt;

    /// <summary>Makine kritikken kesmek için BİZİM yığınımızın da geçmesi gereken alt sınır (v22-S4).</summary>
    public const long KesmeIcinAsgariYigin = 256L * 1024 * 1024;

    /// <summary>
    /// 🛑 Okuma KESİLMELİ mi (v22-S4 saha turu-4 m.4, ÖLÇÜMLE bulundu). <see cref="KritikMi"/>
    /// MAKİNE RAM yüküne bakar — bizim sürecimize değil. Başka bir uygulama (VS, tarayıcı,
    /// veritabanı) RAM'i doldurduğunda kullanıcının sorgusu, biz daha <b>0,4 MB</b> okumuşken
    /// kesiliyordu: yerel ölçümde limitli bir Mongo okuması <b>64 belgede</b> "bellek" gerekçesiyle
    /// sonlandı. Bu, kullanıcıya hiç veri vermeyen ama korumuş gibi görünen bir davranıştır.
    ///
    /// Doğru kural iki parçalı: (a) SÜREÇ TAVANI tek başına yeter — kendi yığınımız tavandaysa
    /// gerçekten tehlikedeyiz; (b) makine kritikliği ancak BİZİM yığınımız da anlamlı büyüklükteyse
    /// (<see cref="KesmeIcinAsgariYigin"/>) kesme sebebidir — yoksa RAM'i yiyen biz değiliz ve
    /// kesmek kimseyi kurtarmaz. Baskı altında bütçeleri küçültme (<see cref="BaskiAltindaMi"/>)
    /// davranışı DEĞİŞMEDİ: o kesmez, daraltır.
    /// </summary>
    public static bool OkumaKesilmeli() => OkumaKesilmeli(YiginBayt(), KritikMi());

    /// <summary>Saf kural (testlenebilir).</summary>
    public static bool OkumaKesilmeli(long yiginBayt, bool makineKritik) =>
        SurecTavaniAstiMi(yiginBayt, SurecTavaniBayt)
        || (makineKritik && yiginBayt >= KesmeIcinAsgariYigin);
}
