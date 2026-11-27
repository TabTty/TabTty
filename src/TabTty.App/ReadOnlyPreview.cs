using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal static class ReadOnlyPreview
{
    internal const int PageUnits = 8192;

    internal static void Add(MainWindow.FormWindow dialog, string contentKey, DialogText contents, string source)
    {
        int page = -1;
        int pages = Math.Max(1, (source.Length + PageUnits - 1) / PageUnits);
        var position = new TextBlock { TextWrapping = System.Windows.TextWrapping.Wrap };
        dialog.AddField(contentKey, contents);
        AutomationProperties.SetLabeledBy(contents, null);
        if (pages > 1)
        {
            dialog.AddDisplay("PastePreviewPageLabel", position);
            dialog.AddAction("PastePreviousPage", () => Show(Math.Max(0, page - 1)));
            dialog.AddAction("PasteNextPage", () => Show(Math.Min(pages - 1, page + 1)));
        }
        Show(0);

        void Show(int index)
        {
            if (index == page) return;
            page = index;
            int start = Boundary(page);
            contents.Text = PermissionDisplay.PastePage(source.AsSpan(start, Boundary(page + 1) - start));
            position.Text = string.Format(CultureInfo.CurrentCulture, Text("PastePreviewPage"), page + 1, pages);
            AutomationProperties.SetName(contents, $"{Text(contentKey)} {position.Text}");
        }

        int Boundary(int index)
        {
            int offset = index * PageUnits;
            if (offset >= source.Length) return source.Length;
            if (offset > 0 && (char.IsLowSurrogate(source[offset]) || source[offset] == '\n' && source[offset - 1] == '\r'))
                return offset - 1;
            return offset;
        }
    }
}
