using CommunityToolkit.Mvvm.ComponentModel;

namespace SQLST.App.Views;

/// <summary>
/// REST istemcisi "Değişkenler" grid satırı (v20-S13 "DB değeri → değişken"): <see cref="Ad"/> →
/// <c>{{Ad}}</c> yer tutucusu; <see cref="Deger"/> ya literaldir ya da <see cref="Sql"/> doluysa
/// "🔄 SQL'den çöz" ilk hücreyi buraya yazar. Değer GÖZLEMLENEBİLİR olmalı — grid'i kod-taraflı
/// (SQL çözümü) güncellemeyi görsün diye <see cref="ObservableObject"/>. Parametresiz kurucu =
/// DataGrid "yeni satır" için gerekli.
/// </summary>
public sealed partial class RestDegiskenGorunum : ObservableObject
{
    [ObservableProperty] private string _ad = "";
    [ObservableProperty] private string _deger = "";
    [ObservableProperty] private string _sql = "";
}
