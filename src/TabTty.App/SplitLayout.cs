using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TabTty.Core;
using static TabTty.App.App;

namespace TabTty.App;

internal sealed class SplitViewport(SplitLayout layout) : ScrollViewer
{
    protected override Size MeasureOverride(Size constraint)
    {
        double width = constraint.Width;
        double height = constraint.Height;
        if (height < layout.MinHeight) width -= SystemParameters.VerticalScrollBarWidth;
        if (width < layout.MinWidth) height -= SystemParameters.HorizontalScrollBarHeight;
        if (height < layout.MinHeight) width = constraint.Width - SystemParameters.VerticalScrollBarWidth;
        layout.Width = double.IsFinite(width) ? Math.Max(layout.MinWidth, width) : double.NaN;
        layout.Height = double.IsFinite(height) ? Math.Max(layout.MinHeight, height) : double.NaN;
        return base.MeasureOverride(constraint);
    }
}

internal sealed class SplitLayout : Grid
{
    private const double DividerSize = 6;
    private readonly Dictionary<FrameworkElement, Leaf> leaves = [];
    private Node root;

    private abstract class Node(FrameworkElement view)
    {
        public FrameworkElement View { get; } = view;
        public Branch? Parent { get; set; }
    }

    private sealed class Leaf(FrameworkElement view) : Node(view);

    private sealed class Branch(Grid grid, Node first, Node second, Orientation orientation, ResourceReservation memory) : Node(grid)
    {
        public Node First = first;
        public Node Second = second;
        public Orientation Orientation { get; } = orientation;
        public Grid Grid => grid;
        public ResourceReservation Memory { get; } = memory;
    }

    public SplitLayout(FrameworkElement initial)
    {
        var leaf = new Leaf(initial);
        leaves.Add(initial, leaf);
        root = leaf;
        Children.Add(initial);
        RefreshMinimums();
    }

    public IEnumerable<FrameworkElement> Views => Enumerate(root);

    private static IEnumerable<FrameworkElement> Enumerate(Node node)
    {
        if (node is Branch branch)
        {
            foreach (var first in Enumerate(branch.First)) yield return first;
            foreach (var second in Enumerate(branch.Second)) yield return second;
        }
        else yield return node.View;
    }

    public static bool CanSplit(FrameworkElement view, Orientation orientation, Size newMinimum) =>
        orientation == Orientation.Horizontal
            ? view.ActualWidth >= 2 * Math.Max(view.MinWidth, newMinimum.Width) + DividerSize && view.ActualHeight >= newMinimum.Height
            : view.ActualHeight >= 2 * Math.Max(view.MinHeight, newMinimum.Height) + DividerSize && view.ActualWidth >= newMinimum.Width;

    public void Split(FrameworkElement source, FrameworkElement added, Orientation orientation)
    {
        if (!leaves.TryGetValue(source, out var first)) return;
        var memory = ApplicationResources.LayoutCache.Reserve(8192);
        var second = new Leaf(added);
        var parent = first.Parent;
        var grid = new Grid();
        var branch = new Branch(grid, first, second, orientation, memory) { Parent = parent };
        Replace(parent, first, branch);
        first.Parent = second.Parent = branch;
        leaves.Add(added, second);
        var splitter = new GridSplitter
        {
            Focusable = true, ShowsPreview = true, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = orientation == Orientation.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
        };
        splitter.SetResourceReference(BackgroundProperty, "WindowBackground");
        AutomationProperties.SetName(splitter, Text("SplitDivider"));
        if (orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DividerSize) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            SetColumn(splitter, 1);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DividerSize) });
            grid.RowDefinitions.Add(new RowDefinition());
            SetRow(splitter, 1);
        }
        Attach(branch, first.View, 0);
        Attach(branch, second.View, 2);
        grid.Children.Add(splitter);
        RefreshMinimums();
    }

    public bool Remove(FrameworkElement view)
    {
        if (!leaves.TryGetValue(view, out var leaf) || leaf.Parent is not { } branch) return false;
        var survivor = branch.First == leaf ? branch.Second : branch.First;
        branch.Grid.Children.Clear();
        var parent = branch.Parent;
        Replace(parent, branch, survivor);
        survivor.Parent = parent;
        leaves.Remove(view);
        branch.Memory.Dispose();
        RefreshMinimums();
        return true;
    }

    private void Replace(Branch? parent, Node old, Node replacement)
    {
        if (parent is null)
        {
            Children.Remove(old.View);
            root = replacement;
            SetColumn(replacement.View, 0);
            SetRow(replacement.View, 0);
            Children.Add(replacement.View);
            return;
        }
        parent.Grid.Children.Remove(old.View);
        bool first = parent.First == old;
        if (first) parent.First = replacement;
        else parent.Second = replacement;
        Attach(parent, replacement.View, first ? 0 : 2);
    }

    private static void Attach(Branch branch, FrameworkElement view, int index)
    {
        SetColumn(view, branch.Orientation == Orientation.Horizontal ? index : 0);
        SetRow(view, branch.Orientation == Orientation.Vertical ? index : 0);
        branch.Grid.Children.Add(view);
    }

    public void RefreshMinimums()
    {
        var size = MeasureMinimum(root);
        MinWidth = size.Width;
        MinHeight = size.Height;
    }

    public void UpdateLabels() => LabelDividers(root);

    public void ReleaseReservations() => Release(root);

    private static void Release(Node node)
    {
        if (node is not Branch branch) return;
        Release(branch.First);
        Release(branch.Second);
        branch.Memory.Dispose();
    }

    private static void LabelDividers(Node node)
    {
        if (node is not Branch branch) return;
        foreach (var divider in branch.Grid.Children.OfType<GridSplitter>())
            AutomationProperties.SetName(divider, Text("SplitDivider"));
        LabelDividers(branch.First);
        LabelDividers(branch.Second);
    }

    private static Size MeasureMinimum(Node node)
    {
        if (node is not Branch branch) return new Size(node.View.MinWidth, node.View.MinHeight);
        Size first = MeasureMinimum(branch.First), second = MeasureMinimum(branch.Second);
        var grid = branch.Grid;
        if (branch.Orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions[0].MinWidth = first.Width;
            grid.ColumnDefinitions[2].MinWidth = second.Width;
            return new Size(first.Width + second.Width + DividerSize, Math.Max(first.Height, second.Height));
        }
        grid.RowDefinitions[0].MinHeight = first.Height;
        grid.RowDefinitions[2].MinHeight = second.Height;
        return new Size(Math.Max(first.Width, second.Width), first.Height + second.Height + DividerSize);
    }
}
