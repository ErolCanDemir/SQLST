using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Dosyadan tabloya aktarım motoru (v13-S3): <see cref="DosyaOkuyucu"/> akışından okur, hedefe
/// HAZIR parametreli INSERT (ve Ekle/Güncelle kipinde önce UPDATE) ile yazar — v12
/// <see cref="AktarimServisi"/> deseninin dosya-kaynaklı ikizi: 500'lük parti COMMIT'leri,
/// ilerleme, iptalde aktif parti ROLLBACK (dürüst sayaç), hata politikaları, ilk 20 hata örneği.
/// FARKI değer dönüşümüdür: TXT'den her şey STRING gelir — hedefe gitmeden tahmin edilen tipe
/// SEÇİLEN KÜLTÜRLE parse edilir ("1.250,75" TR → 1250.75m; sürücüye bırakılırsa ya yanlış ya
/// hata olurdu); çevrilemeyen değer satır hatasıdır ve politikaya düşer. Excel değerleri zaten
/// tiplidir, olduğu gibi geçer. Sanal üyeler: UI testi sahteler.
/// </summary>
public class DosyaAktarimServisi(ILehceSaglayici lehceler)
{
    private const int EnCokHataOrnegi = 20;

    public virtual async Task<AktarimSonucu> AktarAsync(
        DosyaAktarimIstegi istek, IEnumerable<object?[]> satirlar,
        IProgress<AktarimIlerleme>? ilerleme, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        long okunan = 0, yazilan = 0, guncellenen = 0, atlanan = 0;
        var hataOrnekleri = new List<string>();
        CultureInfo kultur = istek.KulturAdi is null
            ? CultureInfo.InvariantCulture : new CultureInfo(istek.KulturAdi);

        try
        {
            ILehce lehce = lehceler.Getir(istek.Hedef.Motor);
            await using DbConnection hedef = lehce.BaglantiOlustur(
                istek.Hedef, istek.HedefVeritabani, havuz: true);
            await hedef.OpenAsync(ct);

            // Yeni tablo yolu (v13-S4): UI'da ONAYLANMIŞ CREATE script'i — aynı bağlantıda,
            // aktarımdan hemen önce çalışır (tablo aynı oturumda hemen görünür).
            if (!string.IsNullOrWhiteSpace(istek.OnceDdl))
            {
                await using DbCommand ddl = hedef.CreateCommand();
                ddl.CommandText = istek.OnceDdl;
                await ddl.ExecuteNonQueryAsync(ct);
            }

            if (istek.OnceTemizle)
            {
                await using DbCommand sil = hedef.CreateCommand();
                // Tablo adı tırnaklı (inceleme 2026-07-30 — AktarimServisi ile aynı KRİTİK düzeltme).
                sil.CommandText = $"DELETE FROM {AktarimEslestirici.TabloYaz(lehce.TirnaklaTanimlayici, istek.HedefTablo)}";
                sil.CommandTimeout = istek.Hedef.KomutTimeoutSn;
                await sil.ExecuteNonQueryAsync(ct);
            }

            // Eşleşme başına kaynak kolon İNDEKSİ + tip tahmini (dosya kolon sırası sabittir).
            int[] indeksler = new int[istek.Eslesmeler.Count];
            var tipler = new DosyaTipi[istek.Eslesmeler.Count];
            for (int i = 0; i < istek.Eslesmeler.Count; i++)
            {
                int k = -1;
                for (int j = 0; j < istek.KaynakKolonlar.Count; j++)
                {
                    if (istek.KaynakKolonlar[j].Ad.Equals(
                            istek.Eslesmeler[i].KaynakKolon, StringComparison.OrdinalIgnoreCase))
                    {
                        k = j;
                        break;
                    }
                }

                if (k < 0)
                {
                    return new AktarimSonucu(false, 0, 0, 0, 0,
                        $"Eşleşmedeki '{istek.Eslesmeler[i].KaynakKolon}' kolonu dosyada yok.",
                        sure.Elapsed, hataOrnekleri);
                }

                indeksler[i] = k;
                tipler[i] = istek.KaynakKolonlar[k].Tip;
            }

            string onek = AktarimEslestirici.ParametreOneki(lehce.MotorId);
            await using DbCommand yaz = hedef.CreateCommand();
            yaz.CommandText = AktarimEslestirici.HedefInsert(
                lehce.TirnaklaTanimlayici, istek.HedefTablo, istek.Eslesmeler, onek);
            var parametreler = new DbParameter[istek.Eslesmeler.Count];
            for (int i = 0; i < parametreler.Length; i++)
            {
                DbParameter p = yaz.CreateParameter();
                p.ParameterName = $"{onek}p{i}";
                yaz.Parameters.Add(p);
                parametreler[i] = p;
            }

            // Ekle/Güncelle (v12-S4 deseni): p{i} indeksi ortak; ekleme sırası metin sırası.
            DbParameter?[] guncelleParametreleri = new DbParameter?[istek.Eslesmeler.Count];
            await using DbCommand? guncelle = GuncelleKur(istek, hedef, lehce, onek, guncelleParametreleri);
            if (istek.YazmaKipi == AktarimYazmaKipi.EkleGuncelle && guncelle is null)
            {
                return new AktarimSonucu(false, 0, 0, 0, 0,
                    "Ekle/Güncelle kipi için anahtar kolonlar VE anahtar dışında en az bir eşlenmiş kolon gerekir.",
                    sure.Elapsed, hataOrnekleri);
            }

            DbTransaction islem = await hedef.BeginTransactionAsync(ct);
            yaz.Transaction = islem;
            if (guncelle is not null)
                guncelle.Transaction = islem;
            long partidekiEklenen = 0, partidekiGuncellenen = 0;

            try
            {
                foreach (object?[] satir in satirlar)
                {
                    ct.ThrowIfCancellationRequested();
                    okunan++;

                    bool savepointAlindi = false; // PG parti-iptali koruması (AktarimServisi ile aynı, 2026-07-30)
                    try
                    {
                        for (int i = 0; i < parametreler.Length; i++)
                        {
                            object deger = Donustur(
                                indeksler[i] < satir.Length ? satir[indeksler[i]] : null,
                                tipler[i], kultur, istek.Eslesmeler[i].KaynakKolon, okunan);
                            parametreler[i].Value = deger;
                            if (guncelleParametreleri[i] is { } gp)
                                gp.Value = deger;
                        }

                        if (istek.HataPolitikasi == AktarimHataPolitikasi.AtlaVeRaporla && islem.SupportsSavepoints)
                        {
                            await islem.SaveAsync("satir", ct);
                            savepointAlindi = true;
                        }

                        if (guncelle is not null && await guncelle.ExecuteNonQueryAsync(ct) > 0)
                        {
                            guncellenen++;
                            partidekiGuncellenen++;
                        }
                        else
                        {
                            await yaz.ExecuteNonQueryAsync(ct);
                            yazilan++;
                            partidekiEklenen++;
                        }
                    }
                    catch (Exception satirHatasi)
                        when (satirHatasi is DbException or FormatException
                              && istek.HataPolitikasi == AktarimHataPolitikasi.AtlaVeRaporla)
                    {
                        atlanan++;
                        if (savepointAlindi && satirHatasi is DbException)
                            await islem.RollbackAsync("satir", CancellationToken.None); // yalnız bu satır geri sarılır
                        if (hataOrnekleri.Count < EnCokHataOrnegi)
                            hataOrnekleri.Add($"Satır {okunan}: {satirHatasi.Message}");
                    }
                    catch (FormatException donusumHatasi)
                    {
                        try { await islem.RollbackAsync(CancellationToken.None); } catch (Exception) { } // dispose penceresi koruması (2026-07-30)
                        yazilan -= partidekiEklenen;
                        guncellenen -= partidekiGuncellenen;
                        return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                            donusumHatasi.Message, sure.Elapsed, hataOrnekleri);
                    }

                    if (partidekiEklenen + partidekiGuncellenen >= AktarimServisi.PartiBoyu)
                    {
                        await islem.CommitAsync(ct);
                        await islem.DisposeAsync();
                        islem = await hedef.BeginTransactionAsync(ct);
                        yaz.Transaction = islem;
                        if (guncelle is not null)
                            guncelle.Transaction = islem;
                        partidekiEklenen = 0;
                        partidekiGuncellenen = 0;
                        ilerleme?.Report(new AktarimIlerleme(okunan, yazilan, guncellenen, atlanan));
                    }
                }

                await islem.CommitAsync(CancellationToken.None); // son parti — iptalde de tamamlanmışı koru
            }
            catch (OperationCanceledException)
            {
                try { await islem.RollbackAsync(CancellationToken.None); } catch (Exception) { } // dispose penceresi koruması (2026-07-30)
                yazilan -= partidekiEklenen;
                guncellenen -= partidekiGuncellenen;
                return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                    "Aktarım kullanıcı tarafından iptal edildi.", sure.Elapsed, hataOrnekleri, IptalEdildi: true);
            }
            catch (DbException hata)
            {
                try { await islem.RollbackAsync(CancellationToken.None); } catch (Exception) { } // dispose penceresi koruması (2026-07-30)
                yazilan -= partidekiEklenen;
                guncellenen -= partidekiGuncellenen;
                return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                    $"Satır {okunan} yazılamadı: {hata.Message}", sure.Elapsed, hataOrnekleri);
            }
            finally
            {
                await islem.DisposeAsync();
            }

            ilerleme?.Report(new AktarimIlerleme(okunan, yazilan, guncellenen, atlanan));
            return new AktarimSonucu(true, okunan, yazilan, guncellenen, atlanan, null, sure.Elapsed, hataOrnekleri);
        }
        catch (OperationCanceledException)
        {
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                "Aktarım kullanıcı tarafından iptal edildi.", sure.Elapsed, hataOrnekleri, IptalEdildi: true);
        }
        catch (DbException ex)
        {
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
        catch (IOException ex)
        {
            // Dosya okuma hatası (kilitli/yarıda silinen dosya) — akış numaralandırması fırlatır.
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
    }

    /// <summary>
    /// TXT string değerini tahmin edilen tipe KÜLTÜRLE çevirir (boş/null → DBNull); Excel'in tipli
    /// değerleri olduğu gibi geçer. Çevrilemeyen değer FormatException'dır — satır hatası olarak
    /// politikaya düşer (mesajda satır no + kolon + ham değer: kullanıcı dosyada bulabilsin).
    /// </summary>
    private static object Donustur(object? deger, DosyaTipi tip, CultureInfo kultur, string kolon, long satirNo)
    {
        if (deger is null or "")
            return DBNull.Value;
        if (deger is not string s)
            return deger; // Excel — hücre tipiyle geldi

        s = s.Trim();
        try
        {
            return tip switch
            {
                DosyaTipi.TamSayi => long.Parse(s, NumberStyles.Integer, kultur),
                DosyaTipi.Ondalik => decimal.Parse(s, NumberStyles.Number, kultur),
                DosyaTipi.Tarih => DateTime.Parse(s, kultur, DateTimeStyles.None),
                DosyaTipi.Bool => bool.Parse(s),
                _ => s,
            };
        }
        catch (FormatException)
        {
            throw new FormatException(
                $"Satır {satirNo}: '{s}' değeri ({kolon}) {TipAdi(tip)} tipine çevrilemedi.");
        }
    }

    private static string TipAdi(DosyaTipi tip) => tip switch
    {
        DosyaTipi.TamSayi => "tam sayı",
        DosyaTipi.Ondalik => "ondalık",
        DosyaTipi.Tarih => "tarih",
        DosyaTipi.Bool => "true/false",
        _ => "metin",
    };

    private static DbCommand? GuncelleKur(
        DosyaAktarimIstegi istek, DbConnection hedef, ILehce lehce, string onek,
        DbParameter?[] guncelleParametreleri)
    {
        if (istek.YazmaKipi != AktarimYazmaKipi.EkleGuncelle)
            return null;

        IReadOnlyList<string> anahtarlar = istek.AnahtarKolonlar ?? [];
        string? sql = AktarimEslestirici.HedefUpdate(
            lehce.TirnaklaTanimlayici, istek.HedefTablo, istek.Eslesmeler, anahtarlar, onek);
        if (sql is null)
            return null;

        DbCommand komut = hedef.CreateCommand();
        komut.CommandText = sql;
        var anahtarSeti = new HashSet<string>(anahtarlar, StringComparer.OrdinalIgnoreCase);
        foreach (bool anahtarMi in (bool[])[false, true]) // önce SET, sonra WHERE — metin sırası
        {
            for (int i = 0; i < istek.Eslesmeler.Count; i++)
            {
                if (anahtarSeti.Contains(istek.Eslesmeler[i].HedefKolon) != anahtarMi)
                    continue;
                DbParameter p = komut.CreateParameter();
                p.ParameterName = $"{onek}p{i}";
                komut.Parameters.Add(p);
                guncelleParametreleri[i] = p;
            }
        }

        return komut;
    }
}
