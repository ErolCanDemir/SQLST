using System.Text;
using System.Text.RegularExpressions;
using SQLST.Contracts;

namespace SQLST.Application;

/// <summary>
/// 🕸 Kayıt Haritası çekirdeği (kullanıcı fikri 2026-07-31: "bu satırın bağlı olduğu tüm satırları
/// çıkart, sonra SELECT'e alabileyim"). SAF ve testli: seçili satırın FK'larla bağlı ÜST (satırın
/// işaret ettikleri) ve ALT (bu satırı işaret edenler) kayıt sorgularını üretir. Sorgular yorumlu
/// TEK script'e dökülüp sekmede çalıştırılır — çoklu grid + düzenlenebilir SELECT'ler bir arada.
/// v1: 1 seviye derinlik, ilişki başına TOP 100 (kullanıcı onaylı varsayılanlar).
/// </summary>
public static partial class KayitHaritasi
{
    /// <summary>
    /// Tek ilişki sorgusu: başlık (yorum + [İlişki] kolonu) + SELECT metni.
    /// v22-S9 ekleri: <paramref name="VarlikSql"/> "dolu mu?" sondası, <paramref name="Seviye"/>
    /// 1 = kaynağın komşusu · 2 = komşunun komşusu, <paramref name="Sema"/>/<paramref name="Ad"/>
    /// 2. seviyeyi kurarken ve tekrarları elerken gerekir.
    /// </summary>
    /// <param name="VarlikSql">
    /// EN UCUZ "dolu mu" sondası: tek satır, tek sabit sütun — <c>SELECT TOP 1 1 FROM … WHERE …</c>.
    /// COUNT(*) BİLEREK kullanılmadı: milyonlarca satırlık tabloda tam tarama yaptırır, oysa bize
    /// yalnız "en az bir satır var mı" lazım (kullanıcı isteği: "dolu olan satırlara ait select'ler
    /// gelsin"). Sonda motor tarafında ilk eşleşmede durur.
    /// </param>
    /// <param name="Kosul">
    /// Bu ilişkinin WHERE koşulu — 2. seviye bunu ALT SORGU olarak kullanır, böylece 1. seviyenin
    /// satır değerlerini önce çekip sonra gömmeye gerek kalmaz.
    /// </param>
    public sealed record Iliski(
        string Baslik, string Sql, string VarlikSql, int Seviye, string Sema, string Ad, string Kosul);

    /// <summary>
    /// Son çalışan SELECT'ten TEK tablo adını çıkarır (şemalı/köşeli olabilir); JOIN'li/çok tablolu
    /// sorguda null — kaynak tablo belirsizdir, kullanıcıya söylenir.
    /// </summary>
    public static string? TabloCikar(string sql)
    {
        if (JoinDeseni().IsMatch(sql))
            return null;
        Match m = FromDeseni().Match(sql);
        return m.Success ? m.Groups["tablo"].Value.Replace("[", "").Replace("]", "") : null;
    }

