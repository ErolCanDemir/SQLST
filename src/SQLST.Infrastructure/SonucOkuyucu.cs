using System.Data.Common;
using System.Diagnostics;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Açık bir bağlantıda sorguyu akışla okuyup QueryResult'a çeviren ortak mantık (02-mimari §4.1):
/// satır sınırlı akış, PRINT/rowcount mesajları, iptal ve sağlayıcı istisnası → hata nesnesi.
/// Hem stateless <see cref="SqlExecutor"/> hem kalıcı <see cref="DbOturum"/> bunu kullanır —
/// böylece okuma davranışı tek yerde.
///
/// V3-S1 / Faz 0: gövde ADO.NET SOYUT tipleriyle (DbConnection/DbCommand/DbDataReader)
/// çalışır — motor-nötrdür. Sağlayıcıya özgü tek iki nokta (bilgi mesajı aboneliği ve
/// istisna yorumu) <see cref="ILehce"/> üzerinden gelir.
///
/// <b>UI DONMA KURALI (2026-07-20, kullanıcı bulgusu):</b> buradaki HER await
/// <c>ConfigureAwait(false)</c> kullanır. Sebep WPF'e özgü ve ölümcül: bu gövde UI
/// thread'inden çağrılır ve <c>ReadAsync</c> satırlar ağ tamponundaysa <b>senkron</b>
/// tamamlanır — yani <c>await</c> hiç beklemeden döner. ConfigureAwait olmadan
/// <c>ExecuteReaderAsync</c>'in devamı UI thread'ine postalanır ve BÜTÜN satır
/// materyalizasyon döngüsü UI thread'inde koşar; yoğun bir sorguda uygulama "Yanıt
/// vermiyor" olur (sekme geçişi bile takılır). ConfigureAwait(false) devamı thread
/// havuzuna alır → UI serbest kalır.
/// </summary>
internal static class SonucOkuyucu
{
    public static async Task<QueryResult> OkuAsync(
        DbConnection baglanti, ILehce lehce, string sql, ExecuteOptions opts, int komutTimeoutSn, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        var mesajlar = new List<string>();
        var setler = new List<ResultSetData>();
        int toplamSatir = 0;
        long toplamBayt = 0;
        bool sinirAsildi = false;
        bool bellekAsildi = false;

        // 💾 Uyarlanır bütçe (v20-S16 donma teşhisi — Application Hang): makine bellek baskısı
        // altındayken normal bütçelerle materyalizasyon bile GC+takas kilitlenmesine yetiyor
        // (10M LIKE vakası: "stopped interacting with Windows"). Baskıda sınırlar küçültülür —
        // sonuç yine gelir, band nedenini açıkça söyler.
        int satirSiniri = opts.SatirSiniri;
        long bellekSiniriBayt = opts.BellekSiniriBayt;
        bool baskiKisti = false; // bellek baskısı bütçeyi küçülttü mü (mesaj yalnız GERÇEKTEN kesince)
        ISonucAlici? alici = opts.TumKumeler ? null : opts.SonucAlici; // 🧱 v22-S3 akışlı alıcı
        if (opts.TumKumeler)
        {
            // İç metadata okuması (şema/tanım/FK/indeks): batch'in TÜM kümeleri okunmalı → bütçe VE
            // bellek-baskısı küçültmesi devre dışı. Aksi halde büyük "kolonlar" kümesi bütçeyi doldurup
            // NextResult döngüsünü durduruyor ve sonraki küme(ler) düşüyordu ("4 küme gelmedi").
            satirSiniri = int.MaxValue;
            bellekSiniriBayt = long.MaxValue;
        }
        else if (BellekNobetcisi.BaskiAltindaMi() && (satirSiniri > 10_000 || bellekSiniriBayt > 24L * 1024 * 1024))
        {
            // Mesaj BURADA basılmaz (canlı bulgu 2026-08-14 "bellek olayı çok saçma ne alaka"):
            // baskı varken küçük sonuçlu/DDL sorgularda bile her koşuya düşüyordu. Bütçe yine
            // sessizce küçülür; not yalnız kesinti GERÇEKLEŞİRSE eklenir (aşağıda).
            satirSiniri = Math.Min(satirSiniri, 10_000);
            bellekSiniriBayt = Math.Min(bellekSiniriBayt, 24L * 1024 * 1024);
            baskiKisti = true;
        }

        using IDisposable abonelik = lehce.BilgiMesajlariniDinle(baglanti, mesajlar.Add);
        try
        {
            await using DbCommand komut = baglanti.CreateCommand();
            komut.CommandText = sql;
            komut.CommandTimeout = opts.KomutTimeoutSnOverride ?? komutTimeoutSn; // madde 2: yüzey bazlı timeout ezme
            lehce.KomutuAyarla(komut); // sağlayıcıya özgü komut ayarı (Oracle LONG/LOB — Faz 2)
            await using DbDataReader okuyucu = await komut.ExecuteReaderAsync(ct).ConfigureAwait(false);

            do
            {
                if (okuyucu.FieldCount == 0)
                    continue; // sonuç kümesi üretmeyen batch (DDL/DML)

                // v23-S13: kolon şeması (ölçek) — datetime2(3)'ün KAÇ kesir hanesi gösterileceği
                // buradan gelir (ham gösterim SSMS'le birebir). Sağlayıcı desteklemezse null.
                System.Collections.ObjectModel.ReadOnlyCollection<DbColumn>? sema =
                    okuyucu.CanGetColumnSchema() ? okuyucu.GetColumnSchema() : null;
                var kolonlar = new KolonBilgisi[okuyucu.FieldCount];
                for (int i = 0; i < okuyucu.FieldCount; i++)
                    kolonlar[i] = new KolonBilgisi(okuyucu.GetName(i), okuyucu.GetDataTypeName(i), okuyucu.GetFieldType(i),
                        sema is not null && i < sema.Count ? sema[i].NumericScale : null);

                // 🧱 v22-S3: alıcı varsa satırlar BİRİKTİRİLMEZ — doğrudan hedefe (grid tablosu) yazılır.
                // Ara liste + DataTable birlikte satır başına 708 bayt tutuyordu; tek kopya 457 bayt.
                var satirlar = alici is null ? new List<object?[]>() : null;
                alici?.KumeBasladi(setler.Count, kolonlar);
                while (!sinirAsildi && await okuyucu.ReadAsync(ct).ConfigureAwait(false))
                {
                    var satir = new object[okuyucu.FieldCount];
                    okuyucu.GetValues(satir);
                    if (satirlar is not null)
                    {
                        satirlar.Add(satir);
                    }
                    else if (!alici!.Satir(satir))
                    {
                        // Alıcı "dur" dedi (bellek tavanı) — kesinti bayrakları okuma yolundakiyle aynı.
                        sinirAsildi = bellekAsildi = true;
                        mesajlar.Add("🧱 Uygulama bellek tavanına dayandı — grid yüklemesi burada kesildi "
                            + "(uygulama çökmesin diye). Tamamı için “⬇ Tümünü dışa aktar”.");
                        break;
                    }

                    // ÜÇ sınır — hangisi önce dolarsa okuma durur:
                    //   • satır sınırı (100.000): dar satırlarda baskın (2026-07-23 OOM kalkanı)
                    //   • bellek bütçesi: geniş satırlarda (nvarchar(max)/varbinary/XML) baskın
                    //   • 💾 FİZİKSEL RAM nöbetçisi (v20-S15): bütçeler tahmindir ve iç yollar
                    //     (karşılaştırma hash'i 1M satır) daha geniştir — makine belleği kritiğe
                    //     dayandıysa ayar ne derse desin kes; OOM süreci kurtarılamaz öldürür.
                    // Bayt tahmini toplamBayt SETLER ARASI birikir (çoklu result set).
                    toplamBayt += SatirBayt(satir);
                    if (++toplamSatir >= satirSiniri)
                        sinirAsildi = true;
                    else if (toplamBayt >= bellekSiniriBayt)
                        sinirAsildi = bellekAsildi = true;
                    // 🛑 v22-S4 (saha turu-4 m.4): iki ayrı denetim (makine kritik / süreç tavanı)
                    // TEK kurala alındı — bkz. BellekNobetcisi.OkumaKesilmeli. Neden: makine
                    // kritikliği TEK BAŞINA kesiyordu; RAM'i biz doldurmasak da (VS, tarayıcı,
                    // veritabanı) kullanıcının sorgusu daha ilk yüz satırda sonlanıyordu
                    // (yerel ölçüm: limitli Mongo okuması 0,4 MB'ta, 64 belgede kesildi).
                    // ⚠ v22-S4 (çökme denetimi): `!opts.TumKumeler` koşulu KALDIRILDI. Metadata
                    // yolunda satır/bayt bütçeleri bilerek kapalı (tüm kümeler okunmalı), ama
                    // SÜREÇ TAVANI bir bütçe değil, çökme kalkanıdır — onu da kapatmak, korumasız
                    // tek bir yol bırakıyordu. Tavan artık HER okuma yolunda geçerli.
                    else if ((toplamSatir & 255) == 0 && BellekNobetcisi.OkumaKesilmeli())
                    {
                        sinirAsildi = bellekAsildi = true;
                        mesajlar.Add("🧱 Uygulama bellek tavanına dayandı — okuma burada kesildi ki uygulama "
                            + "ÇÖKMESİN. Eldeki satırlar gösteriliyor; tamamı için sorguyu daraltın "
                            + "(WHERE/TOP, gereksiz kolonları çıkarın) ya da “⬇ Tümünü dışa aktar” kullanın.");
                    }
                }

                // Tam sınırda yanlış "ilk N gösteriliyor" bandı olmasın (inceleme 2026-07-30): sınır
                // dolduğunda bir SONRAKİ satırı yokla — yoksa hiçbir şey kesilmemiştir; bayraklar
                // temizlenir ve do-while kalan result set'lere olağan biçimde devam eder.
                if (sinirAsildi && !await okuyucu.ReadAsync(ct).ConfigureAwait(false))
                    sinirAsildi = bellekAsildi = false;

                alici?.KumeBitti(setler.Count);
                setler.Add(new ResultSetData { Kolonlar = kolonlar, Satirlar = satirlar ?? [] });
            } while (!sinirAsildi && await okuyucu.NextResultAsync(ct).ConfigureAwait(false));

            // RecordsAffected erken-kesme (inceleme 2026-07-30 bekleyeni): sınırda okuyucu
            // TÜKETİLMEDEN kapanır → sürücü Attention gönderir, batch'in KALAN ifadeleri (DML
            // dahil) sunucuda HİÇ çalışmaz ve sayaç eksik kalır. Sessiz yanlış sayı yerine:
            // sayı gösterilmez + açık uyarı. (Kasıtlı: sınırı kaldırmak/kalanı akıtmak OOM/donma
            // kalkanını deler; DML'li batch'ler zaten Güvenli Yazma raylarında korunur.)
            if (sinirAsildi && baskiKisti)
                mesajlar.Add("💾 Makine bellek baskısı altında olduğundan bu koşuda sınırlar 10.000 satır / "
                    + "24 MB'a düşürülmüştü — kesinti bu yüzden erken geldi. Diğer uygulamaları kapatınca "
                    + "normal sınırlar döner.");
            if (sinirAsildi)
                mesajlar.Add("⚠ Satır/bellek sınırında durduruldu — bu batch'te bundan SONRA gelen "
                    + "ifadeler (varsa) ÇALIŞTIRILMADI; 'satır etkilendi' sayısı bu yüzden gösterilmiyor. "
                    + "Tamamı için sınırı artırıp yeniden çalıştırın.");
            else if (okuyucu.RecordsAffected >= 0)
                mesajlar.Add($"({okuyucu.RecordsAffected} satır etkilendi)");

            return new QueryResult
            {
                Basarili = true,
                ResultSetler = setler,
                Mesajlar = mesajlar,
                Sure = sure.Elapsed,
                ToplamSatir = toplamSatir,
                ToplamBayt = toplamBayt,
                SatirSiniriAsildi = sinirAsildi,
                BellekSiniriAsildi = bellekAsildi,
                EtkilenenSatir = !sinirAsildi && okuyucu.RecordsAffected >= 0 ? okuyucu.RecordsAffected : null,
            };
        }
        catch (OperationCanceledException)
        {
            return IptalSonucu(setler, mesajlar, sure.Elapsed, toplamSatir);
        }
        catch (DbException) when (ct.IsCancellationRequested)
        {
            return IptalSonucu(setler, mesajlar, sure.Elapsed, toplamSatir);
        }
        catch (DbException ex)
        {
            return new QueryResult
            {
                Hata = lehce.HataYorumla(ex),
                TumHatalar = lehce.HatalariYorumla(ex), // canlı denetim "bütün hatalar" (2026-07-30)
                ResultSetler = setler,
                Mesajlar = mesajlar,
                Sure = sure.Elapsed,
                ToplamSatir = toplamSatir,
                ToplamBayt = toplamBayt, // kısmi setler de bütçeden düşer (GO'lu script muhasebesi)
            };
        }
    }

