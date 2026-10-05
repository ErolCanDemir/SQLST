using System.Windows;
using SQLST.App.ViewModels;

namespace SQLST.App.Views;

/// <summary>Kod parçası yönetimi penceresi (V5-S4).</summary>
public partial class SnippetPenceresi : Window
{
    private readonly SnippetlerViewModel _vm;

    public SnippetPenceresi(SnippetlerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.YukleAsync();
    }
}
