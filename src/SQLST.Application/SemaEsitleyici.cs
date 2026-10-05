using System.Text;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🔀 Şema Eşitleme script üreticisi (madde 4'ün ikinci yarısı, kullanıcı onayı 2026-08-03; yöntem
/// veri eşitlemeyle AYNI: fark satırlarını seçip "Seçilenleri/Tümünü eşitle"). Hedefi KAYNAĞA
/// eşitleyen DDL üretir — SALT METİN, ÇALIŞTIRMAZ.
///
/// v2 kapsamı (2026-08-03): tablo CREATE/DROP + kolon ADD/DROP/DEĞİŞTİR + FK ADD + Index
/// CREATE/DROP + gövde taşıma. v19-S4 kapanışı: (1) hedefteki fazla FK'nın DROP'u — kısıt ADI
/// artık modelde (YabanciAnahtar.Ad, lehçe sorgularından gelir), sagFkler'den eşlenip üretilir;
/// (2) PK DEĞİŞİMİ — MSSQL'de tam blok (dinamik ad çözümlemeli DROP + gerekirse ALTER COLUMN +
/// ADD PRIMARY KEY, tablo başına TEK kez); diğer motorlarda adı bulan sorgu + kalıp DDL notu;
/// (3) GÖVDE DEĞİŞİMİ — "Degisti" view/SP/fonksiyon kaynak tanımıyla değiştirilir (MSSQL
/// CREATE OR ALTER · PG/Oracle CREATE OR REPLACE · MySQL DROP IF EXISTS + CREATE).
/// Sıra: önce KURUCU, sonra ⚠ YIKICI (DROP'lar) — silenler en sonda ve göz önünde.
/// </summary>
public static class SemaEsitleyici
{
    public static string ScriptUret(
        ILehce lehce, string hedefOzeti, IReadOnlyList<SemaFarkSatiri> secilen,
        IReadOnlyList<SemaNesnesi> solNesneler, IReadOnlyList<SemaNesnesi> sagNesneler,
        IReadOnlyList<YabanciAnahtar>? solFkler = null,
        IReadOnlyList<Indeks>? solIndeksler = null, IReadOnlyList<Indeks>? sagIndeksler = null,
        IReadOnlyDictionary<string, string>? govdeler = null,
        IReadOnlyList<YabanciAnahtar>? sagFkler = null)
    {
        string son = lehce.IfadeSonu;
        var kurucular = new List<string>();
        var yikicilar = new List<string>();
        var notlar = new List<string>();
        // PK değişimi tablo başına TEK blok üretir (aynı tablonun birden çok PK-kolon farkı olabilir).
        var pkYenidenKur = new Dictionary<string, List<SemaKolonu>>(StringComparer.OrdinalIgnoreCase);

        SemaNesnesi? Bul(IReadOnlyList<SemaNesnesi> liste, string tamAd, SemaNesneTuru tur)
            => liste.FirstOrDefault(n => n.Tur == tur
                && n.TamAd.Equals(tamAd, StringComparison.OrdinalIgnoreCase));

        string TamYaz(SemaNesnesi n) => string.IsNullOrEmpty(n.Sema)
            ? lehce.TirnaklaTanimlayici(n.Ad) : lehce.TamAdYaz(n.Sema, n.Ad);

        string GovdeTamAd(string tamAd)
        {
            int i = tamAd.IndexOf('.');
            return i < 0 ? lehce.TirnaklaTanimlayici(tamAd) : lehce.TamAdYaz(tamAd[..i], tamAd[(i + 1)..]);
        }

        foreach (SemaFarkSatiri fark in secilen)
        {
            if (fark.Kapsam.StartsWith("Kolon: ", StringComparison.Ordinal))
            {
                KolonFarki(fark, fark.Kapsam[7..]);
                continue;
            }

            switch (fark.Kapsam)
            {
                case nameof(SemaNesneTuru.Tablo) when fark.Tur == SemaDegisim.YalnizSol:
                {
                    if (Bul(solNesneler, fark.Nesne, SemaNesneTuru.Tablo) is not { } tablo)
                    { notlar.Add($"-- ⚠ {fark.Nesne}: kaynak şemada bulunamadı — atlandı."); break; }
                    var yeni = new YeniTablo(tablo.Sema, tablo.Ad,
                        [.. tablo.Kolonlar.Select(k => new YeniKolon(k.Ad, k.Tip, k.NullOlabilir, k.PkMi))]);
                    (string? sql, string? hata) = TabloOlusturucu.Uret(yeni, lehce);
                    if (sql is not null)
                    {
                        kurucular.Add(sql);
                        notlar.Add($"-- ℹ {fark.Nesne}: FK/index'ler CREATE'e dahil DEĞİL — FK farkları ayrıca listelenir.");
                    }
                    else
                    {
                        notlar.Add($"-- ⚠ {fark.Nesne}: CREATE üretilemedi — {hata}");
                    }
                    break;
                }

                case nameof(SemaNesneTuru.Tablo) when fark.Tur == SemaDegisim.YalnizSag:
                {
                    SemaNesnesi? tablo = Bul(sagNesneler, fark.Nesne, SemaNesneTuru.Tablo);
                    string tam = tablo is not null
                        ? TamYaz(tablo)
                        : lehce.TirnaklaTanimlayici(fark.Nesne);
                    yikicilar.Add($"DROP TABLE {tam}{son}");
                    break;
                }

                case "FK" when fark.Tur == SemaDegisim.YalnizSol:
                {
                    YabanciAnahtar? fk = (solFkler ?? []).FirstOrDefault(f =>
                        $"{f.KaynakSema}.{f.KaynakTablo}".Equals(fark.Nesne, StringComparison.OrdinalIgnoreCase)
                        && SemaKarsilastirici.FkImza(f) == fark.Detay);
                    if (fk is null)
                    { notlar.Add($"-- ⚠ FK {fark.Nesne} · {fark.Detay}: kaynak FK listesinde bulunamadı — atlandı."); break; }
                    string ad = $"FK_{fk.KaynakTablo}_{fk.HedefTablo}_esitle"; // deterministik ad (FkScripti deseni)
                    kurucular.Add($"ALTER TABLE {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} "
                        + $"ADD CONSTRAINT {lehce.TirnaklaTanimlayici(ad)} "
                        + $"FOREIGN KEY ({string.Join(", ", fk.KaynakKolonlar.Select(lehce.TirnaklaTanimlayici))}) "
                        + $"REFERENCES {lehce.TamAdYaz(fk.HedefSema, fk.HedefTablo)} "
                        + $"({string.Join(", ", fk.HedefKolonlar.Select(lehce.TirnaklaTanimlayici))}){son}");
                    break;
                }

                case "FK": // YalnizSag — v19-S4: kısıt ADI artık modelde (lehçe sorgusundan) → DROP üretilir
                {
                    YabanciAnahtar? fk = (sagFkler ?? []).FirstOrDefault(f =>
                        $"{f.KaynakSema}.{f.KaynakTablo}".Equals(fark.Nesne, StringComparison.OrdinalIgnoreCase)
                        && SemaKarsilastirici.FkImza(f) == fark.Detay);
                    if (fk?.Ad is { Length: > 0 } fkAd)
                    {
                        // MySQL'de söz dizimi farklı: DROP FOREIGN KEY; diğer üçü DROP CONSTRAINT.
                        string birim = lehce.MotorId == "mysql" ? "FOREIGN KEY" : "CONSTRAINT";
                        yikicilar.Add($"ALTER TABLE {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} "
                            + $"DROP {birim} {lehce.TirnaklaTanimlayici(fkAd)}{son}");
                    }
                    else
                    {
                        notlar.Add($"-- ⚠ Hedefte fazla FK ({fark.Nesne} · {fark.Detay}): kısıt adı okunamadı — "
                            + "adı bulun: " + FkAdSorgusu(lehce.MotorId, fark.Nesne)
                            + " — sonra ALTER TABLE … DROP CONSTRAINT <ad>.");
                    }
                    break;
                }

                case "Index" when fark.Tur == SemaDegisim.YalnizSol:
                {
                    Indeks? ix = (solIndeksler ?? []).FirstOrDefault(i =>
                        i.TamTablo.Equals(fark.Nesne, StringComparison.OrdinalIgnoreCase)
                        && SemaKarsilastirici.IndeksImza(i) == fark.Detay);
                    if (ix is null)
                    { notlar.Add($"-- ⚠ Index {fark.Nesne} · {fark.Detay}: kaynak listede bulunamadı — atlandı."); break; }
                    kurucular.Add($"CREATE {(ix.Benzersiz ? "UNIQUE " : "")}INDEX {lehce.TirnaklaTanimlayici(ix.Ad)} "
                        + $"ON {lehce.TamAdYaz(ix.Sema, ix.Tablo)} "
                        + $"({string.Join(", ", ix.Kolonlar.Select(lehce.TirnaklaTanimlayici))}){son}");
                    break;
                }

                case "Index": // YalnizSag — ad HEDEF listesinden bilinir → DROP üretilebilir
                {
                    Indeks? ix = (sagIndeksler ?? []).FirstOrDefault(i =>
                        i.TamTablo.Equals(fark.Nesne, StringComparison.OrdinalIgnoreCase)
                        && SemaKarsilastirici.IndeksImza(i) == fark.Detay);
                    if (ix is null)
                    { notlar.Add($"-- ⚠ Index {fark.Nesne} · {fark.Detay}: hedef listede bulunamadı — atlandı."); break; }
                    yikicilar.Add(lehce.MotorId is "mssql" or "mysql"
                        ? $"DROP INDEX {lehce.TirnaklaTanimlayici(ix.Ad)} ON {lehce.TamAdYaz(ix.Sema, ix.Tablo)}{son}"
                        : $"DROP INDEX {lehce.TirnaklaTanimlayici(ix.Ad)}{son}");
                    break;
                }

                // View/SP/Fonksiyon varlık farkları: gövdeler VM'de TanimGetir ile çekilip gelir.
                case nameof(SemaNesneTuru.View) or nameof(SemaNesneTuru.StoredProcedure)
                    or nameof(SemaNesneTuru.Fonksiyon) when fark.Tur == SemaDegisim.YalnizSol:
                {
                    if (govdeler is not null && govdeler.TryGetValue(fark.Nesne, out string? govde)
                        && !string.IsNullOrWhiteSpace(govde))
                    {
                        // PG view tanımı çıplak SELECT gelebilir — CREATE ile başlamıyorsa sarmala.
                        string metin = govde.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
                            ? govde.TrimEnd()
                            : $"CREATE VIEW {GovdeTamAd(fark.Nesne)} AS\n{govde.TrimEnd()}";
                        kurucular.Add(metin.EndsWith(";", StringComparison.Ordinal) || son.Length == 0
                            ? metin : metin + son);
                    }
                    else
                    {
                        notlar.Add($"-- ⚠ {fark.Kapsam} {fark.Nesne}: tanımı okunamadı — "
                            + "nesneye sağ tık → tanım ya da 📤 Şema Kopyalama kullanın.");
                    }
                    break;
                }

                // v19-S4: gövdesi DEĞİŞEN nesne kaynak tanımıyla değiştirilir (VM gövde kıyası üretir).
                case nameof(SemaNesneTuru.View) or nameof(SemaNesneTuru.StoredProcedure)
                    or nameof(SemaNesneTuru.Fonksiyon) when fark.Tur == SemaDegisim.Degisti:
                {
                    if (govdeler is null || !govdeler.TryGetValue(fark.Nesne, out string? govde)
                        || string.IsNullOrWhiteSpace(govde))
                    {
                        notlar.Add($"-- ⚠ {fark.Kapsam} {fark.Nesne} (gövde farklı): kaynak tanımı okunamadı — elle taşıyın.");
                        break;
                    }
                    kurucular.Add(DegistirmeScripti(lehce, fark.Kapsam, fark.Nesne, govde, son, GovdeTamAd));
                    break;
                }

                case nameof(SemaNesneTuru.View) or nameof(SemaNesneTuru.StoredProcedure)
                    or nameof(SemaNesneTuru.Fonksiyon): // YalnizSag → hedeften düşür
                {
                    string tamNesne = GovdeTamAd(fark.Nesne);
                    yikicilar.Add(fark.Kapsam switch
                    {
                        nameof(SemaNesneTuru.View) => $"DROP VIEW {tamNesne}{son}",
                        nameof(SemaNesneTuru.StoredProcedure) => $"DROP PROCEDURE {tamNesne}{son}",
                        _ => $"DROP FUNCTION {tamNesne}{son}",
                    });
                    break;
                }

                default:
                    notlar.Add($"-- ⚠ desteklenmeyen fark ({fark.Kapsam}): {fark.Nesne} · {fark.Detay} — elle uygulayın.");
                    break;
            }
        }

        // v19-S4: PK yeniden-kurulum blokları (tablo başına TEK kez, kurucular bölümünün sonunda).
        foreach ((string tabloAd, List<SemaKolonu> degisenler) in pkYenidenKur)
        {
            SemaNesnesi? solTablo = Bul(solNesneler, tabloAd, SemaNesneTuru.Tablo);
            if (solTablo is null)
            { notlar.Add($"-- ⚠ {tabloAd}: PK değişimi için kaynak tablo bulunamadı — atlandı."); continue; }
            IReadOnlyList<SemaKolonu> pkKolonlari = [.. solTablo.Kolonlar.Where(k => k.PkMi)];
            if (pkKolonlari.Count == 0)
            { notlar.Add($"-- ⚠ {tabloAd}: kaynakta PK kolonu yok — PK bloğu üretilmedi."); continue; }

            string tam = TamYaz(solTablo);
            string pkListe = string.Join(", ", pkKolonlari.Select(k => lehce.TirnaklaTanimlayici(k.Ad)));
            if (lehce.MotorId == "mssql")
            {
                // Dinamik ad çözümlemesi: hedefteki PK kısıtının adı bilinmez → sys.key_constraints'ten
                // bulunup EXEC ile düşürülür; ardından değişen kolonlar ALTER edilir (PK bağlıyken tip
                // değiştirilemez — sıra bunun için) ve kaynak PK kümesi kurulur. TEK batch (GO yok).
                var blok = new StringBuilder();
                blok.AppendLine($"-- ⚠ PK değişimi ({tabloAd}): önce mevcut PK düşürülür, kolonlar uyarlanır, kaynak PK kurulur.");
                blok.AppendLine($"DECLARE @pk sysname = (SELECT name FROM sys.key_constraints WHERE [type] = 'PK' AND parent_object_id = OBJECT_ID(N'{tabloAd.Replace("'", "''")}'));");
                blok.AppendLine($"IF @pk IS NOT NULL EXEC(N'ALTER TABLE {tam.Replace("'", "''")} DROP CONSTRAINT ' + QUOTENAME(@pk));");
                foreach (SemaKolonu k in degisenler)
                    blok.AppendLine($"ALTER TABLE {tam} ALTER COLUMN {lehce.TirnaklaTanimlayici(k.Ad)} {k.Tip} {(k.NullOlabilir ? "NULL" : "NOT NULL")};");
                blok.Append($"ALTER TABLE {tam} ADD CONSTRAINT {lehce.TirnaklaTanimlayici($"PK_{solTablo.Ad}_esitle")} PRIMARY KEY ({pkListe});");
                kurucular.Add(blok.ToString());
            }
            else
            {
                // Diğer motorlarda dinamik ad çözümü tek script'te taşınabilir değil — adı bulan
                // sorgu + kalıp DDL verilir (dürüst sınır; kullanıcı adı yerine koyup koşar).
                notlar.Add($"-- ⚠ PK değişimi ({tabloAd}): mevcut PK'nın adını bulun: {PkAdSorgusu(lehce.MotorId, tabloAd)}");
                notlar.Add($"--   sonra: ALTER TABLE {tam} DROP CONSTRAINT <ad>{son} ALTER TABLE {tam} ADD PRIMARY KEY ({pkListe}){son}");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"-- 🔀 Şema Eşitleme · yön: KAYNAK → HEDEF · {secilen.Count} fark seçildi");
        sb.AppendLine($"-- HEDEF: {hedefOzeti} — script'i HEDEF bağlantıda çalıştırın (burada ÇALIŞTIRILMADI).");
        sb.AppendLine($"-- {kurucular.Count} kurucu ifade · {yikicilar.Count} YIKICI ifade"
            + (notlar.Count > 0 ? $" · {notlar.Count} not" : ""));
        sb.AppendLine("-- İnceleyip Güvenli Yazma AÇIKKEN koşmanız önerilir (DDL geri alınabilirliği motora göre değişir).");
        sb.AppendLine();
        foreach (string n in notlar) sb.AppendLine(n);
        if (notlar.Count > 0) sb.AppendLine();
        foreach (string k in kurucular) { sb.AppendLine(k); sb.AppendLine(); }
        if (yikicilar.Count > 0)
        {
            sb.AppendLine("-- ⚠⚠ YIKICI BÖLÜM: aşağıdaki ifadeler HEDEFTEKİ veriyi/nesneyi SİLER — emin değilseniz silin.");
            foreach (string y in yikicilar) sb.AppendLine(y);
        }
        return sb.ToString().TrimEnd();

        static string PkAdSorgusu(string motorId, string tablo)
        {
            string yalin = tablo.Contains('.') ? tablo[(tablo.LastIndexOf('.') + 1)..] : tablo;
            return motorId switch
            {
                "postgres" => $"SELECT conname FROM pg_constraint WHERE contype = 'p' AND conrelid = to_regclass('{tablo}')",
                "mysql" => "-- MySQL'de PK adı hep 'PRIMARY': ALTER TABLE … DROP PRIMARY KEY; … ADD PRIMARY KEY (…)",
                _ => $"SELECT constraint_name FROM all_constraints WHERE constraint_type = 'P' AND table_name = '{yalin.ToUpperInvariant()}'",
            };
        }

        /// <summary>Gövdesi değişen nesnenin motor-özel DEĞİŞTİRME script'i (v19-S4).</summary>
        static string DegistirmeScripti(
            ILehce lehce, string kapsam, string tamAd, string govde, string son, Func<string, string> govdeTamAd)
        {
            string metin = govde.Trim();
            bool createLi = metin.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase);

            switch (lehce.MotorId)
            {
                case "mssql":
                    // CREATE OR ALTER (SQL 2016 SP1+): DROP gerektirmeden yerine koyar, izinler korunur.
                    if (createLi && !metin.StartsWith("CREATE OR ALTER", StringComparison.OrdinalIgnoreCase))
                        metin = "CREATE OR ALTER" + metin["CREATE".Length..];
                    break;

                case "postgres" or "oracle":
                    if (!createLi)
                        metin = $"CREATE OR REPLACE VIEW {govdeTamAd(tamAd)} AS\n{metin}"; // PG çıplak view SELECT'i
                    else if (!metin.StartsWith("CREATE OR REPLACE", StringComparison.OrdinalIgnoreCase))
                        metin = "CREATE OR REPLACE" + metin["CREATE".Length..];
                    break;

                case "mysql":
                    if (kapsam == nameof(SemaNesneTuru.View))
                    {
                        if (createLi && !metin.StartsWith("CREATE OR REPLACE", StringComparison.OrdinalIgnoreCase))
                            metin = "CREATE OR REPLACE" + metin["CREATE".Length..];
                    }
                    else
                    {
                        // MySQL'de SP/fonksiyon için OR REPLACE yok → önce IF EXISTS ile düşür.
                        string birim = kapsam == nameof(SemaNesneTuru.StoredProcedure) ? "PROCEDURE" : "FUNCTION";
                        metin = $"DROP {birim} IF EXISTS {govdeTamAd(tamAd)}{son}\n{metin}";
                    }
                    break;
            }

            return metin.EndsWith(";", StringComparison.Ordinal) || son.Length == 0 ? metin : metin + son;
        }

        static string FkAdSorgusu(string motorId, string tablo)
        {
            string yalin = tablo.Contains('.') ? tablo[(tablo.LastIndexOf('.') + 1)..] : tablo;
            return motorId switch
            {
                "mssql" => $"SELECT name FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'{tablo}')",
                "postgres" => $"SELECT conname FROM pg_constraint WHERE contype = 'f' AND conrelid = to_regclass('{tablo}')",
                "mysql" => "SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS "
                    + $"WHERE CONSTRAINT_TYPE = 'FOREIGN KEY' AND TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{yalin}'",
                _ => $"SELECT constraint_name FROM all_constraints WHERE constraint_type = 'R' AND table_name = '{yalin.ToUpperInvariant()}'",
            };
        }

        void KolonFarki(SemaFarkSatiri fark, string kolonAd)
        {
            SemaNesnesi? solTablo = Bul(solNesneler, fark.Nesne, SemaNesneTuru.Tablo);
            SemaNesnesi? sagTablo = Bul(sagNesneler, fark.Nesne, SemaNesneTuru.Tablo);
            SemaKolonu? solKolon = solTablo?.Kolonlar.FirstOrDefault(
                k => k.Ad.Equals(kolonAd, StringComparison.OrdinalIgnoreCase));
            string tam = solTablo is not null ? TamYaz(solTablo)
                : sagTablo is not null ? TamYaz(sagTablo) : lehce.TirnaklaTanimlayici(fark.Nesne);

            switch (fark.Tur)
            {
                case SemaDegisim.YalnizSol when solTablo is not null && solKolon is not null:
                {
                    // ADD COLUMN söz dizimi motorlara göre TabloOlusturucu'da hazır (Mod=Ekle).
                    (string? sql, string? hata) = TabloOlusturucu.Uret(
                        new YeniTablo(solTablo.Sema, solTablo.Ad,
                            [new YeniKolon(solKolon.Ad, solKolon.Tip, solKolon.NullOlabilir)],
                            TabloModu.Ekle), lehce);
                    if (sql is not null) kurucular.Add(sql);
                    else notlar.Add($"-- ⚠ {fark.Nesne}.{kolonAd}: ADD üretilemedi — {hata}");
                    break;
                }

                case SemaDegisim.YalnizSag:
                    yikicilar.Add($"ALTER TABLE {tam} DROP COLUMN {lehce.TirnaklaTanimlayici(kolonAd)}{son}");
                    break;

                case SemaDegisim.Degisti when solKolon is not null:
                {
                    if (fark.Detay.Contains(" PK", StringComparison.Ordinal))
                    {
                        // v19-S4: PK değişimi — tablo başına TEK yeniden-kurulum bloğu (çıkışta üretilir).
                        // Kolonun ALTER'ı da o bloğa girer: PK bağlıyken tip değiştirilemez, sıra şart.
                        if (!pkYenidenKur.TryGetValue(fark.Nesne, out List<SemaKolonu>? kolonListe))
                            pkYenidenKur[fark.Nesne] = kolonListe = [];
                        kolonListe.Add(solKolon);
                        break;
                    }
                    string kq = lehce.TirnaklaTanimlayici(kolonAd);
                    string nul = solKolon.NullOlabilir ? "NULL" : "NOT NULL";
                    kurucular.Add(lehce.MotorId switch
                    {
                        "mssql" => $"ALTER TABLE {tam} ALTER COLUMN {kq} {solKolon.Tip} {nul}{son}",
                        "postgres" => $"ALTER TABLE {tam} ALTER COLUMN {kq} TYPE {solKolon.Tip}{son}\n"
                            + $"ALTER TABLE {tam} ALTER COLUMN {kq} "
                            + $"{(solKolon.NullOlabilir ? "DROP NOT NULL" : "SET NOT NULL")}{son}",
                        "mysql" => $"ALTER TABLE {tam} MODIFY {kq} {solKolon.Tip} {nul}{son}",
                        // Oracle: zaten aynı NULL kısıtını yeniden atamak ORA-01442/01451 verir —
                        // script yorumu kullanıcıyı uyarır (satırı gerekirse budar).
                        _ => $"ALTER TABLE {tam} MODIFY ({kq} {solKolon.Tip} {nul}){son} "
                            + "-- NULL kısıtı zaten aynıysa bu bölümü satırdan silin (ORA-01442/01451)",
                    });
                    break;
                }

                default:
                    notlar.Add($"-- ⚠ {fark.Nesne}.{kolonAd}: kaynak kolon bilgisi bulunamadı — atlandı.");
                    break;
            }
        }
    }
}
