using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 🔍 Profiler saf çekirdeği (v23-S1): oturum DDL'leri şablona sadık üretilmeli, ring_buffer
/// XML'i doğru satırlara çözülmeli. Canlı XEvents doğrulaması VM/canlı testte — burada sözleşme.
/// </summary>
public class ProfilerSorgulariTests
{
    [Fact]
    public void Standart_sablon_batch_ve_rpc_izler_sure_suzgeci_koymaz()
    {
        string ddl = ProfilerSorgulari.OturumOlustur(ProfilerSablonu.Standart);

        Assert.Contains("CREATE EVENT SESSION [SQLST_Profiler] ON SERVER", ddl);
        Assert.Contains("sqlserver.sql_batch_completed", ddl);
        Assert.Contains("sqlserver.rpc_completed", ddl);
        Assert.Contains("package0.ring_buffer", ddl);
        Assert.DoesNotContain("duration >=", ddl);
        // Bağlam eylemleri: kim/nereden/hangi DB olmadan akışın değeri yok.
        Assert.Contains("sqlserver.client_app_name", ddl);
        Assert.Contains("sqlserver.database_name", ddl);
        Assert.Contains("sqlserver.session_id", ddl);
    }

    [Fact]
    public void Yavas_sorgular_sablonu_esigi_MIKROSANIYE_olarak_gomer() // XEvents duration µs'tir
    {
        string ddl = ProfilerSorgulari.OturumOlustur(ProfilerSablonu.YavasSorgular, esikMs: 1500);
        Assert.Contains("duration >= 1500000", ddl); // 1500 ms = 1.500.000 µs — ms gömülürse süzgeç fiilen kapanır
    }

    [Fact]
    public void Hatalar_sablonu_error_reported_ve_onem_11_suzer()
    {
        string ddl = ProfilerSorgulari.OturumOlustur(ProfilerSablonu.Hatalar);
        Assert.Contains("sqlserver.error_reported", ddl);
        Assert.Contains("severity >= 11", ddl);
        Assert.DoesNotContain("sql_batch_completed", ddl);
    }

    [Fact]
    public void Veritabani_suzgeci_sunucu_tarafina_kacisli_gomulur()
    {
        string ddl = ProfilerSorgulari.OturumOlustur(ProfilerSablonu.Standart, veritabani: "Kds'Demo");
        Assert.Contains("[sqlserver].[database_name] = N'Kds''Demo'", ddl);
    }

    [Fact]
    public void Yasam_dongusu_sorgulari_ayni_oturum_adini_kullanir()
    {
        Assert.Contains("IF EXISTS", ProfilerSorgulari.OturumSil());
        Assert.Contains("DROP EVENT SESSION [SQLST_Profiler]", ProfilerSorgulari.OturumSil());
        Assert.Contains("STATE = START", ProfilerSorgulari.OturumBaslat());
        Assert.Contains("STATE = STOP", ProfilerSorgulari.OturumDurdur());
        Assert.Contains("dm_xe_session_targets", ProfilerSorgulari.RingBufferOku());
        Assert.Contains("N'SQLST_Profiler'", ProfilerSorgulari.RingBufferOku());
    }

