using System.Xml.Linq;

namespace SQLST.Application;

/// <summary>Kilitlenmedeki tek süreç (v23-S3): SSMS'in oval düğümünün karşılığı.</summary>
/// <param name="KurbanMi">Sunucunun geri aldığı süreç — şemada üstü çizili/kırmızı gösterilir.</param>
public sealed record DeadlockSureci(
    string Id, int Spid, string? Uygulama, string? Kullanici, string? YalitimDuzeyi,
    string? Sorgu, bool KurbanMi);

/// <summary>Çekişilen kaynak: kim tutuyor (sahip), kim bekliyor — kilit kipleriyle.</summary>
public sealed record DeadlockKaynagi(
    string Ad, string Tur,
    IReadOnlyList<(string SurecId, string Kip)> Sahipler,
    IReadOnlyList<(string SurecId, string Kip)> Bekleyenler);

/// <summary>Çözülmüş kilitlenme grafiği — pencere şemayı bundan çizer.</summary>
public sealed record DeadlockGrafigi(
    IReadOnlyList<DeadlockSureci> Surecler,
    IReadOnlyList<DeadlockKaynagi> Kaynaklar);

/// <summary>
/// 🕸 xml_deadlock_report çözümleyicisi (v23-S3 — SAF, UI/IO yok): SQL Server'ın deadlock
/// XML'ini süreç/kaynak grafiğine açar. Biçim sürümler arasında küçük oynar — bilinmeyen düğümler
/// atlanır, zorunlu parçalar (process-list) yoksa null döner (çağıran ham XML'i gösterir; şema
/// çizilemedi diye bilgi KAYBOLMAZ).
/// </summary>
public static class DeadlockCozumleyici
{
    public static DeadlockGrafigi? Coz(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        XElement kok;
        try { kok = XElement.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        // Kök <deadlock> olabilir ya da sarmalayıcı içinde gelebilir (deadlock-list vb.).
        XElement? deadlock = kok.Name.LocalName == "deadlock"
            ? kok
            : kok.Descendants().FirstOrDefault(e => e.Name.LocalName == "deadlock");
        if (deadlock is null)
            return null;

        // Kurban(lar): victim özniteliği (eski biçim) ya da victim-list/victimProcess (yeni).
        var kurbanlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (deadlock.Attribute("victim")?.Value is { Length: > 0 } tekKurban)
            kurbanlar.Add(tekKurban);
        foreach (XElement v in deadlock.Descendants().Where(e => e.Name.LocalName == "victimProcess"))
        {
            if (v.Attribute("id")?.Value is { Length: > 0 } id)
                kurbanlar.Add(id);
        }

        var surecler = new List<DeadlockSureci>();
        foreach (XElement p in deadlock.Descendants().Where(e => e.Name.LocalName == "process"))
        {
            // resource-list altındaki owner/waiter da "process" DEĞİLDİR — yalnız process-list'in
            // (id özniteliği "process.." + inputbuf/spid taşıyan) gerçek süreç düğümleri alınır.
            if (p.Parent?.Name.LocalName != "process-list")
                continue;
            string? id = p.Attribute("id")?.Value;
            if (id is null)
                continue;
            surecler.Add(new DeadlockSureci(
                Id: id,
                Spid: int.TryParse(p.Attribute("spid")?.Value, out int spid) ? spid : 0,
                Uygulama: p.Attribute("clientapp")?.Value,
                Kullanici: p.Attribute("loginname")?.Value,
                YalitimDuzeyi: p.Attribute("isolationlevel")?.Value,
                Sorgu: p.Elements().FirstOrDefault(e => e.Name.LocalName == "inputbuf")?.Value?.Trim(),
                KurbanMi: kurbanlar.Contains(id)));
        }
        if (surecler.Count == 0)
            return null;

        var kaynaklar = new List<DeadlockKaynagi>();
        XElement? kaynakListesi = deadlock.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "resource-list");
        foreach (XElement r in kaynakListesi?.Elements() ?? [])
        {
            static IReadOnlyList<(string, string)> Taraf(XElement kaynak, string listeAdi)
                => [.. kaynak.Elements().Where(e => e.Name.LocalName == listeAdi)
                    .SelectMany(l => l.Elements())
                    .Select(o => (o.Attribute("id")?.Value ?? "", o.Attribute("mode")?.Value ?? ""))
                    .Where(t => t.Item1.Length > 0)];

            kaynaklar.Add(new DeadlockKaynagi(
                Ad: r.Attribute("objectname")?.Value
                    ?? r.Attribute("waitresource")?.Value ?? r.Name.LocalName,
                Tur: r.Name.LocalName,                       // keylock / pagelock / objectlock…
                Sahipler: Taraf(r, "owner-list"),
                Bekleyenler: Taraf(r, "waiter-list")));
        }

        return new DeadlockGrafigi(surecler, kaynaklar);
    }
}
