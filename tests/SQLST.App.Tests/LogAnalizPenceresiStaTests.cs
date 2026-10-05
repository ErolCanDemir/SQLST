using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SQLST.App.Views;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// Log Analizi penceresi UI testi (kullanıcı kararları 2026-07-24/25). GERÇEK WPF pencereyi TEK
/// paylaşımlı STA dispatcher'da açıp sahte köprülerle sürer. Doğrular:
///  • SQL: DB → tablo → kolon akışı; seçilen DB köprülere geçer; seçim GÖRÜNÜR (non-editable combo,
///    özel şablonda PART_EditableTextBox yok → editable'da seçilen görünmüyordu); analiz SON 24 SAAT
///    sorgusu (WHERE + zaman kolonu) üretir ve Grid dolar.
///  • Mongo (kullanıcı 2026-07-25 "Mongo'da da olmalı"): metinler koleksiyon/alan olur; analiz SQL
///    değil find/aggregate JSON üretir ve koleksiyon adı ÇIPLAK geçer (TamAd "db.koleksiyon" değil).
///
/// NEDEN TEK STA THREAD: WPF Application AppDomain'de tektir ve bir dispatcher thread'ine bağlıdır;
/// her testi ayrı thread'de koşmak Application'ı ölü thread'e bağlar. Paylaşımlı dispatcher bunu çözer.
/// </summary>
public class LogAnalizPenceresiStaTests
{
    // Paylaşımlı STA altyapısı StaOrtak'a taşındı (2026-07-25): xUnit sınıf paralelliğinde
    // iki STA sınıfı aynı anda Application kurmaya çalışıp çakışıyordu.
    private static Dispatcher Sta() => StaOrtak.Sta();

    private static void Birlestir(string dosya) => StaOrtak.Birlestir(dosya);

    private sealed record Sonuc(
        string? DbSecili, int TabloItems, string? TabloSecili,
        int KolonItems, string? KolonSecili, int GridSayisi, string? CalisanSql, string? CalisanDb);

