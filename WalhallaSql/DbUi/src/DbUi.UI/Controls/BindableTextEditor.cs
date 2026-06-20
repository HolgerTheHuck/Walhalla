using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using DbUi.Core.Catalog;
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
        TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        TextArea.SelectionChanged += OnSelectionChanged;
    }

    // ── Caret / Selection bindable properties ───────────────────────────
    // Umbenannt, damit sie die gleichnamigen AvalonEdit-Member nicht verdecken.

    public static readonly DependencyProperty EditorCaretOffsetProperty =
        DependencyProperty.Register(
            nameof(EditorCaretOffset),
            typeof(int),
            typeof(BindableTextEditor),
            new PropertyMetadata(0));

    public int EditorCaretOffset
    {
        get => (int)GetValue(EditorCaretOffsetProperty);
        set => SetValue(EditorCaretOffsetProperty, value);
    }

    public static readonly DependencyProperty EditorSelectedTextProperty =
        DependencyProperty.Register(
            nameof(EditorSelectedText),
            typeof(string),
            typeof(BindableTextEditor),
            new PropertyMetadata(string.Empty));

    public string EditorSelectedText
    {
        get => (string)GetValue(EditorSelectedTextProperty);
        set => SetValue(EditorSelectedTextProperty, value);
    }

    public static readonly DependencyProperty EditorSelectionStartProperty =
        DependencyProperty.Register(
            nameof(EditorSelectionStart),
            typeof(int),
            typeof(BindableTextEditor),
            new PropertyMetadata(0));

    public int EditorSelectionStart
    {
        get => (int)GetValue(EditorSelectionStartProperty);
        set => SetValue(EditorSelectionStartProperty, value);
    }

    public static readonly DependencyProperty EditorSelectionLengthProperty =
        DependencyProperty.Register(
            nameof(EditorSelectionLength),
            typeof(int),
            typeof(BindableTextEditor),
            new PropertyMetadata(0));

    public int EditorSelectionLength
    {
        get => (int)GetValue(EditorSelectionLengthProperty);
        set => SetValue(EditorSelectionLengthProperty, value);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
        => EditorCaretOffset = TextArea.Caret.Offset;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var selection = TextArea.Selection;
        EditorSelectedText = selection.IsEmpty ? string.Empty : selection.GetText();
        EditorSelectionStart = selection.IsEmpty
            ? EditorCaretOffset
            : Document.GetOffset(selection.StartPosition.Line, selection.StartPosition.Column);
        EditorSelectionLength = selection.IsEmpty ? 0 : Math.Max(selection.Length, 0);
    }

    // ── CatalogSnapshot dependency property ─────────────────────────────

    public static readonly DependencyProperty CatalogSnapshotProperty =
        DependencyProperty.Register(
            nameof(CatalogSnapshot),
            typeof(CatalogSnapshot),
            typeof(BindableTextEditor),
            new PropertyMetadata(default(CatalogSnapshot)));

    public CatalogSnapshot? CatalogSnapshot
    {
        get => (CatalogSnapshot?)GetValue(CatalogSnapshotProperty);
        set => SetValue(CatalogSnapshotProperty, value);
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

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenCompletionWindow(forceAll: true);
        }
    }

    private string GetTextBeforeCaret()
    {
        var offset = CaretOffset;
        return Document.GetText(0, offset);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void OpenCompletionWindow(bool forceAll = false)
    {
        if (_completionWindow is not null) return;

        var textBeforeCaret = GetTextBeforeCaret();
        var prefix = SqlCompletionSource.ExtractPrefix(textBeforeCaret);

        var completions = (prefix.Length == 0 && !forceAll)
            ? []
            : SqlCompletionSource.GetCompletions(textBeforeCaret, CatalogSnapshot);

        if (completions.Count == 0 && !forceAll) return;
        if (completions.Count == 0)
            completions = SqlCompletionSource.GetCompletions("", CatalogSnapshot);

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
