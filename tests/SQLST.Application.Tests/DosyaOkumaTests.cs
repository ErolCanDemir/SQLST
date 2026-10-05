using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.Application.Tests;

/// <summary>
/// v13-S1 — Excel/TXT Import dosya çekirdeği: SAF ayrıştırıcı + tip tahmini birimleri ve
/// GERÇEK dosyayla okuyucu entegrasyonu (Windows-1254 Türkçe TXT + elde üretilmiş minimal xlsx).
/// </summary>
public class DosyaOkumaTests
{
    private static readonly CultureInfo Tr = new("tr-TR");

    // ---- MetinTabloAyristirici (saf) ----

    [Fact]
    public void Ayristirici_tirnakli_alan_ayrac_ve_satir_sonunu_yutar()
    {
        const string icerik = "Ad;Adres;Puan\r\n" +
            "Ali;\"İstanbul; Kadıköy\";10\r\n" +
            "Ayşe;\"iki\r\nsatır\";20\r\n" +
            "\r\n" +                        // boş satır atlanır
            "Can;\"o \"\"takma\"\" adla\";30"; // son satır newline'sız + "" kaçışı

        var satirlar = MetinTabloAyristirici.Ayristir(new StringReader(icerik), ';').ToList();

        Assert.Equal(4, satirlar.Count); // başlık + 3 veri (boş satır yok)
        Assert.Equal(["Ad", "Adres", "Puan"], satirlar[0]);
        Assert.Equal("İstanbul; Kadıköy", satirlar[1][1]);   // tırnak içindeki ayraç bölmedi
        Assert.Equal("iki\r\nsatır", satirlar[2][1]);        // tırnak içindeki satır sonu alanda
        Assert.Equal("o \"takma\" adla", satirlar[3][1]);    // "" → tek tırnak
    }

