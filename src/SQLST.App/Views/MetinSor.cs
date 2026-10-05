using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SQLST.App.Views;

/// <summary>Tek satırlık metin isteyen minik modal (kod ile kurulur — ayrı XAML yok). İptalse null.</summary>
public static class MetinSor
{
    public static string? Sor(Window? sahip, string baslik, string etiket, string varsayilan = "")
    {
        var kutu = new TextBox
        {
            Text = varsayilan,
            Margin = new Thickness(0, 6, 0, 12),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var tamam = new Button { Content = "Tamam", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), MinWidth = 80 };
        var iptal = new Button { Content = "İptal", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };

        var pencere = new Window
        {
            Title = baslik,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = sahip is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = sahip,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Application.Current?.TryFindResource("PencereZeminFircasi") as Brush ?? Brushes.White,
            Foreground = System.Windows.Application.Current?.TryFindResource("MetinFircasi") as Brush ?? Brushes.Black,
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = etiket, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(kutu);

        var dugmeler = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tamam.Click += (_, _) => pencere.DialogResult = true;
        dugmeler.Children.Add(tamam);
        dugmeler.Children.Add(iptal);
        panel.Children.Add(dugmeler);

        pencere.Content = panel;
        kutu.Loaded += (_, _) => { kutu.SelectAll(); kutu.Focus(); };
        return pencere.ShowDialog() == true && !string.IsNullOrWhiteSpace(kutu.Text) ? kutu.Text.Trim() : null;
    }
}
