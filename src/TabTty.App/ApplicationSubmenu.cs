using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace TabTty.App;

// Draw beyond the scrolling viewport while staying under the Menu's input capture.
[ContentProperty(nameof(Child))]
internal sealed class ApplicationSubmenu : FrameworkElement
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(ApplicationSubmenu),
        new PropertyMetadata(false, (element, _) => ((ApplicationSubmenu)element).UpdateFlyout()));

    private FrameworkElement? child;
    private AdornerLayer? layer;
    private Flyout? flyout;

    public ApplicationSubmenu()
    {
        Loaded += (_, _) => UpdateFlyout();
        IsVisibleChanged += (_, _) => UpdateFlyout();
        Unloaded += (_, _) => RemoveFlyout();
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public FrameworkElement? Child
    {
        get => child;
        set
        {
            RemoveFlyout();
            if (child is not null) RemoveLogicalChild(child);
            child = value;
            if (child is not null) AddLogicalChild(child);
            UpdateFlyout();
        }
    }

    protected override IEnumerator LogicalChildren =>
        (child is null ? Array.Empty<FrameworkElement>() : new[] { child }).GetEnumerator();

    private void UpdateFlyout()
    {
        if (!IsOpen || !IsLoaded || !IsVisible || child is null)
        {
            RemoveFlyout();
            return;
        }
        if (flyout is not null || TemplatedParent is not FrameworkElement anchor ||
            MainWindow.Ancestor<Menu>(this) is not { } menu ||
            menu.Template.FindName("menuOverlay", menu) is not FrameworkElement root ||
            AdornerLayer.GetAdornerLayer(root) is not { } targetLayer) return;

        layer = targetLayer;
        flyout = new Flyout(root, anchor, child);
        layer.Add(flyout);
        LayoutUpdated += UpdatePlacement;
    }

    private void UpdatePlacement(object? sender, EventArgs e) => flyout?.RefreshPlacement();

    private void RemoveFlyout()
    {
        if (flyout is not { } open) return;
        flyout = null;
        LayoutUpdated -= UpdatePlacement;
        layer!.Remove(open);
        layer = null;
        open.Detach();
    }

    private sealed class Flyout : Adorner
    {
        private readonly FrameworkElement root;
        private readonly FrameworkElement anchor;
        private FrameworkElement? content;
        private Rect lastAnchor = Rect.Empty;

        internal Flyout(FrameworkElement root, FrameworkElement anchor, FrameworkElement content) : base(root)
        {
            this.root = root;
            this.anchor = anchor;
            this.content = content;
            AddVisualChild(content);
        }

        protected override int VisualChildrenCount => content is null ? 0 : 1;
        protected override Visual GetVisualChild(int index) =>
            index == 0 && content is not null ? content : throw new ArgumentOutOfRangeException(nameof(index));

        internal void Detach()
        {
            if (content is not { } child) return;
            content = null;
            RemoveVisualChild(child);
        }

        internal void RefreshPlacement()
        {
            if (anchor.IsLoaded && AnchorBounds() != lastAnchor) InvalidateArrange();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            var size = root.RenderSize;
            content?.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size size)
        {
            if (content is not { } child || !anchor.IsLoaded) return size;
            lastAnchor = AnchorBounds();
            var extent = new Size(Math.Min(child.DesiredSize.Width, size.Width), Math.Min(child.DesiredSize.Height, size.Height));
            child.Arrange(new Rect(Math.Clamp(lastAnchor.Left - extent.Width, 0, size.Width - extent.Width),
                Math.Clamp(lastAnchor.Top, 0, size.Height - extent.Height), extent.Width, extent.Height));
            return size;
        }

        private Rect AnchorBounds() => new(anchor.TranslatePoint(new Point(), root), anchor.RenderSize);
    }
}
