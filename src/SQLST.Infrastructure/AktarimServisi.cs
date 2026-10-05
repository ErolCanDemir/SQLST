using System.Data.Common;
using System.Diagnostics;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Paket Aktarım motoru (v12-S1): kaynaktan <see cref="DbDataReader"/> ile AKIŞLI okur (tamamı
/// belleğe alınmaz), hedefe HAZIR parametreli INSERT ile yazar — 500'lük partiler hâlinde
/// COMMIT'ler (yarıda kesilme = o ana dek commit'lenen kalır, dürüst sayım). SQL ailesi içinde
/// motorlar-arası serbesttir (parametre değerleri ADO.NET doğal dönüşümüyle taşınır);
/// Mongo v12-S5'e (en sona) bırakıldı. Hedefte hiçbir DDL çalıştırılmaz; "önce temizle"
/// TRUNCATE değil DELETE'tir (yetki/FK dostu) ve UI açık onay ister.
/// </summary>
public class AktarimServisi(ILehceSaglayici lehceler)
{
    /// <summary>Parti boyu: her N satırda COMMIT + ilerleme raporu (v12 planı).</summary>
    public const int PartiBoyu = 500;

    /// <summary>v22-S16 (kullanıcı: "hataları loglayalım"): 20 → 10.000. Ekran yine ilk 20'yi
    /// gösterir; tamamı "⬇ Hata raporunu kaydet" dosyasına + (ilk 200'ü) uygulama günlüğüne gider.
    /// Tavan bellek sigortasıdır (~10k × ~100 karakter ≈ birkaç MB); aşımı rapor açıkça söyler.</summary>
    private const int EnCokHataOrnegi = 10_000;

    /// <summary>
    /// Sorgu→Tablo (v12-S3): kaynak sorgunun SONUÇ kolonlarını hiç satır çekmeden keşfeder
    /// (SchemaOnly). Eşleme gridinin kaynak adayları buradan gelir. Sanal: UI testi sahteler.
    /// </summary>
    public virtual async Task<IReadOnlyList<SemaKolonu>> KaynakKolonlariAsync(
        ConnectionProfile profil, string? veritabani, string sql, CancellationToken ct)
    {
        ILehce lehce = lehceler.Getir(profil.Motor);
        await using DbConnection baglanti = lehce.BaglantiOlustur(profil, veritabani, havuz: true);
        await baglanti.OpenAsync(ct);
        await using DbCommand komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        await using DbDataReader okuyucu =
            await komut.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, ct);
        var kolonlar = new List<SemaKolonu>(okuyucu.FieldCount);
        for (int i = 0; i < okuyucu.FieldCount; i++)
            kolonlar.Add(new SemaKolonu(okuyucu.GetName(i), okuyucu.GetDataTypeName(i), true, false));
        return kolonlar;
    }

    public async Task<AktarimSonucu> AktarAsync(
        AktarimIstegi istek, IProgress<AktarimIlerleme>? ilerleme, CancellationToken ct)
    {
        var sure = Stopwatch.StartNew();
        long okunan = 0, yazilan = 0, guncellenen = 0, atlanan = 0;
        var hataOrnekleri = new List<string>();

        try
        {
            ILehce kaynakLehce = lehceler.Getir(istek.Kaynak.Motor);
            ILehce hedefLehce = lehceler.Getir(istek.Hedef.Motor);

            await using DbConnection kaynak = kaynakLehce.BaglantiOlustur(
                istek.Kaynak, istek.KaynakVeritabani, havuz: true);
            await using DbConnection hedef = hedefLehce.BaglantiOlustur(
                istek.Hedef, istek.HedefVeritabani, havuz: true);
            await kaynak.OpenAsync(ct);
            await hedef.OpenAsync(ct);

            // Kaynak okuyucu "önce temizle"den ÖNCE açılır (inceleme 2026-07-30): kaynak sorgusu ya da
            // kolon eşlemesi hatalıysa geri dönüşsüz DELETE hiç çalışmamış olur (hedef boşalmaz).
            await using DbCommand oku = kaynak.CreateCommand();
            oku.CommandText = istek.KaynakSelectSql;
            oku.CommandTimeout = istek.Kaynak.KomutTimeoutSn;  // profil timeout'u (0 = sınırsız) — 30 sn sağlayıcı varsayılanı kalkar
            await using DbDataReader okuyucu = await oku.ExecuteReaderAsync(ct);

            // Kaynak kolon adı → ordinal (sorgu kipinde eşleşmeler sonuç kolonlarına göre)
            int[] ordinaller = new int[istek.Eslesmeler.Count];
            for (int i = 0; i < istek.Eslesmeler.Count; i++)
                ordinaller[i] = okuyucu.GetOrdinal(istek.Eslesmeler[i].KaynakKolon);

            // Önce temizle (açık onaylı): DELETE — TRUNCATE değil (yetki/FK dostu; geri alınabilir sınıf).
            // Tablo adı PARÇA PARÇA TIRNAKLI (inceleme 2026-07-30, KRİTİK): ham ad PG/Oracle'da
            // büyük-küçük katlanıp aynı adlı İKİZE (yanlış tabloya) gidebiliyordu.
            if (istek.OnceTemizle)
            {
                await using DbCommand sil = hedef.CreateCommand();
                sil.CommandText = $"DELETE FROM {AktarimEslestirici.TabloYaz(hedefLehce.TirnaklaTanimlayici, istek.HedefTablo)}";
                sil.CommandTimeout = istek.Hedef.KomutTimeoutSn;
                await sil.ExecuteNonQueryAsync(ct);
            }

            // Hedef: hazır parametreli INSERT (satır başına yalnız değer ataması değişir)
            string onek = AktarimEslestirici.ParametreOneki(hedefLehce.MotorId);
            await using DbCommand yaz = hedef.CreateCommand();
            yaz.CommandText = AktarimEslestirici.HedefInsert(
                hedefLehce.TirnaklaTanimlayici, istek.HedefTablo, istek.Eslesmeler, onek);
            yaz.CommandTimeout = istek.Hedef.KomutTimeoutSn; // profil timeout'u (0 = sınırsız)
            var parametreler = new DbParameter[istek.Eslesmeler.Count];
            for (int i = 0; i < parametreler.Length; i++)
            {
                DbParameter p = yaz.CreateParameter();
                p.ParameterName = $"{onek}p{i}";
                yaz.Parameters.Add(p);
                parametreler[i] = p;
            }

            // UPSERT (v12-S4): anahtara göre önce UPDATE, 0 satır etkilendiyse INSERT — bile bile
            // motor-bağımsız iki adım (MERGE/ON CONFLICT lehçe lehçe ayrışırdı; çoklu-motor kuralı).
            DbParameter?[] guncelleParametreleri = new DbParameter?[istek.Eslesmeler.Count];
            await using DbCommand? guncelle = GuncelleKur(istek, hedef, hedefLehce, onek, guncelleParametreleri);
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
                while (await okuyucu.ReadAsync(ct))
                {
                    okunan++;
                    for (int i = 0; i < parametreler.Length; i++)
                    {
                        object deger = okuyucu.GetValue(ordinaller[i]);
                        object atanacak = deger is DBNull ? DBNull.Value : deger;
                        parametreler[i].Value = atanacak;
                        if (guncelleParametreleri[i] is { } gp)
                            gp.Value = atanacak;
                    }

                    // PG'de satır hatası TÜM işlemi iptal durumuna (25P02) sokar: partinin kalan satırları
                    // "aborted" ile düşer, COMMIT sessizce ROLLBACK olur → veri kaybı + yanlış sayaç
                    // (inceleme 2026-07-30, KRİTİK). Savepoint destekleyen sürücüde satır öncesi savepoint
                    // alınır; hatada yalnız o satır geri sarılır, parti yaşar. Desteklemeyende davranış eski.
                    bool savepointli = istek.HataPolitikasi == AktarimHataPolitikasi.AtlaVeRaporla
                        && islem.SupportsSavepoints;
                    try
                    {
                        if (savepointli)
                            await islem.SaveAsync("satir", ct);

                        // UPDATE 1+ satır etkilediyse kaynak satırı "güncellendi" sayılır (satır bazlı sayım).
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
                    catch (DbException satirHatasi) when (istek.HataPolitikasi == AktarimHataPolitikasi.AtlaVeRaporla)
                    {
                        atlanan++;
                        if (savepointli)
                            await islem.RollbackAsync("satir", CancellationToken.None); // yalnız bu satır geri sarılır
                        if (hataOrnekleri.Count < EnCokHataOrnegi)
                            hataOrnekleri.Add($"Satır {okunan}: {satirHatasi.Message}");
                    }

                    if (partidekiEklenen + partidekiGuncellenen >= PartiBoyu)
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

                await islem.CommitAsync(CancellationToken.None); // son parti — iptal gelse de tamamlanmışı koru
            }
            catch (OperationCanceledException)
            {
                // Aktif parti geri alınır; commit'lenmiş partiler durur (dürüst sayım). Rollback best-effort:
                // commit/yeni-parti penceresinde işlem dispose edilmiş olabilir (inceleme 2026-07-30).
                try { await islem.RollbackAsync(CancellationToken.None); } catch (Exception) { }
                yazilan -= partidekiEklenen;
                guncellenen -= partidekiGuncellenen;
                return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan,
                    "Aktarım kullanıcı tarafından iptal edildi.", sure.Elapsed, hataOrnekleri, IptalEdildi: true);
            }
            catch (DbException hata)
            {
                // IlkHatadaDur: aktif parti geri alınır, o ana dek commit'lenen korunur.
                try { await islem.RollbackAsync(CancellationToken.None); } catch (Exception) { }
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
            // Bağlantı/okuma/temizleme düzeyi hata — kaynağı açıkça söyle.
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
        catch (InvalidOperationException ex)
        {
            // GetOrdinal (eşleşen kolon sonuçta yok) vb. yapılandırma hataları.
            return new AktarimSonucu(false, okunan, yazilan, guncellenen, atlanan, ex.Message, sure.Elapsed, hataOrnekleri);
        }
    }

    /// <summary>
    /// UPSERT'in UPDATE komutunu kurar; kip YalnizEkle ise ya da geçerli UPDATE kurulamıyorsa
    /// (anahtar yok / anahtar dışı kolon yok) null. Parametreler komut METNİNDEKİ geçiş sırasıyla
    /// eklenir (SET'ler, sonra WHERE'ler) — adla bağlamayan sürücüde de doğru eşleşsin;
    /// <paramref name="guncelleParametreleri"/> eşleşme İNDEKSİYLE doldurulur (satır döngüsü
    /// değerleri INSERT parametresiyle aynı indeksten atar).
    /// </summary>
    private static DbCommand? GuncelleKur(
        AktarimIstegi istek, DbConnection hedef, ILehce hedefLehce, string onek,
        DbParameter?[] guncelleParametreleri)
    {
        if (istek.YazmaKipi != AktarimYazmaKipi.EkleGuncelle)
            return null;

        IReadOnlyList<string> anahtarlar = istek.AnahtarKolonlar ?? [];
        string? sql = AktarimEslestirici.HedefUpdate(
            hedefLehce.TirnaklaTanimlayici, istek.HedefTablo, istek.Eslesmeler, anahtarlar, onek);
        if (sql is null)
            return null;

        DbCommand komut = hedef.CreateCommand();
        komut.CommandText = sql;
        komut.CommandTimeout = istek.Hedef.KomutTimeoutSn; // profil timeout'u (0 = sınırsız)
        var anahtarSeti = new HashSet<string>(anahtarlar, StringComparer.OrdinalIgnoreCase);
        foreach (bool anahtarMi in (bool[])[false, true]) // önce SET (anahtar dışı), sonra WHERE
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
