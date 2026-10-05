using System.IO;
using Serilog;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App;

/// <summary>
/// 📦 Aktarım günlüğü + tam hata raporu (v22-S16 — kullanıcı: "paket aktarımında hataları
/// loglayalım; nereye loglandığını da kullanıcıya söyleyelim").
///
/// İş bölümü: EKRAN ilk <see cref="UiOrnekTavani"/> örneği gösterir (rapor kutusu şişmesin);
/// TAMAMI iki kalıcı yere gider — (a) uygulama günlüğü (Serilog; özet + ilk
/// <see cref="GunlukOrnekTavani"/> örnek), (b) kullanıcının "⬇ Hata raporunu kaydet" ile yazdığı
/// metin dosyası (servis tavanına kadar HEPSİ). Pencere I/O'yu yapar, buradaki her şey saf metin.
/// </summary>
public static class AktarimGunlugu
{
    /// <summary>Ekrandaki rapor kutusunda gösterilen en çok hata örneği.</summary>
    public const int UiOrnekTavani = 20;

    /// <summary>Günlük dosyasına tek kayıtta gömülen en çok örnek — günlüğü şişirmemek için;
    /// gerisi sayı olarak kayıtta, tamamı kullanıcının kaydettiği rapor dosyasında.</summary>
    private const int GunlukOrnekTavani = 200;

    /// <summary>Bugünün günlük dosyası (App açılışında kurulan Serilog kalıbı: sqlst-YYYYAAGG.log).</summary>
    public static string GunlukDosyasi
        => Path.Combine(UygulamaVeriYolu.GunlukKlasoru, $"sqlst-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Rapor kutusuna eklenen "nereye loglandı" satırı (kullanıcı isteği: açıkça söylensin).</summary>
    public static string GunlukNotu => $"🧾 Bu aktarımın kaydı günlüğe yazıldı: {GunlukDosyasi}";

    /// <summary>Aktarım sonucunu uygulama günlüğüne yazar: özet her zaman; hata/örnekler varsa uyarı.</summary>
    public static void Logla(string baslik, string kaynak, string hedef, AktarimSonucu s)
    {
        Log.Information(
            "📦 {Baslik} · {Kaynak} → {Hedef} · okunan {Okunan} · eklenen {Yazilan} · "
            + "güncellenen {Guncellenen} · atlanan {Atlanan} · süre {Sure:F1} sn · başarılı={Basarili}"
            + (s.IptalEdildi ? " · İPTAL EDİLDİ" : ""),
            baslik, kaynak, hedef, s.Okunan, s.Yazilan, s.Guncellenen, s.Atlanan,
            s.Sure.TotalSeconds, s.Basarili);
        if (s.Hata is not null)
            Log.Warning("📦 {Baslik} sonucu: {Hata}", baslik, s.Hata);
        if (s.HataOrnekleri.Count > 0)
        {
            var ilk = s.HataOrnekleri.Take(GunlukOrnekTavani);
            Log.Warning("📦 {Baslik} · atlanan satır ayrıntıları (günlükte ilk {N}/{Toplam}):\n{Satirlar}",
                baslik, Math.Min(GunlukOrnekTavani, s.HataOrnekleri.Count), s.HataOrnekleri.Count,
                string.Join("\n", ilk));
        }
    }

    /// <summary>
    /// "⬇ Hata raporunu kaydet" dosya içeriği: başlık + kaynak/hedef + özet + TÜM hata örnekleri
    /// (ekranın 20 sınırı burada YOKTUR). Servis tavanına takılmışsa (atlanan > örnek sayısı)
    /// bunu açıkça söyler — sessiz kırpma yok.
    /// </summary>
    public static string HataRaporuMetni(string baslik, string kaynak, string hedef, AktarimSonucu s)
    {
        var m = new System.Text.StringBuilder()
            .AppendLine($"SQLST — {baslik} hata raporu")
            .AppendLine($"Tarih: {DateTime.Now:dd.MM.yyyy HH:mm:ss}")
            .AppendLine($"Kaynak: {kaynak}")
            .AppendLine($"Hedef:  {hedef}")
            .AppendLine($"Okunan {s.Okunan:N0} · Eklenen {s.Yazilan:N0} · Güncellenen {s.Guncellenen:N0}"
                + $" · Atlanan {s.Atlanan:N0} · Süre {s.Sure.TotalSeconds:F1} sn")
            .AppendLine(s.Hata is not null ? $"Sonuç: {s.Hata}" : "Sonuç: tamamlandı")
            .AppendLine(new string('-', 60));
        foreach (string ornek in s.HataOrnekleri)
            m.AppendLine(ornek);
        if (s.Atlanan > s.HataOrnekleri.Count)
            m.AppendLine($"… ve {s.Atlanan - s.HataOrnekleri.Count:N0} satır daha atlandı "
                + $"(ayrıntı tavanı {s.HataOrnekleri.Count:N0} — mesajları toplanamadı).");
        return m.ToString().TrimEnd();
    }
}
