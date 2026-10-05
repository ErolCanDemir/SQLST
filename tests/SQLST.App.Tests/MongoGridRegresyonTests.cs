using System.Collections.ObjectModel;
using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;
using SQLST.Infrastructure;

namespace SQLST.App.Tests;

/// <summary>
/// v22-S4 saha turu-4 m.3 — kullanıcı: "Mongo select'imiz bozulmuş… sadece mesajda 200 belge
/// yazıyor, grid dolmuyor."
///
/// REGRESYON KAYNAĞI v22-S3 (çökme üçlüsü düzeltmesi): sonuç boru hattı akışlı alıcıya
/// (<c>ISonucAlici</c>/<c>GridTabloAlicisi</c>) taşındı ve görünüm sarmalayıcıları ARTIK alıcının
/// tablolarından kuruluyordu. Ama alıcıyı YALNIZ SQL yolu (<c>SonucOkuyucu</c>) besler —
/// <c>MongoExecutor</c> sonucu <c>QueryResult.ResultSetler</c>'de döndürür. Sonuç: Mongo'da alıcı
/// boş → grid boş, oysa mesaj ("200 belge") ve satır sayısı doğru. Ağaçtan "İlk 200 satır" da aynı
/// yoldan geçtiği için o da çalışmıyor görünüyordu (m.2).
///
/// Düzeltme motor-bağımsızdır: alıcı boşsa sonucun kendi kümelerinden kurulur. Bu testler
/// alıcıyı BESLEMEYEN bir yürütücü (SahteExecutor — Mongo'nun aynısı) ile o sözleşmeyi sabitler.
/// </summary>
public class MongoGridRegresyonTests
{
    /// <summary>
    /// Alıcıyı BESLEMEYEN oturum — MongoExecutor'ın davranışının aynısı: sonucu
    /// <c>QueryResult.ResultSetler</c>'de döndürür, <c>opts.SonucAlici</c>'ya hiç dokunmaz.
    /// </summary>
    private sealed class AliciyiBeslemeyenOturum(ConnectionProfile profil, QueryResult sonuc) : IDbOturum
    {
        public ConnectionProfile Profil => profil;

        public Task<QueryResult> CalistirAsync(string sql, ExecuteOptions opts, CancellationToken ct)
            => Task.FromResult(sonuc);

        public Task<IslemDurumu> IslemDurumuAsync(CancellationToken ct = default)
            => Task.FromResult(IslemDurumu.Yok);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SahteFabrika(QueryResult sonuc) : IOturumFabrikasi
    {
        public IDbOturum Olustur(ConnectionProfile profil) => new AliciyiBeslemeyenOturum(profil, sonuc);
    }

    private static SorguSekmesiViewModel Kur(ConnectionProfile profil, QueryResult sonuc)
    {
        var saglayici = new LehceSaglayici(new DpapiSecretProtector());
        return new SorguSekmesiViewModel(
            new QueryService(),
            new SahteFabrika(sonuc), saglayici,
            () => profil,
            kirliOkumaGetir: () => false, guvenliYazmaGetir: () => false, rollbackSnGetir: () => 300,
            new ObservableCollection<string>(["MersisServices"]), "test.json");
    }

    private static QueryResult MongoSonucu(int satir)
    {
        var set = new ResultSetData
        {
            Kolonlar =
            [
                new KolonBilgisi("_id", "objectId", typeof(string)),
                new KolonBilgisi("Mesaj", "string", typeof(string)),
                new KolonBilgisi("SureMs", "int32", typeof(int)),
            ],
            Satirlar = [.. Enumerable.Range(0, satir)
                .Select(i => new object?[] { $"66f0a{i:D3}", $"istek {i}", i * 3 })],
        };
        return new QueryResult
        {
            Basarili = true,
            ResultSetler = [set],
            Mesajlar = [$"({satir} belge)"],
            ToplamSatir = satir,
        };
    }

    [Fact]
    public async Task Aliciyi_beslemeyen_yurutucude_GRID_DOLAR()
    {
        SorguSekmesiViewModel sekme = Kur(Profiller.Yap(MotorTuru.Mongo), MongoSonucu(200));
        sekme.Belge.Text = """{ "find": "MersisServices.QueryLog", "limit": 200 }""";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        await sekme.CalistirAsync();

        // ESKİDEN: SonucSetleri BOŞ — mesaj "(200 belge)" ama gridde tek satır yok.
        Assert.Single(sekme.SonucSetleri);
        Assert.Equal(200, sekme.SonucSetleri[0].Set.SatirSayisi);
        Assert.Equal(200, sekme.SonucSetleri[0].Tablo.Rows.Count);
        Assert.Equal(["_id", "Mesaj", "SureMs"],
            sekme.SonucSetleri[0].Tablo.Columns.Cast<System.Data.DataColumn>().Select(k => k.ColumnName));
        Assert.Equal("istek 7", sekme.SonucSetleri[0].Tablo.Rows[7]["Mesaj"]);
    }

    /// <summary>Ağaçtan "İlk 200 satır" da aynı boru hattından geçer — m.2'nin gerçek nedeni buydu.</summary>
    [Fact]
    public async Task Ilk_n_satir_sorgusu_da_gride_duser()
    {
        SorguSekmesiViewModel sekme = Kur(Profiller.Yap(MotorTuru.Mongo), MongoSonucu(200));
        sekme.Belge.Text = """{ "find": "Talep", "limit": 200 }""";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        await sekme.CalistirAsync();

        Assert.NotEmpty(sekme.SonucSetleri);
        Assert.Equal(200, sekme.SonucSetleri[0].Tablo.Rows.Count);
    }

    /// <summary>Gerçekten boş sonuçta uydurma set üretilmez (emniyet, sessiz veri icadı yok).</summary>
    [Fact]
    public async Task Bos_sonucta_set_uretilmez()
    {
        SorguSekmesiViewModel sekme = Kur(Profiller.Yap(MotorTuru.Mongo),
            new QueryResult { Basarili = true, ResultSetler = [], Mesajlar = ["(0 belge)"] });
        sekme.Belge.Text = """{ "find": "Bos", "limit": 200 }""";
        sekme.MetinSaglayici = () => sekme.Belge.Text;

        await sekme.CalistirAsync();

        Assert.Empty(sekme.SonucSetleri);
    }
}
