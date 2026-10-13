using SQLST.App.ViewModels;
using SQLST.Application;
using SQLST.Contracts;

namespace SQLST.App.Tests;

/// <summary>
/// 📈 Query Store sekmesi VM'i (v23-S28 S1) sahte köprüyle: açık durum → liste + ilk sorgu seçili + planlar + grafik;
/// çip değişimi listeyi yeniden çeker (metrik/pencere SQL'e yansır); kapalı → bant + Aç (onay) → açılır; sürüm desteklemiyor;
/// izin yok; plan zorla (onay reddi/kabulü) → kaldır; planı aç XML'i köprüye verir; sorguyu sekmede aç; salt-okunur profil.
/// </summary>
public class QueryStoreVmTests
{
    private sealed class Sahte
    {
        public int Surum = 17;
        public string Durum = "READ_WRITE";
        public bool Okuma = true, Alter = true;
        public bool Zorlu;
        public bool OnayCevabi = true;
        public List<string> Sqller = [];
        public List<OnayIstegi> Onaylar = [];

        private static ResultSetData Rs(string[] k, params object?[][] s) => new() { Kolonlar = [.. k.Select(x => new KolonBilgisi(x, "x"))], Satirlar = s };

        public Task<QueryResult> Kos(string sql, string? db, int timeout, CancellationToken ct)
        {
            Sqller.Add(sql);
            if (sql.Contains("ProductMajorVersion"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["surum", "okuma_izni", "alter_izni"], [Surum, Okuma ? 1 : 0, Alter ? 1 : 0])] });
            if (sql.Contains("database_query_store_options"))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["durum", "readonly_reason", "yakalama", "max_mb", "cur_mb", "aralik_dk", "sorgu_sayisi", "plan_sayisi"],
                    [Durum, Durum == "READ_ONLY" ? 65536 : 0, "AUTO", 1000, 42, 60, 1284, 1412])] });
            if (sql.StartsWith("ALTER DATABASE")) { Durum = "READ_WRITE"; return Task.FromResult(new QueryResult { Basarili = true }); }
            if (sql.Contains("sp_query_store_force_plan")) { Zorlu = true; return Task.FromResult(new QueryResult { Basarili = true }); }
            if (sql.Contains("sp_query_store_unforce_plan")) { Zorlu = false; return Task.FromResult(new QueryResult { Basarili = true }); }
            if (sql.Contains("sp_query_store_flush_db")) return Task.FromResult(new QueryResult { Basarili = true });
            if (sql.Contains("plan_xml")) return Task.FromResult(new QueryResult { ResultSetler = [Rs(["plan_xml"], ["<ShowPlanXML/>"])] });
            if (sql.Contains("WHERE p.query_id ="))
                return Task.FromResult(new QueryResult { ResultSetler = [Rs(["plan_id", "yurutme", "ort_ms", "enyuksek_ms", "son_calisma", "zorlanmis", "zorlama_hatasi"],
                    [1588L, 6210L, 100.2m, 842m, new DateTime(2026, 10, 13, 9, 41, 0), Zorlu, DBNull.Value],
                    [1402L, 11880L, 31.4m, 120m, new DateTime(2026, 10, 12, 8, 2, 0), false, DBNull.Value])] });
            // liste (en pahalı / gerileyen / zorlanmış)
            return Task.FromResult(new QueryResult { ResultSetler = [Rs(["query_id", "sql_metni", "nesne", "yurutme", "toplam_ms", "ort_ms", "enyuksek_ms", "cpu_toplam_ms", "okuma_toplam", "plan_sayisi", "zorlanmis", "zorlama_hatasi", "son_calisma", "onceki_ort_ms", "plan_degisti"],
                [412L, "SELECT s.SiparisId FROM Siparis s", "dbo.usp_SiparisOzeti", 18420L, 1846210m, 100.2m, 842m, 90000m, 12345m, 3, Zorlu ? 1 : 0, 0, new DateTime(2026, 10, 13, 9, 41, 0), sql.Contains("once") ? 31.4m : DBNull.Value, sql.Contains("once") ? 1 : 0],
                [87L, "UPDATE Stok SET Adet = Adet - 1", DBNull.Value, 402113L, 612004m, 1.5m, 9m, 1000m, 50m, 1, 0, 0, DBNull.Value, DBNull.Value, 0])] });
        }

        public bool Onay(OnayIstegi i) { Onaylar.Add(i); return OnayCevabi; }
    }

    private static (QueryStoreSekmesiViewModel Vm, Sahte S, List<(string Baslik, string Sql, string? Db)> Sekmeler, List<(string Xml, string Baslik)> Planlar) Kur(Action<Sahte>? ayarla = null, bool saltOkunur = false)
    {
        var s = new Sahte();
        ayarla?.Invoke(s);
        List<(string, string, string?)> sekmeler = [];
        List<(string, string)> planlar = [];
        var vm = new QueryStoreSekmesiViewModel("Satis", s.Kos, (b, q, d) => sekmeler.Add((b, q, d)), (x, b) => planlar.Add((x, b)), s.Onay, saltOkunur);
        return (vm, s, sekmeler, planlar);
    }

    [Fact]
    public async Task Acik_durum_liste_secim_planlar_grafik_ve_cipler()
    {
        (QueryStoreSekmesiViewModel vm, Sahte s, _, _) = Kur();
        await vm.YenileAsync();
        await Task.Delay(50);

        Assert.Equal("📈 Query Store · Satis", vm.Baslik);
        Assert.Equal("ok", vm.DurumTuru);
        Assert.Contains("AÇIK", vm.DurumRozeti);
        Assert.Contains("42 / 1.000 MB", vm.DurumMeta.Replace(".", "."));
        Assert.False(vm.UyariVar);
        Assert.Equal(2, vm.Sorgular.Count);
        Assert.Equal("SELECT s.SiparisId FROM Siparis s", vm.Sorgular[0].Kisa);
        Assert.Equal("—", vm.Sorgular[1].Nesne);
        Assert.Equal(412, vm.SeciliSorgu!.QueryId);                 // ilk sorgu ön-seçili
        Assert.Equal("#412 · dbo.usp_SiparisOzeti", vm.SeciliBaslik);
        Assert.Equal(2, vm.Planlar.Count);
        Assert.Equal(1588, vm.SeciliPlan!.PlanId);                  // en son çalışan = "şu anki"
        Assert.Equal("şu anki", vm.Planlar[0].DurumMetni);
        Assert.Equal("eski", vm.Planlar[1].DurumMetni);
        Assert.NotNull(vm.PlanGrafigi);
        Assert.Equal(["plan 1588", "plan 1402"], vm.PlanGrafigi!.Kategoriler);
        Assert.Equal([100.2, 31.4], vm.PlanGrafigi.Seriler[0].Degerler);
        Assert.True(vm.ZorlaAcik); Assert.False(vm.KaldirAcik); Assert.True(vm.FlushAcik);
        Assert.Contains("son 24 saat", vm.ListeNotu);

        // metrik/pencere çipleri SQL'e yansır
        s.Sqller.Clear();
        vm.MetrikCpu = true;
        vm.IstatistikOrtalama = true;
        vm.PencereGun7 = true;
        await Task.Delay(50);
        Assert.Contains(s.Sqller, q => q.Contains("ORDER BY cpu_ort_ms DESC") && q.Contains("-168,"));
        Assert.True(vm.MetrikSecilebilir);

        // gerileyen görünümü: rozet ve seçili rozet metni, metrik çipleri pasif
        vm.GorunumGerileyen = true;
        await Task.Delay(50);
        Assert.False(vm.MetrikSecilebilir);
        Assert.Equal("plan değişti", vm.Sorgular[0].RozetMetni);
        Assert.Contains("×3,2", vm.SeciliRozet.Replace('.', ','));
        vm.GorunumZorlanmis = true;
        Assert.False(vm.PencereSecilebilir);
    }

    [Fact]
    public async Task Kapali_bant_ve_ac_onayli()
    {
        (QueryStoreSekmesiViewModel vm, Sahte s, List<(string Baslik, string Sql, string? Db)> sekmeler, _) = Kur(x => x.Durum = "OFF");
        await vm.YenileAsync();
        Assert.Contains("KAPALI", vm.DurumRozeti);
        Assert.Contains("KAPALI", vm.Uyari);
        Assert.True(vm.AcilebilirMi);
        Assert.Empty(vm.Sorgular);
        Assert.False(vm.IslemYapilabilir);

        vm.AcScriptiAlCommand.Execute(null);
        Assert.Contains("SET QUERY_STORE = ON", Assert.Single(sekmeler).Sql);

        s.OnayCevabi = false;
        await vm.AcCommand.ExecuteAsync(null);
        Assert.Contains("KAPALI", vm.DurumRozeti);
        s.OnayCevabi = true;
        await vm.AcCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.Equal("▶ Query Store'u aç", s.Onaylar[^1].OnayMetni);
        Assert.Contains("AÇIK", vm.DurumRozeti);
        Assert.Equal(2, vm.Sorgular.Count);
    }

    [Fact]
    public async Task Surum_izin_salt_okunur_ve_profil_salt_okunur()
    {
        (QueryStoreSekmesiViewModel vm, _, _, _) = Kur(x => x.Surum = 12);
        await vm.YenileAsync();
        Assert.Contains("desteklemiyor", vm.Uyari);
        Assert.Equal("hata", vm.DurumTuru);
        Assert.False(vm.AcilebilirMi);

        (vm, _, _, _) = Kur(x => x.Okuma = false);
        await vm.YenileAsync();
        Assert.Contains("VIEW DATABASE STATE", vm.Uyari);
        Assert.Empty(vm.Sorgular);

        (vm, _, _, _) = Kur(x => x.Durum = "READ_ONLY");
        await vm.YenileAsync();
        Assert.Contains("depo dolu", vm.Uyari);
        Assert.Equal("uyari", vm.DurumTuru);
        Assert.False(vm.FlushAcik);
        Assert.True(vm.IslemYapilabilir);  // salt-okunur depoda plan yine zorlanabilir

        (vm, _, _, _) = Kur(x => x.Durum = "OFF", saltOkunur: true);
        await vm.YenileAsync();
        Assert.False(vm.AcilebilirMi);      // K5: salt-okunur profilde yazma yok
    }

    [Fact]
    public async Task Plan_zorla_kaldir_plani_ac_sorguyu_ac()
    {
        (QueryStoreSekmesiViewModel vm, Sahte s, List<(string Baslik, string Sql, string? Db)> sekmeler, List<(string Xml, string Baslik)> planlar) = Kur();
        await vm.YenileAsync();
        await Task.Delay(50);

        s.OnayCevabi = false;
        await vm.ZorlaCommand.ExecuteAsync(null);
        Assert.False(s.Zorlu);
        s.OnayCevabi = true;
        await vm.ZorlaCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.True(s.Zorlu);
        Assert.Equal("📌 Planı zorla", s.Onaylar[^1].OnayMetni);
        Assert.Contains("zorlandı", vm.Bilgi);
        Assert.Equal("zorlanmış", vm.Sorgular[0].RozetMetni);
        Assert.True(vm.Planlar[0].Plan.Zorlanmis);
        Assert.True(vm.KaldirAcik); Assert.False(vm.ZorlaAcik);

        await vm.ZorlamayiKaldirCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.False(s.Zorlu);
        Assert.Contains("kaldırıldı", vm.Bilgi);

        await vm.PlaniAcCommand.ExecuteAsync(null);
        (string xml, string baslik) = Assert.Single(planlar);
        Assert.Equal("<ShowPlanXML/>", xml);
        Assert.Equal("QS plan 1588 · Satis", baslik);

        vm.SorguyuAcCommand.Execute(null);
        vm.ZorlaScriptiAlCommand.Execute(null);
        Assert.Equal(2, sekmeler.Count);
        Assert.Equal(("qs-412.sql", "SELECT s.SiparisId FROM Siparis s", "Satis"), sekmeler[0]);
        Assert.Contains("sp_query_store_force_plan @query_id = 412, @plan_id = 1588", sekmeler[1].Sql);
    }
}
