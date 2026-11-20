using System.Collections;
using System.Windows.Controls;

namespace PhotoPrintStudio.App.Controls;

public sealed class AssetTrayListBox : ListBox
{
    public void ReplaceSelection(IEnumerable items) => SetSelectedItems(items);
}
