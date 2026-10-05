namespace SQLST.App.ViewModels;

/// <summary>
/// Ana penceredeki sekmelerin ortak yüzü (V2-S5'ten itibaren iki tür var:
/// sorgu sekmesi + düzenleme sekmesi). Başlık şablonu ve kapatma bu yüzden çalışır.
/// </summary>
public interface ISekme
{
    string Baslik { get; }

    SekmeDurumu Durum { get; }

    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz — ✕ gizlenir, Ctrl+W/"Tümünü kapat" atlar.</summary>
    bool Sabit { get; set; }

    /// <summary>Kalıcı bağlantı/kaynaklar bırakılır (açık TRAN varsa rollback).</summary>
    Task KapatAsync();
}