    /// <summary>
    /// İLK sonuç kümesini satır satır AKITIR (v6 "Tümünü dışa aktar") — belleğe tümünü almadan,
    /// satır SINIRI olmadan. <paramref name="baslikYaz"/> kolonlarla bir kez, <paramref name="satirYaz"/>
    /// her satır için çağrılır. ConfigureAwait(false) — UI thread'i bloke olmaz (donma düzeltmesi).
    /// </summary>
    public static async Task<long> AkisAsync(
        DbConnection baglanti, ILehce lehce, string sql, int komutTimeoutSn,
        Action<IReadOnlyList<string>> baslikYaz, Action<object?[]> satirYaz, CancellationToken ct)
    {
        await using DbCommand komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        komut.CommandTimeout = komutTimeoutSn;
        lehce.KomutuAyarla(komut);
        await using DbDataReader okuyucu = await komut.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // İlk sonuç ÜRETEN kümeye geç (DDL/DML ön ekleri sonuç üretmeyebilir).
        do
        {
            if (okuyucu.FieldCount > 0)
                break;
        } while (await okuyucu.NextResultAsync(ct).ConfigureAwait(false));

        if (okuyucu.FieldCount == 0)
            return 0; // hiç sonuç kümesi yok (yalnız DDL/DML)

        baslikYaz([.. Enumerable.Range(0, okuyucu.FieldCount).Select(okuyucu.GetName)]);

        long sayi = 0;
        var tampon = new object[okuyucu.FieldCount]; // yeniden kullanılır — çağrı SENKRON tüketmeli
        while (await okuyucu.ReadAsync(ct).ConfigureAwait(false))
        {
            okuyucu.GetValues(tampon);
            satirYaz(tampon);
            sayi++;
        }
        return sayi;
    }

