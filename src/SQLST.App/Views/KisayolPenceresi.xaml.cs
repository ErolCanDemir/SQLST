using System.Windows;
using System.Windows.Input;

namespace SQLST.App.Views;

/// <summary>
/// Klavye kısayolları kılavuzu (V2-S2 geri bildirimi, 2026-07-17): F1 ya da üst
/// şeritteki "⌨ Kısayollar" ile açılır. Liste MainWindow.InputBindings ile birebir
/// aynı tutulmalı — yeni kısayol eklerken burayı da güncelle.
/// </summary>
public partial class KisayolPenceresi : Window
{
    public sealed record Kisayol(string Tus, string Aciklama);

    public KisayolPenceresi()
    {
        InitializeComponent();
        Liste.ItemsSource = new Kisayol[]
        {
            new("F5  /  Ctrl+E", "Sorguyu çalıştır (metin seçiliyse yalnız seçimi)"),
            new("Ctrl+Enter", "İmleçteki statement'ı çalıştır"),
            new("Esc", "Çalışan sorguyu durdur"),
            new("Ctrl+Shift+F", "SQL'i biçimlendir"),
            new("Ctrl+Space", "Otomatik tamamlama ('.' sonrası kendiliğinden açılır)"),
            new("Ctrl+N  /  Ctrl+T", "Yeni sorgu sekmesi"),
            new("Ctrl+W  /  Ctrl+F4", "Sekmeyi kapat"),
            new("Ctrl+Shift+T", "Son kapatılan sekmeyi geri aç"),
            new("Ctrl+P", "Her yere atla paleti (sekme/nesne/veritabanı/komut)"),
            new("Ctrl+H", "Sorgu geçmişi panelini aç/kapat"),
            new("Ctrl+B", "Nesne gezginini gizle/göster"),
            new("Ctrl+R", "Sonuç bölgesini gizle/göster"),
            new("F1", "Bu pencere"),
        };
    }

    private void Kapat_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
}
