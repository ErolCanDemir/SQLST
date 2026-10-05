using System.Windows;
using System.Windows.Media;
using SQLST.Application;

namespace SQLST.App.Views;

/// <summary>
/// 📋 Shell yapıştır ekranı (v22-S3 saha turu-3 m.4). Kullanıcı: "shell yapıştır butonuna basınca
/// ekran açılmıyor muydu? Eğer açılıyor ise şu anda açılmıyor, bir sorun var."
///
/// ESKİDEN ekran YOKTU: düğme panoyu sessizce okuyup çevirir, başarılıysa yeni sekme açar,
/// çeviremezse SADECE ana penceredeki durum çubuğuna yazardı. Pano beklenen biçimde değilse
/// (en sık hâl) ekranda hiçbir şey olmuyor, özellik bozuk sanılıyordu.
///
/// Artık: pencere açılır, pano ön-dolu gelir, metin DÜZENLENEBİLİR ve çeviri her tuşta yenilenir —
/// hata da girdinin hemen altında görünür. Çeviri saf <see cref="MongoShellCevirici"/> ile yapılır.
/// </summary>
public partial class MongoShellPenceresi : Window
{
    private readonly Action<string> _sekmedeAc;
    private string? _json;

    public MongoShellPenceresi(string baslangicMetni, Action<string> sekmedeAc)
    {
        InitializeComponent();
        _sekmedeAc = sekmedeAc;
        Girdi.Text = baslangicMetni;
        // Tümü seçili açılır: kullanıcı Ctrl+V ile panodakini DOĞRUDAN üzerine yapıştırabilsin
        // (pano kilitliyse metin boş gelir, o zaman da imleç zaten yerindedir).
        Girdi.SelectAll();
        Loaded += (_, _) => Girdi.Focus();
        Yenile();
    }

    private void Girdi_Degisti(object sender, System.Windows.Controls.TextChangedEventArgs e) => Yenile();

    private void Yenile()
    {
        (string? json, string? hata) = MongoShellCevirici.Cevir(Girdi.Text);
        _json = json;

        Cikti.Text = json ?? "";
        Durum.Text = json is not null
            ? "✔ Çevrildi — çalıştırmadan önce gözden geçirin."
            : Girdi.Text.Trim().Length == 0
                ? "Panoda shell komutu yoktu — yukarıya yapıştırın (Ctrl+V)."
                : $"✖ {hata}";
        Durum.Foreground = json is not null
            ? (Brush)FindResource("BasariFircasi")
            : (Brush)FindResource(Girdi.Text.Trim().Length == 0 ? "SolukMetinFircasi" : "TehlikeFircasi");

        SekmedeAcDugmesi.IsEnabled = json is not null;
        KopyalaDugmesi.IsEnabled = json is not null;
    }

    private void SekmedeAc_Click(object sender, RoutedEventArgs e)
    {
        if (_json is null)
            return;
        _sekmedeAc(_json);
        Close();
    }

    private void Kopyala_Click(object sender, RoutedEventArgs e)
    {
        if (_json is not null)
            Clipboard.SetText(_json);
    }
}
