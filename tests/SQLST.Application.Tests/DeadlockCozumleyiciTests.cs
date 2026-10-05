using SQLST.Application;

namespace SQLST.Application.Tests;

/// <summary>
/// 🕸 Deadlock çözümleyicisi (v23-S3): SQL Server'ın xml_deadlock_report biçimi süreç/kaynak
/// grafiğine doğru açılmalı — kurban, sahip/bekleyen kipleri, sorgu metinleri. Bozuk/yabancı
/// XML'de null (çağıran ham XML'i gösterir — bilgi kaybolmaz).
/// </summary>
public class DeadlockCozumleyiciTests
{
    // Gerçekçi örnek: iki süreç, iki keylock — klasik çapraz kilitlenme (biçim: SQL 2019+).
    private const string OrnekXml = """
        <deadlock>
          <victim-list><victimProcess id="process251" /></victim-list>
          <process-list>
            <process id="process251" spid="73" clientapp="MERSIS" loginname="lst\ali"
                     isolationlevel="read committed (2)">
              <inputbuf>UPDATE dbo.Musteriler SET Ad = N'X' WHERE Id = 5</inputbuf>
            </process>
            <process id="process9f8" spid="88" clientapp="ERPServis" loginname="svc_erp"
                     isolationlevel="read committed (2)">
              <inputbuf>UPDATE dbo.Siparisler SET Durum = 1 WHERE Id = 42</inputbuf>
            </process>
          </process-list>
          <resource-list>
            <keylock objectname="KdsDemo.dbo.Musteriler" indexname="PK_Musteriler">
              <owner-list><owner id="process251" mode="X" /></owner-list>
              <waiter-list><waiter id="process9f8" mode="U" requestType="wait" /></waiter-list>
            </keylock>
            <keylock objectname="KdsDemo.dbo.Siparisler" indexname="PK_Siparisler">
              <owner-list><owner id="process9f8" mode="X" /></owner-list>
              <waiter-list><waiter id="process251" mode="U" requestType="wait" /></waiter-list>
            </keylock>
          </resource-list>
        </deadlock>
        """;

    [Fact]
    public void Klasik_capraz_kilitlenme_grafige_acilir()
    {
        DeadlockGrafigi? g = DeadlockCozumleyici.Coz(OrnekXml);

        Assert.NotNull(g);
        Assert.Equal(2, g!.Surecler.Count);
        Assert.Equal(2, g.Kaynaklar.Count);

        DeadlockSureci kurban = g.Surecler.Single(s => s.KurbanMi);
        Assert.Equal("process251", kurban.Id);
        Assert.Equal(73, kurban.Spid);
        Assert.Equal("MERSIS", kurban.Uygulama);
        Assert.Contains("UPDATE dbo.Musteriler", kurban.Sorgu);
        Assert.False(g.Surecler.Single(s => s.Id == "process9f8").KurbanMi);

        DeadlockKaynagi musteriler = g.Kaynaklar.Single(k => k.Ad.Contains("Musteriler"));
        Assert.Equal("keylock", musteriler.Tur);
        Assert.Equal(("process251", "X"), Assert.Single(musteriler.Sahipler));
        Assert.Equal(("process9f8", "U"), Assert.Single(musteriler.Bekleyenler));
    }

    [Fact]
    public void Eski_bicim_victim_ozniteligi_de_taninir()
    {
        string eski = OrnekXml
            .Replace("<victim-list><victimProcess id=\"process251\" /></victim-list>", "")
            .Replace("<deadlock>", "<deadlock victim=\"process9f8\">");
        DeadlockGrafigi? g = DeadlockCozumleyici.Coz(eski);
        Assert.Equal("process9f8", g!.Surecler.Single(s => s.KurbanMi).Id);
    }

    [Fact]
    public void Bozuk_ya_da_yabanci_xml_null_doner() // çağıran ham metni gösterir — çökmek yasak
    {
        Assert.Null(DeadlockCozumleyici.Coz(null));
        Assert.Null(DeadlockCozumleyici.Coz(""));
        Assert.Null(DeadlockCozumleyici.Coz("<bozuk"));
        Assert.Null(DeadlockCozumleyici.Coz("<baska><xml /></baska>"));
        Assert.Null(DeadlockCozumleyici.Coz("<deadlock><process-list /></deadlock>")); // süreçsiz
    }

    [Fact]
    public void Profiler_sablonu_deadlock_oturumu_uretir_ve_akista_cozulur() // uçtan uca köprü
    {
        string ddl = ProfilerSorgulari.OturumOlustur(ProfilerSablonu.Deadlock);
        Assert.Contains("sqlserver.xml_deadlock_report", ddl);
        Assert.DoesNotContain("sql_batch_completed", ddl);

        // ring_buffer olayı: xml_report GÖMÜLÜ XML'dir — .Value değil ağacın kendisi alınmalı.
        string ringXml = $"""
            <RingBufferTarget><event name="xml_deadlock_report" timestamp="2026-09-24T10:00:00.000Z">
              <data name="xml_report"><value>{OrnekXml}</value></data>
            </event></RingBufferTarget>
            """;
        IReadOnlyList<ProfilerOlayi> olaylar = ProfilerSorgulari.RingBufferCoz(ringXml);

        ProfilerOlayi olay = Assert.Single(olaylar);
        Assert.Equal("Deadlock", olay.Olay);
        Assert.Contains("<victim-list>", olay.Metin);   // etiketler KORUNDU (Value birleştirmesi değil)
        Assert.NotNull(DeadlockCozumleyici.Coz(olay.Metin)); // akıştan gelen metin şemaya açılabilir
    }
}
