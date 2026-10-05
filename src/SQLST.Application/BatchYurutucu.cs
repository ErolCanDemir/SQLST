using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// GO batch'lerini sekmenin kalıcı oturumunda SIRAYLA çalıştırır ve sonuçları tek
/// QueryResult'ta toplar (V2-S3, FG-3.11). SSMS davranışı: hatalı batch sonrakini
/// durdurmaz (iptal durdurur); her batch ayrı komuttur, GO sunucuya gitmez.
/// Hata satırları orijinal script satırına eşlenir (batch başlangıcı + sunucu satırı).
/// </summary>
public static class BatchYurutucu
{
    /// <param name="satirTabani">
    /// Gönderilen metnin orijinal belgede başladığı satır (imleçteki statement gibi
    /// kısmi çalıştırmalarda hata satırı belge satırına eşlensin diye; tam metinde 1).
    /// </param>
    public static async Task<QueryResult> CalistirAsync(
        QueryService queryService, IDbOturum oturum, IReadOnlyList<SqlBatch> batchler,
        ExecuteOptions opts, CancellationToken ct, int satirTabani = 1)
    {
        if (batchler.Count == 0)
            return new QueryResult { Basarili = true };

        // Tek batch, tek tekrar — olağan yol: sarmalama maliyeti yok
        if (batchler is [{ Tekrar: 1 } tek])
        {
            QueryResult sonuc = await queryService.RunAsync(oturum, tek.Metin, opts, ct);
            return SatirlariEsle(sonuc, tek.BaslangicSatiri + satirTabani - 1);
        }

        var setler = new List<ResultSetData>();
        var mesajlar = new List<string>();
        SqlHata? ilkHata = null;
        bool iptal = false, sinirAsildi = false, bellekAsildi = false;
        int toplamSatir = 0;
        long toplamBayt = 0;
        int? etkilenen = null;
        TimeSpan toplamSure = TimeSpan.Zero;

        foreach (SqlBatch batch in batchler)
        {
            if (batch.Tekrar > 1)
                mesajlar.Add($"-- GO {batch.Tekrar}: batch {batch.Tekrar} kez çalıştırılıyor…");

            for (int tur = 1; tur <= batch.Tekrar && !iptal; tur++)
            {
                // Satır + bellek bütçesi ÇALIŞTIRMA kapsamındadır, batch başına DEĞİL (inceleme
                // bulgusu 2026-07-23: her batch taze 100k satır / 256 MB alınca "GO 20" kalkanı
                // deliyordu — toplam ~2M satır / GB'larca bellek → düzeltilen OOM geri gelirdi).
                // Bütçe dolunca sonraki batch'ler yine ÇALIŞIR (SSMS gibi — DML atlanamaz),
                // yalnız grid'e en az satır (1) materyalize edilir.
                ExecuteOptions parcaOpts = toplamSatir == 0 && toplamBayt == 0 ? opts : new ExecuteOptions
                {
                    SatirSiniri = Math.Max(1, opts.SatirSiniri - toplamSatir),
                    BellekSiniriBayt = Math.Max(1, opts.BellekSiniriBayt - toplamBayt),
                    VeritabaniOverride = opts.VeritabaniOverride,
                    KirliOkuma = opts.KirliOkuma,
                    KomutTimeoutSnOverride = opts.KomutTimeoutSnOverride, // inceleme 2026-07-30: klon düşürüyordu
                };
                QueryResult parca = await queryService.RunAsync(oturum, batch.Metin, parcaOpts, ct);

                setler.AddRange(parca.ResultSetler);
                mesajlar.AddRange(parca.Mesajlar);
                toplamSatir += parca.ToplamSatir;
                toplamBayt += parca.ToplamBayt;
                toplamSure += parca.Sure;
                sinirAsildi |= parca.SatirSiniriAsildi;
                bellekAsildi |= parca.BellekSiniriAsildi; // inceleme bulgusu #2: bayrak kayboluyordu
                if (parca.EtkilenenSatir is { } e)
                    etkilenen = (etkilenen ?? 0) + e;

                if (parca.IptalEdildi)
                {
                    iptal = true;
                    break;
                }

                if (parca.Hata is { } hata)
                {
                    SqlHata esli = hata with { Satir = hata.Satir + batch.BaslangicSatiri + satirTabani - 2 };
                    if (ilkHata is null)
                        ilkHata = esli;
                    else
                        mesajlar.Add($"Msg {esli.Numara}, Satır {esli.Satir}: {esli.Mesaj}");
                    // SSMS gibi: sonraki batch'e devam
                }
            }

            if (iptal || ct.IsCancellationRequested)
            {
                iptal = true;
                break;
            }
        }

        return new QueryResult
        {
            Basarili = !iptal && ilkHata is null,
            IptalEdildi = iptal,
            ResultSetler = setler,
            Mesajlar = mesajlar,
            Hata = ilkHata,
            Sure = toplamSure,
            ToplamSatir = toplamSatir,
            ToplamBayt = toplamBayt,
            SatirSiniriAsildi = sinirAsildi,
            BellekSiniriAsildi = bellekAsildi,
            EtkilenenSatir = etkilenen,
        };
    }

    private static QueryResult SatirlariEsle(QueryResult sonuc, int batchIlkSatiri)
        => sonuc.Hata is { } hata && batchIlkSatiri > 1
            ? sonuc with { Hata = hata with { Satir = hata.Satir + batchIlkSatiri - 1 } }
            : sonuc;
}
