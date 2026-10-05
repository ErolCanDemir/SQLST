using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace SQLST.App.Views;

/// <summary>
/// v20-S21 m.10 fikir 10: tanım tablosu değeri önerisi — listede <c>3    Onaylandı</c> görünür,
/// editöre YALNIZ kod yazılır (<c>3</c>). Açıklama yalnız seçmeye yardım eder, SQL'e karışmaz.
/// </summary>
public sealed class DegerTamamlamaVerisi(string kod, string aciklama) : ICompletionData
{
    public ImageSource? Image => null;

    /// <summary>Editöre giren metin — yalnız anahtar değeri.</summary>
    public string Text => kod;

    public object Content => $"{kod}    {aciklama}";

    public object? Description => aciklama;

    public double Priority => 100; // tanım değerleri bu bağlamda en üstte

    public void Complete(TextArea textArea, ISegment tamamlamaBolgesi, EventArgs olay)
        => textArea.Document.Replace(tamamlamaBolgesi, Text);
}