    /// <summary>
    /// Bir satırın grid belleğindeki KABA bayt karşılığı — bayt bütçesi için. Kesin değil
    /// (GC/hizalama payını saymaz), ucuz olmalı: O(kolon). string/char[] → 2 bayt/char + başlık,
    /// byte[] → uzunluk + başlık, kutulanmış ilkel → ~16, null/DBNull → ~8. Amaç mutlak doğruluk
    /// değil, patolojik geniş satırları (MB'lık LOB) yakalayıp OOM'dan önce okumayı kesmek.
    /// </summary>
    private static long SatirBayt(object?[] satir)
    {
        // x64 GERÇEK yığın (2026-07-30 crash düzeltmesi): eski tahmin ~2-3x eksikti (bütçe gerçekte
        // 500-700MB'a izin verip OOM'a yol açıyordu). Düzeltmeler: (1) dizi başlığı + eleman BAŞINA
        // 8 bayt referans yuvası; (2) kutulanmış değer tipleri nesne başlığıyla ~24-32 (16 değil).
        long b = 24 + satir.Length * 8L; // object?[] başlık + eleman referans yuvaları
        foreach (object? d in satir)
            b += d switch
            {
                null or DBNull => 0L,            // yuva zaten sayıldı; kutulanmış nesne yok
                string s => 24L + s.Length * 2L, // string başlık + char'lar
                byte[] a => 24L + a.Length,
                char[] c => 24L + c.Length * 2L,
                decimal or Guid => 32L,          // kutu başlığı (16) + 16 bayt değer
                _ => 24L,                        // kutulanmış int/long/DateTime/bool… (başlık 16 + ~8)
            };
        return b;
    }

    private static QueryResult IptalSonucu(
        List<ResultSetData> setler, List<string> mesajlar, TimeSpan sure, int toplamSatir)
    {
        mesajlar.Add("Sorgu kullanıcı tarafından iptal edildi.");
        return new QueryResult
        {
            IptalEdildi = true,
            ResultSetler = setler,
            Mesajlar = mesajlar,
            Sure = sure,
            ToplamSatir = toplamSatir,
        };
    }
}
