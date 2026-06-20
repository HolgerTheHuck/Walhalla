using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace DbUi.UI.Controls;

public sealed class SqlCompletionData : ICompletionData
{
    public SqlCompletionData(string text, string category)
    {
        Text = text;
        Category = category;
    }

    public System.Windows.Media.ImageSource? Image => null;
    public string Text { get; }
    public string Category { get; }
    public object Content => $"{Text}  ({Category})";
    public object? Description => Category;
    public double Priority => Category switch
    {
        "keyword" => 1,
        "column" => 2,
        "table" => 3,
        "procedure" => 4,
        _ => 0,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
