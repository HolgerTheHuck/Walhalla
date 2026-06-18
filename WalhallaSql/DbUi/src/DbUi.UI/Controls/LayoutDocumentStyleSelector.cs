using AvalonDock.Controls;
using System.Windows;
using System.Windows.Controls;

namespace DbUi.UI.Controls;

public sealed class LayoutDocumentStyleSelector : StyleSelector
{
    public Style? DocumentStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        container is LayoutDocumentItem ? DocumentStyle : null;
}
