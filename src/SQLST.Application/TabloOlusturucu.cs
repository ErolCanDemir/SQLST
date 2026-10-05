using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>Sihirbazda girilen tek kolon. Çekirdek: ad · tip · null · PK · IDENTITY · varsayılan.
/// Borç kapanışı: UNIQUE (benzersiz) · CHECK (kontrol ifadesi) · FK (hedef tablo + kolon).</summary>
public sealed record YeniKolon(
    string Ad, string Tip, bool NullOlabilir = false, bool PkMi = false,
    bool IdentityMi = false, string? Varsayilan = null,
    bool BenzersizMi = false, string? Kontrol = null,
    string? FkTablo = null, string? FkKolon = null);

/// <summary>Sihirbazın çalışma kipi: yeni tablo (CREATE) ya da var olan tabloya sütun ekleme (ALTER).</summary>
public enum TabloModu { Olustur, Ekle }

/// <summary>Sihirbazda tanımlanan tablo: şema (varsa) + ad + kolonlar + kip.</summary>
public sealed record YeniTablo(string? Sema, string Ad, IReadOnlyList<YeniKolon> Kolonlar,
    TabloModu Mod = TabloModu.Olustur);

/// <summary>
/// "Tablo oluştur/düzenle" sihirbazının SAF üreticisi (v8-S1/S2 + borç kapanışı): girilen kolon
/// modelinden motor-farkında <c>CREATE TABLE</c> ya da <c>ALTER TABLE … ADD</c> script'i üretir.
/// UI/IO yok — script yeni sorgu sekmesinde AÇILIR (hiçbir DDL kendiliğinden çalışmaz; kullanıcı F5).
///
/// <b>Kısıtlar</b> (dört motorda ANSI): PK · UNIQUE · CHECK · FK. <b>Motor farkı</b> identity/ifade
/// sonu/DEFAULT konumu + ALTER ADD yapısındadır: MSSQL <c>ADD col …</c> · PG/MySQL <c>ADD COLUMN …</c>
/// · Oracle <c>ADD (…)</c> (parantezli, ';' yok). <b>MySQL/Oracle canlı doğrulama borcu</b> sürüyor
/// (elde sunucu yok; SQL üretimi testli — kısıt/ALTER dahil).
/// </summary>
public static class TabloOlusturucu
{
    public static (string? Sql, string? Hata) Uret(YeniTablo tablo, ILehce lehce)
    {
        if (Dogrula(tablo) is { } hata)
            return (null, hata);

        string tam = string.IsNullOrWhiteSpace(tablo.Sema)
            ? lehce.TirnaklaTanimlayici(tablo.Ad)
            : lehce.TamAdYaz(tablo.Sema, tablo.Ad);
        string son = lehce.IfadeSonu; // MSSQL/PG/MySQL ";", Oracle ""

        string sql = tablo.Mod == TabloModu.Ekle
            ? AlterUret(tablo, lehce, tam, son)
            : CreateUret(tablo, lehce, tam, son);
        return (sql, null);
    }

