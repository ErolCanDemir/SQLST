using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SQLST.App.ViewModels;

namespace SQLST.App.Views;

/// <summary>
/// ⏱ SQL Agent S3 sihirbaz penceresi (v23-S18). Mantık <see cref="AgentSihirbazViewModel"/>'de; burada yalnız
/// pencere davranışı: script sekmeye açılınca kapanır, "Açık sorgu sekmesinden al" menüsü o anki sekmelerden kurulur.
/// </summary>
public partial class AgentSihirbazPenceresi : Window
{
    private readonly AgentSihirbazViewModel _vm;

    public AgentSihirbazPenceresi(AgentSihirbazViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += Vm_PropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentSihirbazViewModel.Tamamlandi) && _vm.Tamamlandi)
            Close();
    }

    private void SorgudanAl_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        IReadOnlyList<(string Baslik, string Metin)> sorgular = _vm.AcikSorgular();
        if (sorgular.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Açık sorgu sekmesi yok", IsEnabled = false });
        foreach ((string baslik, string metin) in sorgular)
        {
            string ilkSatir = metin.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? "(boş)";
            var oge = new MenuItem { Header = $"{baslik} — {(ilkSatir.Length > 60 ? ilkSatir[..60] + "…" : ilkSatir)}" };
            oge.Click += (_, _) => _vm.SorgudanAl(metin);
            menu.Items.Add(oge);
        }
        menu.IsOpen = true;
    }
}
