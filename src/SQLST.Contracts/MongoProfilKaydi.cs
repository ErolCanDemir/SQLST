namespace SQLST.Contracts;

/// <summary>
/// system.profile'dan çıkarılmış tek yavaş sorgu kaydı (v20-S21 m.26 fikir 5/10).
/// Motor katmanı (Infrastructure) doldurur, saf öneri üretici (Application) tüketir —
/// bu yüzden sözleşme katmanında durur (ters bağımlılık olmasın).
/// </summary>
/// <param name="Koleksiyon">Sorgunun koştuğu koleksiyon (ns'in nokta sonrası).</param>
/// <param name="FiltreAlanlari">Filtrede geçen alan adları ($and/$or düzleştirilmiş).</param>
/// <param name="SiralamaAlanlari">sort'ta geçen alanlar — bileşik index'te filtreden SONRA gelir.</param>
/// <param name="Milisaniye">Sorgunun süresi.</param>
/// <param name="TaranmisMi">Plan COLLSCAN mı (index kullanılmadı mı).</param>
public sealed record ProfilKaydi(
    string Koleksiyon,
    IReadOnlyList<string> FiltreAlanlari,
    IReadOnlyList<string> SiralamaAlanlari,
    long Milisaniye,
    bool TaranmisMi);
