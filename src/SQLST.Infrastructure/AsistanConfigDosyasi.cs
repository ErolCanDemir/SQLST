using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// Asistan yapılandırmasının TEK kaynağı (kullanıcı kararı 2026-07-26: "kimse değiştiremesin,
/// bir butonda olmasın"): ⚙ ayar penceresi kaldırıldı; sağlayıcı/model/anahtar yalnız
/// <c>%APPDATA%\SQLST\asistan.config</c> dosyasından okunur — uygulama içinden görüntülenemez
/// ve değiştirilemez.
///
/// Dosyaya düz metin <c>"anahtar"</c> yazılabilir: İLK okumada DPAPI ile şifrelenip
/// <c>"anahtarSifreli"</c> olarak dosya MÜHÜRLENİR — düz metin diskte kalmaz ve şifreli değer
/// yalnız bu Windows kullanıcısında çözülür (dosyayı kopyalayan anahtarı kullanamaz).
/// </summary>
public sealed class AsistanConfigDosyasi(string dosyaYolu, ISecretProtector protector)
{
    public AsistanConfigDosyasi(ISecretProtector protector)
        : this(Path.Combine(UygulamaVeriYolu.Klasor, "asistan.config"), protector)
    {
    }

    /// <summary>Config dosyasının çözülmüş TAM YOLU (tanı mesajı için — hangi profilde arandığını gösterir).</summary>
    public string DosyaYolu => dosyaYolu;

    /// <summary>Dosya diskte var mı (tanı).</summary>
    public bool DosyaVar => File.Exists(dosyaYolu);

    private sealed record Icerik(
        string? Saglayici, string? Model, string? TabanAdres, string? Anahtar, string? AnahtarSifreli);

    private static readonly JsonSerializerOptions JsonAyar = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Dosya yoksa ya da okunamıyorsa null — çağıran eski depodan göçü dener.</summary>
    public AsistanAyarlari? Oku()
    {
        // TANI (kullanıcı bulgusu 2026-07-27: kurulumda "yapılandırılmamış" — config okunamıyor).
        // Hangi YOLA bakıldığı ve dosyanın VAR olup olmadığı log'a yazılır (SIR yazılmaz): kurulum
        // farklı bir %APPDATA%'ya (ör. yönetici bağlamı) bakıyorsa buradan anlaşılır.
        bool varMi = File.Exists(dosyaYolu);
        Serilog.Log.Information("Asistan config okunuyor: yol={Yol} dosyaVar={VarMi}", dosyaYolu, varMi);

        Icerik? icerik;
        try
        {
            if (!varMi)
                return null;
            icerik = JsonSerializer.Deserialize<Icerik>(File.ReadAllText(dosyaYolu), JsonAyar);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Asistan config okunamadı: yol={Yol}", dosyaYolu);
            return null; // bozuk/kilitli dosya asistanı değil yalnız yapılandırmayı düşürür
        }

        if (icerik is null)
            return null;

        string? anahtarSifreli = string.IsNullOrWhiteSpace(icerik.AnahtarSifreli) ? null : icerik.AnahtarSifreli;

        // ⛔→🏠 GEMİNİ GÖÇÜ (v22-S6). Sahadaki config dosyalarının çoğunda "saglayici":"Gemini"
        // yazıyor; Gemini enum'dan silindiği için TryParse artık BAŞARISIZ olur. Sessizce
        // varsayılana düşmek YETMEZ: dosyadaki "gemini-flash-latest" model adı da taşınır ve
        // Ollama'ya gönderilip "model bulunamadı" hatası verirdi. Bu yüzden tanınmayan sağlayıcı
        // görülünce MODEL/TABAN da yok sayılıp Yerel varsayılanları kurulur.
        bool tanindi = Enum.TryParse(icerik.Saglayici, ignoreCase: true, out AsistanSaglayici s);
        AsistanSaglayici saglayici = tanindi ? s : AsistanSaglayici.Yerel;
        if (!tanindi && !string.IsNullOrWhiteSpace(icerik.Saglayici))
            Serilog.Log.Information(
                "Asistan config: tanınmayan sağlayıcı '{Eski}' → 🏠 Yerel'e göçürüldü (Gemini v22-S6'da kaldırıldı)",
                icerik.Saglayici);

        // Yerel (v21-S1): minimal {"saglayici":"Yerel"} config yeter — model/taban boşsa varsayılanlar
        // doldurulur (qwen2.5-coder:7b · 127.0.0.1:11434); anahtar/internet gerekmez.
        var ayarlar = new AsistanAyarlari(
            saglayici,
            !tanindi || string.IsNullOrWhiteSpace(icerik.Model)
                ? AsistanYonlendirici.YerelVarsayilanModel : icerik.Model.Trim(),
            !tanindi || string.IsNullOrWhiteSpace(icerik.TabanAdres)
                ? AsistanYonlendirici.YerelVarsayilanTaban : icerik.TabanAdres.Trim(),
            anahtarSifreli); // anahtar KORUNUR — kullanıcının sırrını biz silmeyiz, yalnız kullanmayız

        // Düz metin anahtar bırakılmışsa: şifrele ve dosyayı MÜHÜRLE (düz metin diskte kalmasın).
        if (!string.IsNullOrWhiteSpace(icerik.Anahtar))
        {
            ayarlar = ayarlar with { AnahtarSifreli = protector.Sifrele(icerik.Anahtar.Trim()) };
            Yaz(ayarlar);
        }
        else if (!tanindi)
        {
            Yaz(ayarlar); // göçü diske de yansıt: dosya kendini onarsın, her açılışta uyarı yazmasın
        }

        return ayarlar;
    }

    /// <summary>Şifreli haliyle yazar — eski SQLite ayarının tek seferlik göçü de buradan geçer.</summary>
    public void Yaz(AsistanAyarlari ayarlar)
        => File.WriteAllText(dosyaYolu, JsonSerializer.Serialize(new Icerik(
            ayarlar.Saglayici.ToString(), ayarlar.Model, ayarlar.TabanAdres,
            Anahtar: null, ayarlar.AnahtarSifreli), JsonAyar));
}