    [Fact]
    public void Formul_oneki_gidis_donusu_onarilir() // inceleme kalanı 2026-08-03
    {
        // CsvYazici formül-benzeri hücreye ' öneki koyar; aynı dosya geri içe aktarılınca önek
        // Excel semantiğiyle SOYULUR — veri "=A1+B1" olarak döner, "'=A1+B1" değil.
        string yol = Path.Combine(Path.GetTempPath(), $"sqlst-onek-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(yol,
                "Ad;Formul;Sayi\r\nAli;'=A1+B1;-5\r\nVeli;'@SUM(x);duz metin\r\n",
                new UTF8Encoding(true));

            var satirlar = DosyaOkuyucu.MetinAkis(yol, ';', Encoding.UTF8, ilkSatirBaslik: true).ToList();

            Assert.Equal("=A1+B1", satirlar[0][1]);   // önek soyuldu
            Assert.Equal("-5", satirlar[0][2]);        // sayı zaten öneksiz — dokunulmadı
            Assert.Equal("@SUM(x)", satirlar[1][1]);
            Assert.Equal("duz metin", satirlar[1][2]);

            DosyaOnizleme onizleme = DosyaOkuyucu.MetinOnizle(yol, ';', Encoding.UTF8, true, Tr);
            Assert.Equal("=A1+B1", onizleme.Satirlar[0][1]); // önizleme de aynı onarımı yapar
        }
        finally
        {
            if (File.Exists(yol)) File.Delete(yol);
        }
    }

    [Fact]
    public void Ayristirici_lf_ve_tab_ayracli_dosya()
    {
        var satirlar = MetinTabloAyristirici
            .Ayristir(new StringReader("a\tb\n1\t2\n;\t"), '\t').ToList();

        Assert.Equal(3, satirlar.Count);
        Assert.Equal(["a", "b"], satirlar[0]);
        Assert.Equal(["1", "2"], satirlar[1]);
        Assert.Equal([";", ""], satirlar[2]); // farklı ayraç veri sayılır; sondaki tab boş alan
    }

    [Fact]
    public void Ayristirici_ozel_satir_ayraci_crlf_ye_ek_calisir()
    {
        // Kullanıcı kuralı 2026-07-26: TXT'de satır ayracı da seçilir — tek fiziksel satırlık
        // "kayıt1|kayıt2|…" ihraç dosyası; CRLF görülürse o da satır sonudur (EK sözleşmesi).
        var satirlar = MetinTabloAyristirici
            .Ayristir(new StringReader("Ali;10|Ayşe;20|Can;30\r\nEce;40"), ';', '|').ToList();

        Assert.Equal(4, satirlar.Count);
        Assert.Equal(["Ali", "10"], satirlar[0]);
        Assert.Equal(["Can", "30"], satirlar[2]);
        Assert.Equal(["Ece", "40"], satirlar[3]);
    }

    [Fact]
    public void Csv_ayrac_algilama_tr_noktali_virgul_ve_standart_virgul()
    {
        // Kullanıcı kuralı 2026-07-26: "CSV için ayraca gerek yok" — otomatik algı.
        string trYol = Path.Combine(Path.GetTempPath(), $"sqlst-tr-{Guid.NewGuid():N}.csv");
        string stdYol = Path.Combine(Path.GetTempPath(), $"sqlst-std-{Guid.NewGuid():N}.csv");
        File.WriteAllText(trYol, "Ad;Tutar\r\n\"Yılmaz; Ali\";1.250,75\r\n", Encoding.UTF8);
        File.WriteAllText(stdYol, "name,amount\r\n\"Smith, John\",99.5\r\n", Encoding.UTF8);

        try
        {
            // Tırnak İÇİNDEKİ ayraç sayılmaz — "Yılmaz; Ali" TR dosyayı virgüle kaydırmaz.
            Assert.Equal(';', DosyaOkuyucu.AyracAlgila(trYol, Encoding.UTF8));
            Assert.Equal(',', DosyaOkuyucu.AyracAlgila(stdYol, Encoding.UTF8));
        }
        finally
        {
            File.Delete(trYol);
            File.Delete(stdYol);
        }
    }

    // ---- KolonTipiTahminci (saf) ----

    [Fact]
    public void Tip_tahmini_tr_kulturuyle_metin_degerlerden()
    {
        IReadOnlyList<string> adlar = ["Tutar", "Tarih", "Adet", "Not", "Bos"];
        IReadOnlyList<object?[]> satirlar =
        [
            ["1.250,75", "24.07.2026", "42", "merhaba", ""],
            ["3,10", "01.01.2025", "7", "9 elma", null],
        ];

        IReadOnlyList<DosyaKolonu> k = KolonTipiTahminci.Tahmin(adlar, satirlar, Tr);

        Assert.Equal(DosyaTipi.Ondalik, k[0].Tip);  // TR ondalık virgül + binlik nokta
        Assert.Equal(DosyaTipi.Tarih, k[1].Tip);
        Assert.Equal(DosyaTipi.TamSayi, k[2].Tip);
        Assert.Equal(DosyaTipi.Metin, k[3].Tip);    // "9 elma" sayı değil
        Assert.Equal(DosyaTipi.Metin, k[4].Tip);    // hep boş → Metin + BosVar
        Assert.True(k[4].BosVar);
        Assert.False(k[2].BosVar);
        Assert.Equal("merhaba".Length, k[3].EnUzunMetin);
    }

    [Fact]
    public void Tip_tahmini_excel_tipli_degerler_ve_karisim_kurallari()
    {
        IReadOnlyList<string> adlar = ["A", "B", "C", "D"];
        IReadOnlyList<object?[]> satirlar =
        [
            [5.0, 5.5, new DateTime(2026, 7, 26), true],
            [7.0, 2, "x", false],
        ];

        IReadOnlyList<DosyaKolonu> k = KolonTipiTahminci.Tahmin(adlar, satirlar, Tr);

        Assert.Equal(DosyaTipi.TamSayi, k[0].Tip); // Excel 5.0/7.0 → tam sayı
        Assert.Equal(DosyaTipi.Ondalik, k[1].Tip); // 5.5 + 2 → sayı karışımı ondalığa genişler
        Assert.Equal(DosyaTipi.Metin, k[2].Tip);   // Tarih + metin → Metin'e düşer
        Assert.Equal(DosyaTipi.Bool, k[3].Tip);
    }

    // ---- DosyaOkuyucu (gerçek dosya) ----

    [Fact]
    public void Txt_windows1254_okunur_basliklar_normallesir_kesme_isler()
    {
        string yol = Path.Combine(Path.GetTempPath(), $"sqlst-1254-{Guid.NewGuid():N}.txt");
        var icerik = new StringBuilder("Ad;;Ad;Tutar\r\n"); // boş başlık + yinelenen ad
        for (int i = 1; i <= DosyaOkuyucu.EnCokOnizlemeSatiri + 5; i++)
            icerik.Append($"ığüşöçİĞÜŞÖÇ{i};x;y;{i.ToString("N2", Tr)}\r\n");
        File.WriteAllText(yol, icerik.ToString(), Encoding.GetEncoding(1254));

        try
        {
            DosyaOnizleme on = DosyaOkuyucu.MetinOnizle(
                yol, ';', DosyaOkuyucu.KodlamaCoz("Windows-1254 (Türkçe ANSI)"),
                ilkSatirBaslik: true, Tr);

            Assert.Equal(["Ad", "Kolon2", "Ad_2", "Tutar"], on.Kolonlar.Select(k => k.Ad));
            Assert.Equal("ığüşöçİĞÜŞÖÇ1", (string?)on.Satirlar[0][0]); // 1254 doğru çözüldü
            Assert.Equal(DosyaTipi.Ondalik, on.Kolonlar[3].Tip);       // "1,00" TR ondalık
            Assert.Equal(DosyaOkuyucu.EnCokOnizlemeSatiri, on.Satirlar.Count);
            Assert.True(on.Kesildi);

            // Akış tamamını verir (başlık hariç) — aktarım (S3) bu yoldan gider.
            Assert.Equal(DosyaOkuyucu.EnCokOnizlemeSatiri + 5,
                DosyaOkuyucu.MetinAkis(yol, ';', Encoding.GetEncoding(1254), ilkSatirBaslik: true).Count());
        }
        finally
        {
            File.Delete(yol);
        }
    }

    [Fact]
    public void Xlsx_okunur_tipler_hucre_tipinden_gelir()
    {
        string yol = Path.Combine(Path.GetTempPath(), $"sqlst-{Guid.NewGuid():N}.xlsx");
        MinimalXlsxYaz(yol, "Veriler",
        [
            ["Ad", "Puan", "Oran"],
            ["Ali", 5.0, 2.5],
            ["Ayşe", 7.0, 3.25],
        ]);

        try
        {
            Assert.Equal(["Veriler"], DosyaOkuyucu.ExcelSayfalari(yol));

            DosyaOnizleme on = DosyaOkuyucu.ExcelOnizle(yol, "Veriler", ilkSatirBaslik: true, Tr);

            Assert.Equal(["Ad", "Puan", "Oran"], on.Kolonlar.Select(k => k.Ad));
            Assert.Equal(2, on.Satirlar.Count);
            Assert.Equal("Ayşe", (string?)on.Satirlar[1][0]);
            Assert.Equal(DosyaTipi.TamSayi, on.Kolonlar[1].Tip); // 5.0/7.0 → tam sayı
            Assert.Equal(DosyaTipi.Ondalik, on.Kolonlar[2].Tip);
            Assert.False(on.Kesildi);
        }
        finally
        {
            File.Delete(yol);
        }
    }

    /// <summary>Test yardımcısı: yazma kütüphanesi eklemeden ELDE minimal .xlsx üretir
    /// (xlsx = zip; inline string + sayı hücreleri ExcelDataReader'a yeter).</summary>
    private static void MinimalXlsxYaz(string yol, string sayfaAdi, IReadOnlyList<object[]> satirlar)
    {
        using FileStream fs = File.Create(yol);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Yaz(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Yaz(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Yaz(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="{sayfaAdi}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Yaz(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        var govde = new StringBuilder();
        foreach (object[] satir in satirlar)
        {
            govde.Append("<row>");
            foreach (object deger in satir)
            {
                govde.Append(deger is string s
                    ? $"<c t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(s)}</t></is></c>"
                    : $"<c><v>{Convert.ToString(deger, CultureInfo.InvariantCulture)}</v></c>");
            }

            govde.Append("</row>");
        }

        Yaz(zip, "xl/worksheets/sheet1.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>{govde}</sheetData>
            </worksheet>
            """);
    }

    private static void Yaz(ZipArchive zip, string ad, string icerik)
    {
        using Stream s = zip.CreateEntry(ad).Open();
        byte[] b = Encoding.UTF8.GetBytes(icerik);
        s.Write(b, 0, b.Length);
    }
}
