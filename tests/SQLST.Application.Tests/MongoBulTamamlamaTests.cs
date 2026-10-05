using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// Find yardımcısı otomatik doldurma (v11-öncesi #3, 2026-07-25): imleç bağlamı (anahtar/değer/
/// tırnak), alan önerisi, Sort 1/-1, Filter $operatör ve uygulama (metin+imleç) davranışları.
/// </summary>
public class MongoBulTamamlamaTests
{
    private static readonly string[] Alanlar = ["_id", "Ad", "AdSoyad", "Zaman", "aktif"];

    [Fact]
    public void Sort_alan_yazarken_koleksiyon_alanlari_onerilir_kullanici_ornegi()
    {
        // Kullanıcı örneği: Sort'a "_id" yazacağım, koleksiyondaki _id alanı gelsin (Compass gibi).
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Sort, "{ _i", 4, Alanlar);
        Assert.NotNull(s);
        MongoOneri oneri = Assert.Single(s!.Oneriler);
        Assert.Equal("_id", oneri.Goster);

        // Uygula → tırnaklı anahtar + değer konumu
        (string metin, int caret) = MongoBulTamamlama.Uygula("{ _i", s.ParcaBas, 4, oneri.Ekle);
        Assert.Equal("{ \"_id\": ", metin);
        Assert.Equal(metin.Length, caret);

        // Değer konumunda artık 1 / -1 önerilir (Compass davranışı)
        MongoTamamlamaSonucu? deger = MongoBulTamamlama.Oner(MongoKutu.Sort, metin, caret, Alanlar);
        Assert.NotNull(deger);
        Assert.Equal(["1", "-1"], deger!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Onek_suzer_ve_harf_duyarsizdir()
    {
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, "{ ad", 4, Alanlar);
        Assert.NotNull(s);
        Assert.Equal(["Ad", "AdSoyad"], s!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Tirnak_icinde_yazarken_acilis_tirnagi_cift_uretilmez()
    {
        // Kullanıcı '"' açıp yazıyor: eklenen metnin baştaki tırnağı düşer.
        // v19-S14: Filter'da alan uygulanınca operatör nesnesi de açılır ("Zaman": { ).
        string metin = "{ \"Za";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        MongoOneri oneri = Assert.Single(s!.Oneriler);
        // v22-S4 m.5: kapanis } DA yazilir (kullanici: "kapatma kelebegini unutmus"), imlec araya konur
        (string yeni, int caret) = MongoBulTamamlama.Uygula(
            metin, s.ParcaBas, metin.Length, oneri.Ekle, oneri.ImlecGeri);
        Assert.Equal("{ \"Zaman\": {  }", yeni);
        Assert.Equal(yeni.Length - 2, caret);
    }

    // ---- v19-S14 (canlı test 2026-08-04): "alan adını yazayım, gerisini o tamamlasın" ----

    [Fact]
    public void Filter_alan_secilince_operator_nesnesi_acilir_ve_dolar_onerisi_zincirlenir()
    {
        // 1) Alan yaz → öneri "KullaniciId" DEĞİL ama örnek kümedeki "Ad": uygula → "Ad": {
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, "A", 1, Alanlar);
        Assert.NotNull(s);
        MongoOneri alan = s!.Oneriler[0];
        Assert.Equal("Ad", alan.Goster);
        (string metin, int caret) =
            MongoBulTamamlama.Uygula("A", s.ParcaBas, 1, alan.Ekle, alan.ImlecGeri);
        Assert.Equal("\"Ad\": {  }", metin);   // v22-S4 m.5: kapanış } DA yazılır
        Assert.Equal(metin.Length - 2, caret); // imleç ayraçların ARASINDA

        // 2) İç nesnede $ne yaz → $ne önerilir (anahtar konumu — süslü otomatik açılmıştı)
        string m2 = metin[..caret] + "$ne" + metin[caret..];
        int caret2 = caret + 3;
        MongoTamamlamaSonucu? op = MongoBulTamamlama.Oner(MongoKutu.Filter, m2, caret2, Alanlar);
        Assert.NotNull(op);
        Assert.Equal("$ne", Assert.Single(op!.Oneriler).Goster);

        // 3) Uygula → değer konumu: kullanıcı yalnız DEĞERİ yazar
        (string m3, _) = MongoBulTamamlama.Uygula(m2, op.ParcaBas, caret2, op.Oneriler[0].Ekle);
        Assert.Equal("\"Ad\": { \"$ne\":  }", m3);
    }

    [Fact]
    public void Filter_deger_konumunda_dolar_yazilirsa_operator_ic_nesneyle_onerilir()
    {
        // Alanı elle ": " ile yazan kullanıcı: değer konumunda $ → operatör "{ "$ne": " ekler.
        string metin = "\"KullaniciId\": $ne";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        MongoOneri o = Assert.Single(s!.Oneriler);
        Assert.Equal("$ne", o.Goster);
        (string yeni, _) = MongoBulTamamlama.Uygula(metin, s.ParcaBas, metin.Length, o.Ekle);
        Assert.Equal("\"KullaniciId\": { \"$ne\": ", yeni);
    }

    [Fact]
    public void Sort_ve_Project_alan_eklemesi_ic_nesne_ACMAZ()
    {
        // Sort/Project değerleri 1/-1 ve 1/0'dır — iç nesne yalnız Filter'da açılır.
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Sort, "Za", 2, Alanlar);
        Assert.NotNull(s);
        Assert.Equal("\"Zaman\": ", Assert.Single(s!.Oneriler).Ekle);
    }

