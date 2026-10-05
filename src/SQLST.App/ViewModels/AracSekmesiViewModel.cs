using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SQLST.App.ViewModels;

/// <summary>
/// v20-S21 saha m.22 devamı (kullanıcı 2026-08-14: "LINQ SQL ve SP sihirbazı ekranları da yönetim
/// paneli gibi sekme olarak açılsın"): mevcut araç PENCERESİNİN içeriğini sekmede barındırır —
/// pencere sınıfı MVVM'e yeniden yazılmadan sekmeleşir. Pencere HİÇ gösterilmez: içeriği sökülüp
/// sekmeye taşınır; olay işleyicileri pencere sınıfında yaşamaya devam eder (WPF'te işleyiciler
/// elemanlara bağlıdır, görsel ebeveyn fark etmez). Pencerenin InputBindings'i (F5 vb.) içerik
/// köküne kopyalanır; Window.Loaded'a bağlanan başlangıç işi, içeriğin İLK Loaded'ında bir kez
/// tetiklenir (sekme geçişleri Loaded'ı yeniden ateşler — tekrar koşmasın).
/// </summary>
public sealed partial class AracSekmesiViewModel : ObservableObject, ISekme
{
    /// <summary>📌 Sabit sekme (v20-S21 saha m.13): kapatılamaz.</summary>
    [ObservableProperty] private bool _sabit;

    public AracSekmesiViewModel(string baslik, Window pencere)
    {
        Baslik = baslik;
        object icerik = pencere.Content;
        pencere.Content = null; // içerik sekmenin olur — aynı eleman iki ebeveynde yaşayamaz

        if (icerik is FrameworkElement kok)
        {
            foreach (InputBinding ib in pencere.InputBindings)
                kok.InputBindings.Add(ib);

            // 🎨 METİN RENGİ KALITIMINI TAŞI (kullanıcı bulgusu 25 Ağu 2026: "SP sihirbazında koyu
            // modda madde başlıkları siyah olduğu için görünmüyor").
            //
            // Araç pencereleri metin rengini PENCERENİN üzerinde tanımlar
            // (<Window Foreground="{DynamicResource MetinFircasi}">). İçeriği buradan koparınca o
            // kalıtım zinciri KOPUYOR: artık pencereden değil sekme barındırıcısından miras alınıyor
            // ve Foreground'u AÇIKÇA verilmemiş her metin WPF varsayılanına — SİYAHA — düşüyor.
            // Rengi elle yazılmış alt satırlar sağ kaldığı için kusur "başlıklar kayboldu, altları
            // duruyor" diye görünüyordu.
            //
            // SetResourceReference kullanılıyor, sabit fırça DEĞİL: tema/palet değişince
            // (DynamicResource semantiği) renk kendiliğinden güncellensin — kullanıcı tema
            // değiştirebiliyor ve sabit atama sekmeyi eski renkte dondururdu.
            //
            // Bu düzeltme TÜM araç sekmelerini kapsar (LINQ SQL, SP Sihirbazı, …) — sorun tek bir
            // pencereye değil, bu barındırma yöntemine ait.
            kok.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MetinFircasi");

            bool tetiklendi = false;
            kok.Loaded += (_, _) =>
            {
                if (tetiklendi)
                    return;
                tetiklendi = true;
                pencere.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            };
        }

        Icerik = icerik;
    }

    public string Baslik { get; }

    /// <summary>Sekme gövdesi — DataTemplate ContentPresenter ile basar.</summary>
    public object Icerik { get; }

    public SekmeDurumu Durum => SekmeDurumu.Tamamlandi;

    public Task KapatAsync() => Task.CompletedTask; // araçlar kalıcı bağlantı tutmaz
}