    private static string? Dogrula(YeniTablo tablo)
    {
        if (string.IsNullOrWhiteSpace(tablo.Ad))
            return "Tablo adı boş olamaz.";
        if (tablo.Kolonlar.Count == 0)
            return tablo.Mod == TabloModu.Ekle ? "En az bir eklenecek kolon gerekir." : "En az bir kolon gerekir.";

        var gorulen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (YeniKolon k in tablo.Kolonlar)
        {
            if (string.IsNullOrWhiteSpace(k.Ad))
                return "Kolon adı boş olamaz.";
            if (string.IsNullOrWhiteSpace(k.Tip))
                return $"'{k.Ad}' kolonunun tipi boş olamaz.";
            if (!gorulen.Add(k.Ad))
                return $"Kolon adı tekrar ediyor: {k.Ad}.";
            if (k.PkMi && k.NullOlabilir)
                return $"'{k.Ad}' birincil anahtar (PK) olduğundan NULL olamaz.";
            // FK: hedef tablo ve kolon birlikte verilmeli (biri boş, öbürü dolu olamaz).
            bool fkT = !string.IsNullOrWhiteSpace(k.FkTablo), fkK = !string.IsNullOrWhiteSpace(k.FkKolon);
            if (fkT ^ fkK)
                return $"'{k.Ad}' için yabancı anahtar hem hedef tablo hem hedef kolon ister.";
        }
        if (tablo.Kolonlar.Count(k => k.IdentityMi) > 1)
            return "Bir tabloda yalnız bir otomatik artan (IDENTITY) kolon olabilir.";

        // ALTER (sütun ekleme) kipi: var olan tabloya PK/IDENTITY eklemek mevcut satırlar yüzünden
        // riskli/motor-özel — sihirbazda engellenir (kullanıcı bunu bilerek script'i elle yazsın).
        if (tablo.Mod == TabloModu.Ekle)
        {
            if (tablo.Kolonlar.Any(k => k.PkMi))
                return "Sütun ekleme (ALTER) kipinde PK eklenemez — yeni tablo kipini kullanın ya da PK'yı elle ekleyin.";
            if (tablo.Kolonlar.Any(k => k.IdentityMi))
                return "Sütun ekleme (ALTER) kipinde IDENTITY eklenemez.";
        }
        return null;
    }

    private static string CreateUret(YeniTablo tablo, ILehce lehce, string tam, string son)
    {
        var satirlar = tablo.Kolonlar.Select(k => "    " + KolonSatiri(k, lehce)).ToList();
        satirlar.AddRange(Kisitlar(tablo, lehce).Select(c => "    " + c));
        return $"CREATE TABLE {tam} (\n{string.Join(",\n", satirlar)}\n){son}";
    }

    private static string AlterUret(YeniTablo tablo, ILehce lehce, string tam, string son)
    {
        List<string> defs = tablo.Kolonlar.Select(k => KolonSatiri(k, lehce)).ToList();
        var ifadeler = new List<string>
        {
            lehce.MotorId switch
            {
                "mssql"  => $"ALTER TABLE {tam} ADD {string.Join(", ", defs)}{son}",          // COLUMN yok
                "oracle" => $"ALTER TABLE {tam} ADD ({string.Join(", ", defs)}){son}",         // parantezli, ';' yok
                _        => $"ALTER TABLE {tam} {string.Join(", ", defs.Select(d => "ADD COLUMN " + d))}{son}",
            },
        };
        // UNIQUE/CHECK/FK: kolonlar eklendikten SONRA ayrı ADD CONSTRAINT ifadeleriyle (dört motorda ANSI).
        ifadeler.AddRange(Kisitlar(tablo, lehce).Select(c => $"ALTER TABLE {tam} ADD {c}{son}"));
        return string.Join("\n", ifadeler);
    }

