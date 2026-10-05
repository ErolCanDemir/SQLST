using ICSharpCode.AvalonEdit.Search;

namespace SQLST.App;

/// <summary>
/// AvalonEdit Ctrl+F arama panelinin Türkçe metinleri/tooltip'leri (kullanıcı isteği 2026-07-21:
/// paneldeki ikon butonların ne yaptığı belli olmuyordu). Panel bu metinleri buton tooltip'i ve
/// "bulunamadı" mesajı olarak kullanır.
/// </summary>
public sealed class AramaYerellestirme : Localization
{
    public override string MatchCaseText => "BÜYÜK/küçük harfe duyarlı";
    public override string MatchWholeWordsText => "Tam sözcük eşleştir";
    public override string UseRegexText => "Düzenli ifade (regex) kullan";
    public override string FindNextText => "Sonrakini bul (Enter · F3)";
    public override string FindPreviousText => "Öncekini bul (Shift+F3)";
    public override string NoMatchesFoundText => "Eşleşme bulunamadı";
}