    [Fact]
    public void Sql_db_tablo_kolon_gorunur_ve_analiz_son24saat_grid_doldurur()
    {
        Sonuc sonuc = Sta().Invoke(Kosu);

        Assert.Equal("LogDb", sonuc.DbSecili);
        Assert.Equal("LogDb", sonuc.CalisanDb);
        Assert.True(sonuc.TabloItems > 0, "Tablo listesi boş.");
        Assert.Equal("dbo.ExceptionLog", sonuc.TabloSecili);
        Assert.True(sonuc.KolonItems > 0, "Kolon listesi boş — tablo seçimi kolonları doldurmadı.");
        Assert.Equal("Mesaj", sonuc.KolonSecili);
        Assert.NotNull(sonuc.CalisanSql);
        Assert.Contains("WHERE", sonuc.CalisanSql!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zaman", sonuc.CalisanSql!);
        Assert.True(sonuc.GridSayisi > 0, "Grid boş kaldı — analiz sonucu ekrana bağlanmadı.");
    }

    private static Sonuc Kosu()
    {
        Birlestir("PaletAcik.xaml");
        Birlestir("Tema.xaml");

        var logKolonlar = new List<SemaKolonu>
        {
            new("Id", "int", false, true),
            new("Mesaj", "nvarchar(400)", true, false),
            new("Zaman", "datetime2", true, false),
        };
        var tablolar = new List<SemaNesnesi>
        {
            new("LogDb", "dbo", "ExceptionLog", SemaNesneTuru.Tablo, logKolonlar, []),
            new("LogDb", "dbo", "Musteri", SemaNesneTuru.Tablo,
                [new("Id", "int", false, true), new("Ad", "nvarchar(100)", true, false)], []),
        };

        string? calisanDb = null;
        string? calisanSql = null;

        Func<string, Task<IReadOnlyList<SemaNesnesi>>> tablolariGetir =
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(tablolar);
        // v20-S6: _calistir artık (db, sql, ct). İlk çağrıyı (KEŞİF) yakala — gerçek-sayım geçişi son SQL'i ezmesin.
        Func<string, string, CancellationToken, Task<QueryResult>> calistir = (db, sql, _) =>
        {
            calisanDb ??= db;
            calisanSql ??= sql;
            var satirlar = new List<object?[]>
            {
                new object?[] { "Tablo A bulunamadi" }, new object?[] { "Tablo B bulunamadi" },
                new object?[] { "Tablo C bulunamadi" }, new object?[] { "Zaman asimi" },
                new object?[] { "Zaman asimi" },
            };
            var rs = new ResultSetData { Kolonlar = [new KolonBilgisi("Mesaj", "nvarchar", null)], Satirlar = satirlar };
            return Task.FromResult(new QueryResult { Basarili = true, ToplamSatir = 5, ResultSetler = [rs] });
        };

        var w = new LogAnalizPenceresi(
            MotorTuru.Mssql, new[] { "AppDb", "LogDb" }, "LogDb", tablolariGetir, calistir)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        Pump(TimeSpan.FromMilliseconds(700)); // Loaded → ExceptionLog ön seçilir

        var dbCb = (ComboBox)w.FindName("VeritabaniKutusu");
        var tabloCb = (ComboBox)w.FindName("TabloKutusu");
        var kolonCb = (ComboBox)w.FindName("KolonKutusu");
        var analiz = (Button)w.FindName("AnalizDugmesi");
        var grid = (DataGrid)w.FindName("Grid");

        string? dbSecili = dbCb.SelectedItem as string;
        int tabloItems = tabloCb.Items.Count;
        string? tabloSecili = tabloCb.SelectedItem as string;
        int kolonItems = kolonCb.Items.Count;
        string? kolonSecili = kolonCb.SelectedItem as string;

        analiz.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(700));

        int gridSayisi = grid.ItemsSource is System.Collections.IEnumerable en ? en.Cast<object>().Count() : 0;
        w.Close();
        return new Sonuc(dbSecili, tabloItems, tabloSecili, kolonItems, kolonSecili, gridSayisi, calisanSql, calisanDb);
    }

    [Fact]
    public void Tum_zamanlar_paneli_tarih_araligiyla_sorgu_uretir_bitis_dahil()
    {
        // #9 (2026-07-25): ikinci panel — tarih aralığı seçilir, bitiş günü DAHİL (+1 gün üst sınır).
        (string? sql, int grid) = Sta().Invoke(() =>
        {
            Birlestir("PaletAcik.xaml");
            Birlestir("Tema.xaml");

            var kolonlar = new List<SemaKolonu>
            {
                new("Id", "int", false, true),
                new("Mesaj", "nvarchar(400)", true, false),
                new("Zaman", "datetime2", true, false),
            };
            var tablolar = new List<SemaNesnesi>
            {
                new("LogDb", "dbo", "ExceptionLog", SemaNesneTuru.Tablo, kolonlar, []),
            };
            string? calisan = null;
            Func<string, Task<IReadOnlyList<SemaNesnesi>>> getir =
                _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(tablolar);
            Func<string, string, CancellationToken, Task<QueryResult>> calistir = (_, s, _) =>
            {
                calisan ??= s; // ilk (keşif) çağrı
                var rs = new ResultSetData
                {
                    Kolonlar = [new KolonBilgisi("Mesaj", "nvarchar", null)],
                    Satirlar = [new object?[] { "hata X" }, new object?[] { "hata X" }],
                };
                return Task.FromResult(new QueryResult { Basarili = true, ToplamSatir = 2, ResultSetler = [rs] });
            };

            var w = new LogAnalizPenceresi(MotorTuru.Mssql, new[] { "LogDb" }, "LogDb", getir, calistir)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            Pump(TimeSpan.FromMilliseconds(700));

            ((DatePicker)w.FindName("BaslangicTarihi")).SelectedDate = new DateTime(2026, 7, 1);
            ((DatePicker)w.FindName("BitisTarihi")).SelectedDate = new DateTime(2026, 7, 20);
            var dugme = (Button)w.FindName("AralikAnalizDugmesi");
            dugme.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(700));

            var grid = (DataGrid)w.FindName("Grid");
            int sayi = grid.ItemsSource is System.Collections.IEnumerable en ? en.Cast<object>().Count() : 0;
            w.Close();
            return (calisan, sayi);
        });