    /// <summary>Tablo düzeyi kısıt tanımları: PK (bileşik) · UNIQUE · CHECK · FK (kolon başına).
    /// Hepsi <c>CONSTRAINT ad …</c> biçiminde; CREATE'te parantez içine, ALTER'da ADD ile kullanılır.</summary>
    private static IEnumerable<string> Kisitlar(YeniTablo tablo, ILehce lehce)
    {
        var pk = tablo.Kolonlar.Where(k => k.PkMi).ToList();
        if (pk.Count > 0)
            yield return $"CONSTRAINT {KisitAdi(lehce, "PK", tablo.Ad)} PRIMARY KEY "
                + $"({string.Join(", ", pk.Select(k => lehce.TirnaklaTanimlayici(k.Ad)))})";

        foreach (YeniKolon k in tablo.Kolonlar.Where(k => k.BenzersizMi && !k.PkMi))
            yield return $"CONSTRAINT {KisitAdi(lehce, "UQ", tablo.Ad, k.Ad)} UNIQUE ({lehce.TirnaklaTanimlayici(k.Ad)})";

        foreach (YeniKolon k in tablo.Kolonlar.Where(k => !string.IsNullOrWhiteSpace(k.Kontrol)))
            yield return $"CONSTRAINT {KisitAdi(lehce, "CK", tablo.Ad, k.Ad)} CHECK ({k.Kontrol!.Trim()})";

        foreach (YeniKolon k in tablo.Kolonlar.Where(k => !string.IsNullOrWhiteSpace(k.FkTablo)))
            yield return $"CONSTRAINT {KisitAdi(lehce, "FK", tablo.Ad, k.Ad)} FOREIGN KEY "
                + $"({lehce.TirnaklaTanimlayici(k.Ad)}) REFERENCES {NitelenmisAd(k.FkTablo!, lehce)} "
                + $"({lehce.TirnaklaTanimlayici(k.FkKolon!.Trim())})";
    }

    private static string KisitAdi(ILehce lehce, string on, string tablo, string? kolon = null)
        => lehce.TirnaklaTanimlayici(kolon is null ? $"{on}_{tablo}" : $"{on}_{tablo}_{kolon}");

    /// <summary>"şema.tablo" ya da "tablo" — parçaları ayrı tırnaklar (FK hedefi kullanıcıdan gelir).</summary>
    private static string NitelenmisAd(string ad, ILehce lehce)
    {
        int nokta = ad.LastIndexOf('.');
        return nokta < 0
            ? lehce.TirnaklaTanimlayici(ad.Trim())
            : lehce.TamAdYaz(ad[..nokta].Trim(), ad[(nokta + 1)..].Trim());
    }

    /// <summary>Motor-farkında tek kolon tanımı. Sıra (identity/null/default) motora göre değişir.</summary>
    private static string KolonSatiri(YeniKolon k, ILehce lehce)
    {
        string q = lehce.TirnaklaTanimlayici(k.Ad);
        string tip = k.Tip.Trim();
        string nullClause = k.NullOlabilir ? "NULL" : "NOT NULL";
        // Identity kolonda DEFAULT çakışır — verilmez.
        string def = !k.IdentityMi && !string.IsNullOrWhiteSpace(k.Varsayilan)
            ? " DEFAULT " + (lehce.MotorId == "mssql" ? $"({k.Varsayilan.Trim()})" : k.Varsayilan.Trim())
            : "";

        return lehce.MotorId switch
        {
            // MSSQL: col type [IDENTITY(1,1)] (NULL|NOT NULL) [DEFAULT (x)]
            "mssql" => $"{q} {tip}{(k.IdentityMi ? " IDENTITY(1,1)" : "")} {nullClause}{def}",

            // PostgreSQL: identity → GENERATED (NOT NULL örtük); değilse col type (NULL|NOT NULL) [DEFAULT x]
            "postgres" => k.IdentityMi
                ? $"{q} {tip} GENERATED BY DEFAULT AS IDENTITY"
                : $"{q} {tip} {nullClause}{def}",

            // MySQL: identity → col type NOT NULL AUTO_INCREMENT; değilse col type (NULL|NOT NULL) [DEFAULT x]
            "mysql" => k.IdentityMi
                ? $"{q} {tip} NOT NULL AUTO_INCREMENT"
                : $"{q} {tip} {nullClause}{def}",

            // Oracle: identity → GENERATED (NOT NULL örtük); değilse col type [DEFAULT x] [NOT NULL] (NULL varsayılan, yazılmaz)
            "oracle" => k.IdentityMi
                ? $"{q} {tip} GENERATED BY DEFAULT AS IDENTITY"
                : $"{q} {tip}{def}{(k.NullOlabilir ? "" : " NOT NULL")}",

            _ => $"{q} {tip} {nullClause}{def}",
        };
    }
}
