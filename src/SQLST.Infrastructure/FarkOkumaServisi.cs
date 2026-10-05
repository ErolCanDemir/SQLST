using System.Data.Common;
using SQLST.Contracts;

namespace SQLST.Infrastructure;

/// <summary>
/// BF-3 (2026-07-27): tam eşitleme farkı için HEDEF tablonun mevcut satırlarını yalnız KIYASLANAN
/// kolonlarla okur (anahtar + karşılaştırılan). Sonuç, kolon adı → değer sözlüğü listesidir; Excel
/// tarafıyla birlikte <see cref="Application.ExcelFarkKarsilastirici"/>'ye verilir. Salt okuma
/// (SELECT) — yazma Güvenli Yazma bandında, kullanıcının COMMIT'iyle olur.
///
/// Tüm tabloyu belleğe alır (fark için iki tarafın tamamı gerekir); bu yüzden UI, satır sayısını
/// kullanıcıya söyler ve çok büyük tabloda uyarır. Sanal üye: UI/servis testi sahteler.
/// </summary>
public class FarkOkumaServisi(ILehceSaglayici lehceler)
{
    public virtual async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> TabloyuOkuAsync(
        ConnectionProfile profil, string? veritabani, string sema, string tablo,
        IReadOnlyList<string> kolonlar, CancellationToken ct)
    {
        if (kolonlar.Count == 0)
            throw new ArgumentException("En az bir kolon gerekir.", nameof(kolonlar));

        ILehce lehce = lehceler.Getir(profil.Motor);
        string secim = string.Join(", ", kolonlar.Select(lehce.TirnaklaTanimlayici));
        string sql = $"SELECT {secim} FROM {lehce.TamAdYaz(sema, tablo)}";

        await using DbConnection baglanti = lehce.BaglantiOlustur(profil, veritabani, havuz: true);
        await baglanti.OpenAsync(ct);
        await using DbCommand komut = baglanti.CreateCommand();
        komut.CommandText = sql;
        lehce.KomutuAyarla(komut);

        await using DbDataReader okuyucu = await komut.ExecuteReaderAsync(ct);
        var satirlar = new List<IReadOnlyDictionary<string, object?>>();
        while (await okuyucu.ReadAsync(ct))
        {
            // SELECT kolon sırası = kolonlar sırası → i'nci değer kolonlar[i] adına düşer.
            var satir = new Dictionary<string, object?>(kolonlar.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < kolonlar.Count; i++)
            {
                object deger = okuyucu.GetValue(i);
                satir[kolonlar[i]] = deger is DBNull ? null : deger;
            }

            satirlar.Add(satir);
        }

        return satirlar;
    }
}
