using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class DetailsColumns
{
    private readonly ListView list;
    private readonly GridView view = new() { AllowsColumnReorder = true };
    private readonly Dictionary<GridViewColumn, Column> columns = [];
    private GridViewColumn? sortedColumn;
    private ListSortDirection direction;

    internal DetailsColumns(ListView list)
    {
        this.list = list;
        list.View = view;
        list.BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(SortByHeader));
    }

    internal GridViewColumn Add(string label, string member, double width, DataTemplate? cell = null,
        bool showHeader = true, ListSortDirection initialDirection = ListSortDirection.Ascending)
    {
        var column = new GridViewColumn { Width = width, Header = new GridViewColumnHeader() };
        if (cell is null) column.DisplayMemberBinding = new Binding(member);
        else column.CellTemplate = cell;
        view.Columns.Add(column);
        columns.Add(column, new(label, member, showHeader, initialDirection));
        return column;
    }

    internal void UpdateHeaders()
    {
        foreach (var (column, info) in columns)
        {
            var header = (GridViewColumnHeader)column.Header;
            string label = Text(info.Label);
            bool sorted = sortedColumn == column;
            bool ascending = direction == ListSortDirection.Ascending;
            header.Content = !info.ShowHeader ? "" : sorted ? label + (ascending ? " ▲" : " ▼") : label;
            AutomationProperties.SetName(header, label);
            AutomationProperties.SetItemStatus(header, sorted ? Text(ascending ? "ProfileSortAscending" : "ProfileSortDescending") : "");
        }
    }

    internal void ApplySort()
    {
        if (sortedColumn is null) return;
        string member = columns[sortedColumn].Member;
        using (list.Items.DeferRefresh())
        {
            list.Items.SortDescriptions.Clear();
            list.Items.SortDescriptions.Add(new SortDescription(member, direction));
            if (member != "Name")
                list.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        }
    }

    internal ListViewItem? RowAt(object origin) => origin is DependencyObject element
        ? ItemsControl.ContainerFromElement(list, element) as ListViewItem : null;

    private void SortByHeader(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal, Column: { } column } ||
            !columns.TryGetValue(column, out var info)) return;
        direction = sortedColumn == column
            ? (direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : info.InitialDirection;
        sortedColumn = column;
        UpdateHeaders();
        ApplySort();
    }

    private sealed record Column(string Label, string Member, bool ShowHeader, ListSortDirection InitialDirection);
}