        Assert.NotNull(sql);
        Assert.Contains("Zaman >= '2026-07-01T00:00:00'", sql!);
        Assert.Contains("Zaman < '2026-07-21T00:00:00'", sql!); // 20'si DAHİL → üst sınır 21 (yarı açık)
        Assert.True(grid > 0, "Grid boş kaldı.");
    }

    private sealed record MongoSonuc(
        string? TabloEtiket, string? KolonEtiket, string? TabloSecili, string? KolonSecili,
        string? CalisanSql, int GridSayisi);

    [Fact]
    public void Mongo_koleksiyon_alan_gorunur_ve_analiz_bare_koleksiyonla_aggregate_uretir()
    {
        MongoSonuc sonuc = Sta().Invoke(MongoKosu);

        Assert.Equal("Koleksiyon:", sonuc.TabloEtiket);
        Assert.Equal("Mesaj alanı:", sonuc.KolonEtiket);
        Assert.Equal("LogDb.ExceptionLog", sonuc.TabloSecili); // ekranda TamAd; seçim GÖRÜNÜR
        Assert.Equal("Mesaj", sonuc.KolonSecili);

        Assert.NotNull(sonuc.CalisanSql);
        using JsonDocument doc = JsonDocument.Parse(sonuc.CalisanSql!);
        // Koleksiyon adı ÇIPLAK (TamAd DEĞİL) + düz tarih literali (v22-S4 m.4: $expr+$$NOW her
        // sunucu sürümünde index kullanamıyordu — 24 saat süzgeci artık {alan:{$gte:{$date}}}).
        Assert.Equal("ExceptionLog", doc.RootElement.GetProperty("aggregate").GetString());
        Assert.Contains("\"$gte\":{\"$date\"", sonuc.CalisanSql!);
        Assert.DoesNotContain("$$NOW", sonuc.CalisanSql!);
        Assert.True(sonuc.GridSayisi > 0, "Grid boş kaldı.");
    }

    private static MongoSonuc MongoKosu()
    {
        Birlestir("PaletAcik.xaml");
        Birlestir("Tema.xaml");

        var alanlar = new List<SemaKolonu>
        {
            new("_id", "objectId", false, true),
            new("Mesaj", "string", true, false),
            new("Zaman", "date", true, false),
        };
        // Mongo: Sema=veritabani, Ad=koleksiyon → TamAd="LogDb.ExceptionLog"
        var koleksiyonlar = new List<SemaNesnesi>
        {
            new("LogDb", "LogDb", "ExceptionLog", SemaNesneTuru.Koleksiyon, alanlar, []),
        };

        string? calisanSql = null;
        Func<string, Task<IReadOnlyList<SemaNesnesi>>> tablolariGetir =
            _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(koleksiyonlar);
        Func<string, string, CancellationToken, Task<QueryResult>> calistir = (_, sql, _) =>
        {
            calisanSql ??= sql; // ilk (keşif) çağrı
            var rows = new List<object?[]> { new object?[] { "hata A" }, new object?[] { "hata A" }, new object?[] { "hata B" } };
            var rs = new ResultSetData { Kolonlar = [new KolonBilgisi("Mesaj", "string", null)], Satirlar = rows };
            return Task.FromResult(new QueryResult { Basarili = true, ToplamSatir = 3, ResultSetler = [rs] });
        };

        var w = new LogAnalizPenceresi(MotorTuru.Mongo, new[] { "LogDb" }, "LogDb", tablolariGetir, calistir)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false,
        };
        w.Show();
        Pump(TimeSpan.FromMilliseconds(700)); // Loaded → ExceptionLog ön seçilir (ad "exception"/"log" içerir)

        var tabloEt = (TextBlock)w.FindName("TabloEtiketi");
        var kolonEt = (TextBlock)w.FindName("KolonEtiketi");
        var tabloCb = (ComboBox)w.FindName("TabloKutusu");
        var kolonCb = (ComboBox)w.FindName("KolonKutusu");
        var analiz = (Button)w.FindName("AnalizDugmesi");
        var grid = (DataGrid)w.FindName("Grid");

        string? tabloSecili = tabloCb.SelectedItem as string;
        string? kolonSecili = kolonCb.SelectedItem as string;

        analiz.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(700));

        int gridSayisi = grid.ItemsSource is System.Collections.IEnumerable en ? en.Cast<object>().Count() : 0;
        w.Close();
        return new MongoSonuc(tabloEt.Text, kolonEt.Text, tabloSecili, kolonSecili, calisanSql, gridSayisi);
    }

    [Fact]
    public void Mongo_alansiz_koleksiyonda_mesaj_alani_canli_kesifle_dolar() // v19-S8
    {
        // Kullanıcı bulgusu 2026-08-03: "Mongo'da mesaj alanı yüklenmiyor". Kök: ağacın hızlı
        // faz-1 önbelleği ALANSIZ — koleksiyon seçilince kutu boş kalıyordu. Artık seçilen
        // koleksiyondan find(limit 50) ile alanlar ANINDA keşfedilir.
        (string? kolonSecili, int kolonItems, string? kesifSorgusu) = Sta().Invoke(() =>
        {
            Birlestir("PaletAcik.xaml");
            Birlestir("Tema.xaml");

            // Faz-1 önbelleği senaryosu: koleksiyonun ALAN LİSTESİ BOŞ.
            var koleksiyonlar = new List<SemaNesnesi>
            {
                new("LogDb", "LogDb", "ExceptionLog", SemaNesneTuru.Koleksiyon, [], []),
            };

            string? kesif = null;
            Func<string, Task<IReadOnlyList<SemaNesnesi>>> getir =
                _ => Task.FromResult<IReadOnlyList<SemaNesnesi>>(koleksiyonlar);
            Func<string, string, CancellationToken, Task<QueryResult>> calistir = (_, sql, _) =>
            {
                kesif ??= sql; // find(limit 50) keşif sorgusu
                var rs = new ResultSetData
                {
                    // MongoSonucEsleyici gibi: belgelerin alan birleşimi kolon envanteridir
                    Kolonlar = [new KolonBilgisi("_id", "objectId", null),
                                new KolonBilgisi("Mesaj", "string", null),
                                new KolonBilgisi("Zaman", "date", null)],
                    Satirlar = [],
                };
                return Task.FromResult(new QueryResult { Basarili = true, ResultSetler = [rs] });
            };

            var w = new LogAnalizPenceresi(MotorTuru.Mongo, new[] { "LogDb" }, "LogDb", getir, calistir)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            w.Show();
            Pump(TimeSpan.FromMilliseconds(900)); // Loaded → koleksiyon ön seçilir → canlı keşif koşar

            var kolonCb = (ComboBox)w.FindName("KolonKutusu");
            (string? Secili, int Items, string? Sorgu) r = (kolonCb.SelectedItem as string, kolonCb.Items.Count, kesif);
            w.Close();
            return r;
        });

        Assert.NotNull(kesifSorgusu);
        using JsonDocument doc = JsonDocument.Parse(kesifSorgusu!);
        Assert.Equal("ExceptionLog", doc.RootElement.GetProperty("find").GetString()); // çıplak ad
        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());             // hafif keşif
        Assert.Equal(3, kolonItems);
        Assert.Equal("Mesaj", kolonSecili); // tahmin metin ("string") alanında çalıştı
    }

    private static void Pump(TimeSpan sure) => StaOrtak.Pump(sure);
}
