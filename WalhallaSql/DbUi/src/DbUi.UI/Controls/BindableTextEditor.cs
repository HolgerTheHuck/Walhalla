using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Windows;
using System.Windows.Input;
using System.Xml;

namespace DbUi.UI.Controls;

public sealed class BindableTextEditor : TextEditor
{
    private CompletionWindow? _completionWindow;
    private bool _updatingText;

    static BindableTextEditor()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(BindableTextEditor),
            new FrameworkPropertyMetadata(typeof(TextEditor)));
    }

    public BindableTextEditor()
    {
        LoadSyntaxHighlighting();
        TextChanged += OnEditorTextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ── BindableText dependency property ──────────────────────────────────

    public static readonly DependencyProperty BindableTextProperty =
        DependencyProperty.Register(
            nameof(BindableText),
            typeof(string),
            typeof(BindableTextEditor),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBindableTextChanged));

    public string BindableText
    {
        get => (string)GetValue(BindableTextProperty);
        set => SetValue(BindableTextProperty, value);
    }

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (BindableTextEditor)d;
        if (editor._updatingText) return;

        var newText = (string)(e.NewValue ?? string.Empty);
        if (editor.Text != newText)
            editor.Text = newText;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _updatingText = true;
        try { BindableText = Text; }
        finally { _updatingText = false; }
    }

    // ── Ctrl+Space completion ─────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenCompletionWindow(forceAll: true);
        }
    }

    private string GetWordBeforeCaret()
    {
        var offset = CaretOffset;
        var doc = Document;
        var start = offset;
        while (start > 0 && IsWordChar(doc.GetCharAt(start - 1)))
            start--;
        return doc.GetText(start, offset - start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void OpenCompletionWindow(bool forceAll = false)
    {
        if (_completionWindow is not null) return;

        var word = GetWordBeforeCaret();
        var completions = (word.Length == 0 && !forceAll)
            ? []
            : KeywordCompletionSource.GetCompletions(word);

        if (completions.Count == 0 && !forceAll) return;
        if (completions.Count == 0)
            completions = KeywordCompletionSource.GetCompletions("");

        _completionWindow = new CompletionWindow(TextArea);
        foreach (var item in completions)
            _completionWindow.CompletionList.CompletionData.Add(item);

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    // ── Syntax highlighting ───────────────────────────────────────────────

    private void LoadSyntaxHighlighting()
    {
        var asm = typeof(BindableTextEditor).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "DbUi.UI.Highlighting.TSQL.xshd");
        if (stream is null) return;

        using var reader = new XmlTextReader(stream);
        SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