    [Fact]
    public void Filter_dolarla_operator_onerir()
    {
        string metin = "{ \"yas\": { $g";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        Assert.Equal(["$gt", "$gte"], s!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Project_deger_konumunda_1_ve_0_onerir()
    {
        string metin = "{ \"Ad\": ";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Project, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        Assert.Equal(["1", "0"], s!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Virgul_sonrasi_yeniden_anahtar_konumudur()
    {
        string metin = "{ \"Ad\": 1, Za";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Sort, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        Assert.Equal("Zaman", Assert.Single(s!.Oneriler).Goster);
    }

    [Fact]
    public void Deger_metni_icindeki_iki_nokta_deger_konumunu_bozmaz()
    {
        // Tırnak İÇİNDEKİ ':' ayraç sayılmaz; "Ad" değeri yazılırken bağlam hâlâ değerdir.
        string metin = "{ \"Ad\": \"x:y\", \"akt";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        Assert.Equal("aktif", Assert.Single(s!.Oneriler).Goster);
    }

    [Fact]
    public void Bos_kutuda_tum_alanlar_onerilir()
    {
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Sort, "", 0, Alanlar);
        Assert.NotNull(s);
        Assert.Equal(Alanlar.Length, s!.Oneriler.Count);
    }

    [Fact]
    public void Eslesme_yoksa_null()
        => Assert.Null(MongoBulTamamlama.Oner(MongoKutu.Sort, "{ xyz", 5, Alanlar));

    [Fact]
    public void Koleksiyon_kutusu_duz_ad_tamamlar()
    {
        MongoTamamlamaSonucu? s = MongoBulTamamlama.KoleksiyonOner("log", ["loglar", "musteriler"]);
        Assert.NotNull(s);
        Assert.Equal("loglar", Assert.Single(s!.Oneriler).Goster);
        Assert.Equal(0, s.ParcaBas); // koleksiyon kutusunda parça = tüm metin
    }

    [Fact]
    public void Birebir_yazilmis_tek_oneri_gurultudur_popup_acilmaz()
    {
        // "loglar" tam yazılmışken hâlâ "loglar" önermek gürültü — null döner.
        Assert.Null(MongoBulTamamlama.KoleksiyonOner("loglar", ["loglar", "musteriler"]));
        // Alan tam yazılmışsa da aynı: "{ _id" → tek aday "_id" = parça → null.
        Assert.Null(MongoBulTamamlama.Oner(MongoKutu.Sort, "{ _id", 5, Alanlar));
    }

    // ---- Editör tamamlaması (C7, 2026-07-25): tam find/aggregate belgesi bağlamı ----

    private static readonly IReadOnlyList<SQLST.Contracts.SemaNesnesi> Koleksiyonlar =
    [
        new("Db", "Db", "loglar", SQLST.Contracts.SemaNesneTuru.Koleksiyon,
            [new("_id", "objectId", false, true), new("Mesaj", "string", true, false),
             new("Zaman", "date", true, false)], []),
        new("Db", "Db", "musteriler", SQLST.Contracts.SemaNesneTuru.Koleksiyon,
            [new("_id", "objectId", false, true), new("Ad", "string", true, false)], []),
    ];

    private static MongoTamamlamaSonucu? E(string metin)
        => MongoBulTamamlama.EditorOner(metin, metin.Length, Koleksiyonlar);

    [Fact]
    public void Editor_kok_anahtarlar_onerilir()
    {
        MongoTamamlamaSonucu? s = E("{ fi");
        Assert.NotNull(s);
        Assert.Equal(["find", "filter"], s!.Oneriler.Select(o => o.Goster).ToArray());

        // Boş belgede de (Ctrl+Space) kök anahtarlar gelir
        Assert.NotNull(E(""));
    }

    [Fact]
    public void Editor_find_degerinde_koleksiyon_adlari()
    {
        MongoTamamlamaSonucu? s = E("{ \"find\": \"log");
        Assert.NotNull(s);
        MongoOneri o = Assert.Single(s!.Oneriler);
        Assert.Equal("loglar", o.Goster);
        Assert.Equal("loglar", o.Ekle); // tırnak içindeyiz — çıplak ad eklenir
    }

    [Fact]
    public void Editor_filter_icinde_alanlar_ve_operatorler()
    {
        MongoTamamlamaSonucu? alan = E("{ \"find\": \"loglar\", \"filter\": { \"Me");
        Assert.NotNull(alan);
        Assert.Equal("Mesaj", Assert.Single(alan!.Oneriler).Goster);

        MongoTamamlamaSonucu? op = E("{ \"find\": \"loglar\", \"filter\": { \"Zaman\": { $gt");
        Assert.NotNull(op);
        Assert.Equal(["$gt", "$gte"], op!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Editor_sort_degerinde_1_ve_eksi1()
    {
        MongoTamamlamaSonucu? s = E("{ \"find\": \"loglar\", \"sort\": { \"Zaman\": ");
        Assert.NotNull(s);
        Assert.Equal(["1", "-1"], s!.Oneriler.Select(o => o.Goster).ToArray());
    }

    [Fact]
    public void Editor_pipeline_adiminda_dolar_operatorleri_ve_match_filter_gibi_davranir()
    {
        MongoTamamlamaSonucu? adim = E("{ \"aggregate\": \"loglar\", \"pipeline\": [ { $ma");
        Assert.NotNull(adim);
        Assert.Equal("$match", Assert.Single(adim!.Oneriler).Goster);

        // $match gövdesi filter gibi: alanlar önerilir
        MongoTamamlamaSonucu? alan = E("{ \"aggregate\": \"loglar\", \"pipeline\": [ { \"$match\": { \"Za");
        Assert.NotNull(alan);
        Assert.Equal("Zaman", Assert.Single(alan!.Oneriler).Goster);
    }

    [Fact]
    public void Editor_limit_degerinde_oneri_yok()
        => Assert.Null(E("{ \"find\": \"loglar\", \"limit\": 2"));

    [Fact]
    public void OtoTamamlama_mongo_motorunda_editor_onerisine_delege_eder()
    {
        var onbellek = new SQLST.Contracts.SemaOnbellegi
        {
            Nesneler = Koleksiyonlar,
            YuklenmeZamaniUtc = DateTime.UtcNow,
        };
        string metin = "{ \"find\": \"loglar\", \"sort\": { \"Za";
        IReadOnlyList<TamamlamaOnerisi> oneriler = OtoTamamlama.Oner(
            metin, metin.Length, onbellek, out int kelimeBasi, SQLST.Contracts.MotorTuru.Mongo);

        TamamlamaOnerisi o = Assert.Single(oneriler);
        Assert.Equal("Zaman", o.Metin);
        Assert.Equal("Zaman\": ", o.Ekle);         // tırnak içindeyiz — açılış tırnağı eklenmez
        Assert.Equal(metin.Length - 2, kelimeBasi); // parça "Za" — tırnak içinden başlar
        // SQL anahtar sözcükleri SIZMAZ
        Assert.DoesNotContain(oneriler, x => x.Metin == "SELECT");
    }

    // ---- v22-S4 saha turu-4 m.5 ----
    // Kullanıcı (ekran görüntüsü): Filter'da ExceptionLog'un "KayitTarihi" alanını seçmiş, kutuda
    // `"KayitTarihi": { ` var ve imleç içeride — ama açılan liste HÂLÂ koleksiyon alanlarını
    // (_id, KullaniciId, IpAddress…) gösteriyor. "Burada önermesin." Doğrusu: bir ALANIN açtığı
    // iç nesnenin anahtarları OPERATÖRdür.

    [Fact]
    public void Alanin_actigi_ic_nesnede_ALAN_degil_OPERATOR_onerilir()
    {
        string metin = "\"Zaman\": { ";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);

        Assert.NotNull(s);
        string[] gosterilenler = [.. s!.Oneriler.Select(o => o.Goster)];
        Assert.All(gosterilenler, g => Assert.StartsWith("$", g));   // hepsi operatör
        Assert.DoesNotContain("_id", gosterilenler);                  // ESKİDEN buradaydı
        Assert.DoesNotContain("Ad", gosterilenler);
        Assert.Contains("$gte", gosterilenler);
    }

    [Fact]
    public void Kok_kapsamda_alan_onerisi_KORUNUR()
    {
        // Regresyon bekçisi: kural yalnız İÇ nesneye bakar; kutunun kökünde alanlar gelmeye devam.
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, "", 0, Alanlar);

        Assert.NotNull(s);
        Assert.Equal(Alanlar, s!.Oneriler.Select(o => o.Goster));
    }

    [Fact]
    public void Dolar_dizisinin_eleman_nesnesinde_ALAN_onerilir()
    {
        // "$or": [ { … } ] → iç nesneyi DİZİ açar (kapsam anahtarı yok) → orada alan doğrudur.
        string metin = "\"$or\": [ { ";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);

        Assert.NotNull(s);
        Assert.Equal(Alanlar, s!.Oneriler.Select(o => o.Goster));
    }

    [Fact]
    public void ElemMatch_govdesinde_ALAN_onerilir()
    {
        // $elemMatch/$expr gövdesi alan bekler — kapsam anahtarı '$' ile başladığı için ayrılır.
        string metin = "\"Etiketler\": { \"$elemMatch\": { ";
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);

        Assert.NotNull(s);
        Assert.Equal(Alanlar, s!.Oneriler.Select(o => o.Goster));
    }

    /// <summary>
    /// "Kapatma kelebeğini de unutmuş": açılan her ayraç kapanışıyla yazılır, imleç araya konur —
    /// kullanıcı değeri yazarken metin GEÇERLİ JSON kalır, kapanışı elle tamamlamak zorunda değil.
    /// </summary>
    [Theory]
    [InlineData("$in")]
    [InlineData("$or")]
    [InlineData("$elemMatch")]
    public void Ayrac_acan_operatorler_kapanisini_da_yazar(string operatorAdi)
    {
        string metin = "\"Zaman\": { " + operatorAdi;
        MongoTamamlamaSonucu? s = MongoBulTamamlama.Oner(MongoKutu.Filter, metin, metin.Length, Alanlar);
        Assert.NotNull(s);
        MongoOneri o = s!.Oneriler.First(x => x.Goster == operatorAdi);

        (string yeni, int caret) =
            MongoBulTamamlama.Uygula(metin, s.ParcaBas, metin.Length, o.Ekle, o.ImlecGeri);

        Assert.EndsWith(o.Ekle[^1] == ']' ? "]" : "}", yeni);
        Assert.Equal(yeni.Length - 2, caret); // imleç ayraçların ARASINDA
    }
}