    /// <summary>
    /// Satırın ilişki sorgularını üretir. ▲ üstler: satırdaki FK kolon değerleriyle hedef tablodan;
    /// ▼ altlar: bu tabloyu işaret eden FK'larla kaynak tablodan (satırın PK/hedef kolon değerleri).
    /// NULL değerli FK bacağı atlanır (bağ yok). Değerler <see cref="LiteralYazici"/> ile güvenle yazılır.
    /// Özellik eşitliği (2026-08-03): ILehce ile motor-parametrik — FK grafı önbellekte zaten
    /// motor-nötrdü, script yazımı da lehçeden gelir (PG/MySQL destekli).
    /// </summary>
    /// <param name="tablolar">
    /// Şemadaki tablolar — YALNIZ tanım/lookup süzgeci için gerekir (v22-S9). null verilirse süzgeç
    /// çalışmaz ve eski davranış aynen sürer.
    /// </param>
    /// <param name="tanimTablolariniAtla">
    /// v22-S9, kullanıcı isteği "lookup tablolarını getirmesin": tanım tablosuna giden ▲ üst
    /// ilişkiler atlanır. YALNIZ ▲ yönünde: bir tanım tablosunun ▼ altları (o tanımı kullanan gerçek
    /// kayıtlar) kullanıcının asıl aradığı şeydir, onlar elenmez.
    /// </param>
    public static IReadOnlyList<Iliski> IliskiSorgulari(
        SemaNesnesi tablo, IReadOnlyDictionary<string, object?> satir,
        IReadOnlyList<YabanciAnahtar> fkler, ILehce lehce, int tavan = 100,
        IReadOnlyList<SemaNesnesi>? tablolar = null, bool tanimTablolariniAtla = false)
    {
        LiteralKurallari kurallar = lehce.LiteralKurallari;
        var sonuc = new List<Iliski>();

        bool BuTablo(string sema, string ad)
            => ad.Equals(tablo.Ad, StringComparison.OrdinalIgnoreCase)
               && sema.Equals(tablo.Sema, StringComparison.OrdinalIgnoreCase);

        string? Kosul(IReadOnlyList<string> yerelKolonlar, IReadOnlyList<string> uzakKolonlar)
        {
            var parcalar = new List<string>();
            for (int i = 0; i < yerelKolonlar.Count && i < uzakKolonlar.Count; i++)
            {
                if (!satir.TryGetValue(yerelKolonlar[i], out object? deger) || deger is null or DBNull)
                    return null; // FK bacağı NULL/yok — bağ kurulmamış, ilişki atlanır
                parcalar.Add($"{lehce.TirnaklaTanimlayici(uzakKolonlar[i])} = {LiteralYazici.Yaz(deger, kurallar)}");
            }
            return parcalar.Count == 0 ? null : string.Join(" AND ", parcalar);
        }

        void Ekle(string yon, string sema, string ad, string iliskiOzeti, string kosul)
        {
            string baslik = $"{yon} {sema}.{ad} ({iliskiOzeti})";
            sonuc.Add(new Iliski(
                baslik,
                Secim(baslik, sema, ad, kosul, lehce, tavan, kurallar),
                VarlikSorgusu(sema, ad, kosul, lehce),
                Seviye: 1, sema, ad, kosul));
        }

        // Tanım/lookup süzgeci — ürünün TEK tanımı LookupCozumleyici'de (bkz. TanimTablosuMu).
        bool TanimMi(string sema, string ad)
        {
            if (!tanimTablolariniAtla || tablolar is null)
                return false;
            SemaNesnesi? hedef = tablolar.FirstOrDefault(t =>
                t.Tur == SemaNesneTuru.Tablo
                && t.Ad.Equals(ad, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(sema) || t.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
            return hedef is not null && LookupCozumleyici.TanimTablosuMu(hedef, fkler, out _, out _);
        }

        foreach (YabanciAnahtar fk in fkler)
        {
            if (BuTablo(fk.KaynakSema, fk.KaynakTablo)
                && !TanimMi(fk.HedefSema, fk.HedefTablo) // ▲ tanım tablosuna gitmek istenmiyor
                && Kosul(fk.KaynakKolonlar, fk.HedefKolonlar) is { } ustKosul)
                Ekle("▲", fk.HedefSema, fk.HedefTablo,
                    $"{string.Join(",", fk.KaynakKolonlar)} → {string.Join(",", fk.HedefKolonlar)}", ustKosul);

            if (BuTablo(fk.HedefSema, fk.HedefTablo)
                && Kosul(fk.HedefKolonlar, fk.KaynakKolonlar) is { } altKosul)
                Ekle("▼", fk.KaynakSema, fk.KaynakTablo,
                    $"{string.Join(",", fk.KaynakKolonlar)} → bu.{string.Join(",", fk.HedefKolonlar)}", altKosul);
        }

        return sonuc;
    }

    /// <summary>Görünen SELECT — başlık kolonu + tüm satır, ilişki başına <paramref name="tavan"/>.</summary>
    private static string Secim(
        string baslik, string sema, string ad, string kosul, ILehce lehce, int tavan,
        LiteralKurallari kurallar)
        => $"SELECT {lehce.SatirSinirBasi(tavan)}{LiteralYazici.Yaz(baslik, kurallar)} AS {lehce.TirnaklaTanimlayici("İlişki")}, * "
         + $"FROM {lehce.TamAdYaz(sema, ad)} WHERE {kosul}{lehce.SatirSinirSonu(tavan)};";

    /// <summary>"Dolu mu?" sondası — TEK satır, TEK sabit sütun; COUNT değil (gerekçe: <see cref="Iliski"/>).</summary>
    private static string VarlikSorgusu(string sema, string ad, string kosul, ILehce lehce)
        => $"SELECT {lehce.SatirSinirBasi(1)}1 FROM {lehce.TamAdYaz(sema, ad)} WHERE {kosul}{lehce.SatirSinirSonu(1)};";

    /// <summary>
    /// 🔗 2. SEVİYE (v22-S9, kullanıcı isteği: <i>"a tablosu var buraya getirdik; bu a tablosu da bir
    /// tabloda geçiyorsa ve o da doluysa ona ait select'i de getirsin"</i>).
    ///
    /// Koşul ALT SORGUYLA kurulur: 1. seviyenin satırlarını önce çekip değerlerini gömmek yerine
    /// <c>B.X IN (SELECT A.Y FROM A WHERE &lt;1. seviye koşulu&gt;)</c> yazılır. Böylece (a) fazladan
    /// veri çekilmez, (b) 1. seviyede birden çok satır varsa hepsi kapsanır, (c) her motorda çalışır.
    ///
    /// TEKRAR ELEME: kaynak tablo ve 1. seviyede zaten getirilen tablolar atlanır — yoksa harita
    /// kendi üstüne kapanır (A→B→A) ve aynı SELECT iki kez yazılır.
    /// </summary>
    public static IReadOnlyList<Iliski> IkinciSeviye(
        Iliski birinciSeviye, SemaNesnesi kaynakTablo,
        IReadOnlyList<YabanciAnahtar> fkler, ILehce lehce, int tavan = 100,
        IReadOnlyList<SemaNesnesi>? tablolar = null, bool tanimTablolariniAtla = false,
        ISet<string>? gorulen = null)
    {
        var sonuc = new List<Iliski>();
        string a = birinciSeviye.Ad, aSema = birinciSeviye.Sema;

        bool Atla(string sema, string ad)
        {
            if (ad.Equals(kaynakTablo.Ad, StringComparison.OrdinalIgnoreCase)
                && sema.Equals(kaynakTablo.Sema, StringComparison.OrdinalIgnoreCase))
                return true; // kaynağa geri dönme
            if (ad.Equals(a, StringComparison.OrdinalIgnoreCase)
                && sema.Equals(aSema, StringComparison.OrdinalIgnoreCase))
                return true; // kendine dönme
            return gorulen?.Contains($"{sema}.{ad}".ToLowerInvariant()) == true;
        }

        bool TanimMi(string sema, string ad)
        {
            if (!tanimTablolariniAtla || tablolar is null)
                return false;
            SemaNesnesi? hedef = tablolar.FirstOrDefault(t =>
                t.Tur == SemaNesneTuru.Tablo
                && t.Ad.Equals(ad, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(sema) || t.Sema.Equals(sema, StringComparison.OrdinalIgnoreCase)));
            return hedef is not null && LookupCozumleyici.TanimTablosuMu(hedef, fkler, out _, out _);
        }

        bool BuA(string sema, string ad)
            => ad.Equals(a, StringComparison.OrdinalIgnoreCase)
               && sema.Equals(aSema, StringComparison.OrdinalIgnoreCase);

        // A'nın koşulunu taşıyan alt sorgu: SELECT <A'nın kolonu> FROM A WHERE <A'nın koşulu>
        string AltSorgu(string aKolon)
            => $"SELECT {lehce.TirnaklaTanimlayici(aKolon)} FROM {lehce.TamAdYaz(aSema, a)} "
             + $"WHERE {birinciSeviye.Kosul}";

        // ÇOK KOLONLU FK (v23-S9 — canlı tanı sırasında bulunan boşluk): tuple-IN her motorda yok,
        // EXISTS her motorda var. Dış tablo (B) ve A kolonları TABLO ADIYLA nitelenir — ad çakışması
        // yanlış kolona bağlanmasın; birinciKosul A'nın niteliksiz kolonlarıyla yazıldı, EXISTS
        // içinde en yakın kapsam A olduğundan doğru çözülür. Eski davranış (tek kolon = IN) korunur.
        string CokluKosul(string disAd, IReadOnlyList<string> disKolonlar, IReadOnlyList<string> aKolonlar)
            => "EXISTS (SELECT 1 FROM " + lehce.TamAdYaz(aSema, a) + " WHERE "
             + string.Join(" AND ", disKolonlar.Zip(aKolonlar, (dis, ak) =>
                   $"{lehce.TirnaklaTanimlayici(a)}.{lehce.TirnaklaTanimlayici(ak)} = "
                 + $"{lehce.TirnaklaTanimlayici(disAd)}.{lehce.TirnaklaTanimlayici(dis)}"))
             + $" AND ({birinciSeviye.Kosul}))";

        void Ekle(string yon, string sema, string ad, string ozet, string kosul)
        {
            string baslik = $"{yon}{yon} {sema}.{ad} ({ozet}) · {aSema}.{a} üzerinden";
            sonuc.Add(new Iliski(
                baslik,
                Secim(baslik, sema, ad, kosul, lehce, tavan, lehce.LiteralKurallari),
                VarlikSorgusu(sema, ad, kosul, lehce),
                Seviye: 2, sema, ad, kosul));
            gorulen?.Add($"{sema}.{ad}".ToLowerInvariant());
        }

        foreach (YabanciAnahtar fk in fkler)
        {
            // A → B (A'nın işaret ettiği): B.hedefKolon IN (SELECT A.kaynakKolon FROM A WHERE …)
            if (BuA(fk.KaynakSema, fk.KaynakTablo)
                && fk.KaynakKolonlar.Count >= 1 && fk.HedefKolonlar.Count == fk.KaynakKolonlar.Count
                && !Atla(fk.HedefSema, fk.HedefTablo)
                && !TanimMi(fk.HedefSema, fk.HedefTablo))
                Ekle("▲", fk.HedefSema, fk.HedefTablo,
                    $"{string.Join(",", fk.KaynakKolonlar)} → {string.Join(",", fk.HedefKolonlar)}",
                    fk.KaynakKolonlar.Count == 1
                        ? $"{lehce.TirnaklaTanimlayici(fk.HedefKolonlar[0])} IN ({AltSorgu(fk.KaynakKolonlar[0])})"
                        : CokluKosul(fk.HedefTablo, fk.HedefKolonlar, fk.KaynakKolonlar));

            // B → A (A'yı işaret eden): B.kaynakKolon IN (SELECT A.hedefKolon FROM A WHERE …)
            if (BuA(fk.HedefSema, fk.HedefTablo)
                && fk.KaynakKolonlar.Count >= 1 && fk.HedefKolonlar.Count == fk.KaynakKolonlar.Count
                && !Atla(fk.KaynakSema, fk.KaynakTablo))
                Ekle("▼", fk.KaynakSema, fk.KaynakTablo,
                    $"{string.Join(",", fk.KaynakKolonlar)} → {aSema}.{a}.{string.Join(",", fk.HedefKolonlar)}",
                    fk.KaynakKolonlar.Count == 1
                        ? $"{lehce.TirnaklaTanimlayici(fk.KaynakKolonlar[0])} IN ({AltSorgu(fk.HedefKolonlar[0])})"
                        : CokluKosul(fk.KaynakTablo, fk.KaynakKolonlar, fk.HedefKolonlar));
        }

        return sonuc;
    }

    /// <summary>İlişki sorgularını yorumlu tek script'e döker — sekmede çalıştırılır/düzenlenir.</summary>
    public static string ScriptUret(string tablo, IReadOnlyDictionary<string, object?> satir, IReadOnlyList<Iliski> iliskiler)
        => $"-- 🕸 Kayıt Haritası · kaynak: {tablo} · {iliskiler.Count} ilişki (her biri TOP 100)\n"
         + "-- ▲ üst = satırın işaret ettikleri · ▼ alt = bu satırı işaret edenler. Düzenleyip F5 ile yeniden koşabilirsiniz.\n\n"
         + string.Join("\n\n", iliskiler.Select(i => $"-- {i.Baslik}\n{i.Sql}"));

    /// <summary>
    /// 🕸 SÜZÜLMÜŞ script (v22-S9): YALNIZ dolu ilişkiler yazılır.
    ///
    /// ⚠ TASARIM DEĞİŞİKLİĞİ (kullanıcı kararı 25 Ağu 2026: <i>"ilişki olmayanlar hiç gelmesin"</i>).
    /// İlk hâlde boş çıkan ilişkiler script sonunda yorum olarak listeleniyordu — gerekçem "sessiz
    /// kırpma yok" idi. Kullanıcı bunu istemedi: haritanın işi bağlı kayıtları GÖSTERMEK, olmayanları
    /// saymak değil; boş liste script'i uzatıp asıl sonucu gölgeliyordu. Sayı yine de başlıkta
    /// duruyor ("… ilişkinin N'i dolu"), yani kaç tanesinin elendiği kaybolmuyor — dökümü gitti.
    ///
    /// <paramref name="atlananTanim"/> BİLEREK duruyor: o bir "boş çıktı" değil, bizim ELEDİĞİMİZ
    /// bir tablo. Yazılmazsa kullanıcı "X tablosu neden yok?" diye sorar ve cevabı üründe bulamaz.
    /// Tek satırda, kısaca yazılır.
    /// </summary>
    public static string SuzulmusScriptUret(
        string tablo,
        IReadOnlyList<Iliski> dolular,
        int bosSayisi,
        IReadOnlyList<string> atlananTanim,
        string? sondaNotu = null)
    {
        int seviye2 = dolular.Count(i => i.Seviye == 2);
        var sb = new StringBuilder();
        sb.AppendLine($"-- 🕸 Kayıt Haritası · kaynak: {tablo}");
        sb.Append($"-- {dolular.Count} DOLU ilişki ({dolular.Count - seviye2} doğrudan · {seviye2} dolaylı), her biri TOP 100.");
        sb.AppendLine(bosSayisi > 0 ? $" ({bosSayisi} ilişki boş çıktı, getirilmedi.)" : "");
        sb.AppendLine("-- ▲ üst = işaret edilenler · ▼ alt = işaret edenler · ▲▲/▼▼ = bir tablo üzerinden dolaylı.");
        if (atlananTanim.Count > 0)
            sb.AppendLine($"-- Atlanan tanım/lookup tablosu: {string.Join(" · ", atlananTanim)}");
        if (sondaNotu is { Length: > 0 })
            sb.AppendLine($"-- {sondaNotu}");
        sb.AppendLine();

        foreach (Iliski i in dolular)
        {
            sb.AppendLine($"-- {i.Baslik}");
            sb.AppendLine(i.Sql);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 🗑 DELETE varyantı (kullanıcı isteği 2026-07-31): önce bu satırı işaret eden ▼ ALT kayıtlar,
    /// SONRA kaynak satırın kendisi (PK'dan). ▲ ÜSTLER BİLEREK DAHİL DEĞİL — başka kayıtların da
    /// ebeveyni olabilirler. Script ÇALIŞTIRILMADAN açılır (Güvenli Yazma + 🔍 önizlemeyle koşulmalı).
    /// PK'sız tabloda null (kaynak satır güvenle hedeflenemez). 1 seviye: torun kayıtlar FK engeli
    /// verebilir — script başındaki uyarı bunu söyler.
    /// </summary>
    public static string? DeleteScriptUret(
        SemaNesnesi tablo, IReadOnlyDictionary<string, object?> satir,
        IReadOnlyList<YabanciAnahtar> fkler, ILehce lehce)
    {
        LiteralKurallari kurallar = lehce.LiteralKurallari;

        IReadOnlyList<SemaKolonu> pk = [.. tablo.Kolonlar.Where(k => k.PkMi)];
        if (pk.Count == 0)
            return null;
        var pkKosul = new List<string>();
        foreach (SemaKolonu k in pk)
        {
            if (!satir.TryGetValue(k.Ad, out object? d) || d is null or DBNull)
                return null; // PK değeri sonuçta yok — güvenli hedefleme yapılamaz
            pkKosul.Add($"{lehce.TirnaklaTanimlayici(k.Ad)} = {LiteralYazici.Yaz(d, kurallar)}");
        }

        var satirlar = new List<string>
        {
            $"-- 🗑 Kayıt Haritası DELETE script'i · kaynak: {tablo.TamAd}",
            "-- SIRA: önce ▼ alt (bağımlı) kayıtlar, sonra kaynak satır. ▲ üst kayıtlar BİLEREK dahil değil.",
            "-- UYARI: 1 seviye — alt kayıtların da altları (torunlar) varsa FK engeli alırsınız.",
            "-- GÜVENLİ YAZMA AÇIKKEN çalıştırın: karar bandında 🔍 ile silinecekleri görüp COMMIT/ROLLBACK dersiniz.",
            "",
        };
        foreach (YabanciAnahtar fk in fkler)
        {
            if (!fk.HedefTablo.Equals(tablo.Ad, StringComparison.OrdinalIgnoreCase)
                || !fk.HedefSema.Equals(tablo.Sema, StringComparison.OrdinalIgnoreCase))
                continue;
            var kosul = new List<string>();
            bool tam = true;
            for (int i = 0; i < fk.KaynakKolonlar.Count && i < fk.HedefKolonlar.Count; i++)
            {
                if (!satir.TryGetValue(fk.HedefKolonlar[i], out object? d) || d is null or DBNull)
                { tam = false; break; }
                kosul.Add($"{lehce.TirnaklaTanimlayici(fk.KaynakKolonlar[i])} = {LiteralYazici.Yaz(d, kurallar)}");
            }
            if (tam && kosul.Count > 0)
                satirlar.Add($"DELETE FROM {lehce.TamAdYaz(fk.KaynakSema, fk.KaynakTablo)} WHERE {string.Join(" AND ", kosul)};");
        }
        satirlar.Add($"DELETE FROM {lehce.TamAdYaz(tablo.Sema, tablo.Ad)} WHERE {string.Join(" AND ", pkKosul)};");
        return string.Join("\n", satirlar);
    }

    /// <summary>
    /// 🕸 İNME köprüsü (v23-S9 — canlı tanı 1 Eki 2026, kullanıcı: "belgenin altındakilere
    /// inemiyoruz"): harita sekmesindeki bir grid'den satır seçilip TEKRAR harita istenince kaynak
    /// tablo, script'in İLK FROM'undan DEĞİL satırın [İlişki] kolonundaki başlıktan çözülür — her
    /// SELECT'in başlığı kendi tablosunu zaten taşıyor. Eski yolda Fatura satırından inişte
    /// faturanın Id'si İLK tablonun (Talepler) Id'si sanılıyor ve BAŞKA bir talebin haritası
    /// geliyordu (sessiz yanlış sonuç). Başlık biçimi: "▼▼ finans.Faturalar (…) · … üzerinden".
    /// </summary>
    public static (string Sema, string Ad)? IliskiBasligindanTablo(object? iliskiDegeri)
    {
        if (iliskiDegeri is not string s)
            return null;
        Match m = IliskiBasligiDeseni().Match(s.TrimStart());
        return m.Success ? (m.Groups["sema"].Value, m.Groups["ad"].Value) : null;
    }

    [GeneratedRegex(@"^[▲▼]+\s+(?:(?<sema>[^.\s(]+)\.)?(?<ad>[^\s(]+)")]
    private static partial Regex IliskiBasligiDeseni();

    [GeneratedRegex(@"\bJOIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex JoinDeseni();

    [GeneratedRegex(@"\bFROM\s+(?<tablo>[\w\[\]\.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromDeseni();
}