    private const string OrnekXml = """
        <RingBufferTarget truncated="0" processingTime="0" totalEventsProcessed="3" eventCount="3">
          <event name="sql_batch_completed" package="sqlserver" timestamp="2026-09-24T08:00:01.100Z">
            <data name="duration"><value>2500000</value></data>
            <data name="cpu_time"><value>1200000</value></data>
            <data name="logical_reads"><value>4321</value></data>
            <data name="writes"><value>7</value></data>
            <data name="row_count"><value>42</value></data>
            <data name="batch_text"><value>SELECT * FROM dbo.Musteriler</value></data>
            <action name="client_app_name" package="sqlserver"><value>MERSIS</value></action>
            <action name="client_hostname" package="sqlserver"><value>PC1</value></action>
            <action name="database_name" package="sqlserver"><value>KdsDemo</value></action>
            <action name="username" package="sqlserver"><value>lst\ali</value></action>
            <action name="session_id" package="sqlserver"><value>73</value></action>
          </event>
          <event name="rpc_completed" package="sqlserver" timestamp="2026-09-24T08:00:02.200Z">
            <data name="duration"><value>800</value></data>
            <data name="statement"><value>exec dbo.SpMusteriGetir @id=5</value></data>
            <action name="session_id" package="sqlserver"><value>74</value></action>
          </event>
          <event name="error_reported" package="sqlserver" timestamp="2026-09-24T08:00:03.300Z">
            <data name="error_number"><value>208</value></data>
            <data name="severity"><value>16</value></data>
            <data name="message"><value>Invalid object name 'dbo.Yok'.</value></data>
            <action name="database_name" package="sqlserver"><value>KdsDemo</value></action>
            <action name="session_id" package="sqlserver"><value>75</value></action>
          </event>
        </RingBufferTarget>
        """;

    [Fact]
    public void RingBuffer_xml_i_olaylara_cozulur()
    {
        IReadOnlyList<ProfilerOlayi> olaylar = ProfilerSorgulari.RingBufferCoz(OrnekXml);

        Assert.Equal(3, olaylar.Count);

        ProfilerOlayi batch = olaylar[0];
        Assert.Equal("Batch", batch.Olay);
        Assert.Equal(2500, batch.SureMs);   // 2.500.000 µs → ms
        Assert.Equal(1200, batch.CpuMs);
        Assert.Equal(4321, batch.Reads);
        Assert.Equal(42, batch.SatirSayisi);
        Assert.Equal("SELECT * FROM dbo.Musteriler", batch.Metin);
        Assert.Equal("KdsDemo", batch.Veritabani);
        Assert.Equal("MERSIS", batch.Uygulama);
        Assert.Equal(73, batch.Spid);
        // UTC → yerel çevrildi (kind Local; mutlak an aynı).
        Assert.Equal(DateTimeKind.Local, batch.Zaman.Kind);
        Assert.Equal(new DateTime(2026, 9, 24, 8, 0, 1, 100, DateTimeKind.Utc).ToLocalTime(), batch.Zaman);

        Assert.Equal("RPC", olaylar[1].Olay);
        Assert.Equal(0, olaylar[1].SureMs);  // 800 µs < 1 ms
        Assert.StartsWith("exec dbo.SpMusteriGetir", olaylar[1].Metin);

        Assert.Equal("Hata", olaylar[2].Olay);
        Assert.Contains("Msg 208", olaylar[2].Metin);
        Assert.Contains("Invalid object name", olaylar[2].Metin);
    }

    [Fact]
    public void Artimli_okuma_yalniz_son_zamandan_yenileri_verir() // ring buffer her seferinde TÜMÜNÜ döndürür
    {
        IReadOnlyList<ProfilerOlayi> ilk = ProfilerSorgulari.RingBufferCoz(OrnekXml);
        IReadOnlyList<ProfilerOlayi> yeni = ProfilerSorgulari.RingBufferCoz(OrnekXml, ilk[1].Zaman);

        ProfilerOlayi tek = Assert.Single(yeni);
        Assert.Equal("Hata", tek.Olay); // ilk ikisi eşik ve öncesi — elendi
    }

    [Fact]
    public void Bozuk_veya_bos_xml_akisi_oldurmez()
    {
        Assert.Empty(ProfilerSorgulari.RingBufferCoz(null));
        Assert.Empty(ProfilerSorgulari.RingBufferCoz(""));
        Assert.Empty(ProfilerSorgulari.RingBufferCoz("<bozuk"));
        // Zaman damgasız olay atlanır, kalanlar çözülür.
        IReadOnlyList<ProfilerOlayi> kismi = ProfilerSorgulari.RingBufferCoz(
            """<RingBufferTarget><event name="rpc_completed"><data name="statement"><value>x</value></data></event>"""
            + """<event name="rpc_completed" timestamp="2026-09-24T09:00:00Z"/></RingBufferTarget>""");
        Assert.Single(kismi);
    }
}
