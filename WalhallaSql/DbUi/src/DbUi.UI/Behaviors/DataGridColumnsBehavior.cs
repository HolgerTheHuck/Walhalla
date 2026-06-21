using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DbUi.Core.Providers;
using DbUi.UI.Converters;

namespace DbUi.UI.Behaviors;

/// <summary>
/// Attached Behavior, das DataGrid-Spalten aus <see cref="QueryColumn"/>-Metadaten
/// generiert. Damit kann ein streamendes <c>ObservableCollection&lt;object?[]&gt;</c>
/// direkt gebunden werden, ohne auf <see cref="DataTable.AutoGeneratingColumn"/>
/// angewiesen zu sein.
/// </summary>
public static class DataGridColumnsBehavior
{
    public static readonly DependencyProperty ColumnsSourceProperty =
        DependencyProperty.RegisterAttached(
            "ColumnsSource",
            typeof(IReadOnlyList<QueryColumn>),
            typeof(DataGridColumnsBehavior),
            new PropertyMetadata(null, OnColumnsSourceChanged));

    public static readonly DependencyProperty ColumnIndexProperty =
        DependencyProperty.RegisterAttached(
            "ColumnIndex",
            typeof(int),
            typeof(DataGridColumnsBehavior),
            new PropertyMetadata(-1));

    public static IReadOnlyList<QueryColumn>? GetColumnsSource(System.Windows.Controls.DataGrid grid) =>
        (IReadOnlyList<QueryColumn>?)grid.GetValue(ColumnsSourceProperty);

    public static void SetColumnsSource(System.Windows.Controls.DataGrid grid, IReadOnlyList<QueryColumn>? value) =>
        grid.SetValue(ColumnsSourceProperty, value);

    public static int GetColumnIndex(DataGridColumn column) => (int)column.GetValue(ColumnIndexProperty);

    public static void SetColumnIndex(DataGridColumn column, int value) => column.SetValue(ColumnIndexProperty, value);

    private static void OnColumnsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.DataGrid grid) return;

        grid.Columns.Clear();

        if (e.NewValue is not IReadOnlyList<QueryColumn> columns || columns.Count == 0)
            return;

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var binding = new System.Windows.Data.Binding($"[{i}]") { Mode = BindingMode.OneWay };

            DataGridColumn column;
            if (col.DataType == typeof(byte[]))
            {
                column = new DataGridTemplateColumn
                {
                    Header = col.Name,
                    CellTemplate = CreateHexCellTemplate(binding),
                    IsReadOnly = true
                };
                SetColumnIndex(column, i);
            }
            else
            {
                column = new DataGridTextColumn
                {
                    Header = col.Name,
                    Binding = binding,
                    IsReadOnly = true
                };
                SetColumnIndex(column, i);

                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                ((DataGridTextColumn)column).ElementStyle = style;
            }

            grid.Columns.Add(column);
        }
    }

    private static DataTemplate CreateHexCellTemplate(System.Windows.Data.Binding binding)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, binding);
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        binding.Converter = new BinaryToHexConverter();
        return new DataTemplate { VisualTree = factory };
    }
}
