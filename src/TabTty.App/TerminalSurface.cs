using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TerminalSurface : FrameworkElement
{
    private FontFamily font = new("Consolas");
    private readonly Dictionary<uint, Brush> brushes = [];
    private readonly Dictionary<(string Text, uint Color, TerminalStyleFlags Style), GlyphEntry> glyphs = [];
    private Dictionary<Point, TranslateTransform> cellTranslations = [];
    private readonly ResourceReservation translationMemory = ApplicationResources.LayoutCache.Reserve(0);
    private readonly ResourceReservation brushMemory = ApplicationResources.LayoutCache.Reserve(0);
    private TerminalSnapshot? snapshot;
    private long snapshotVersion;
    private long renderedSnapshotVersion = -1;
    private TerminalMatch? selection;
    private TerminalMatch? search;
    private IReadOnlyList<TerminalMatch> searchMatches = [];
    private IReadOnlyDictionary<int, TerminalHighlightSpan[]> highlights = new Dictionary<int, TerminalHighlightSpan[]>();
    private (int Row, int Offset)? dragStart;
    private bool connected;
    private bool remoteGesture => remoteButtons.Count != 0;
    private readonly List<int> remoteButtons = [];
    private bool movingHost;
    private bool captureLostDuringMove;
    private int gestureGeneration;
    private (int Button, int Column, int Row)? lastMouseCell;
    private bool suppressContext;
    private double cellWidth;
    private double rowHeight;
    private double dpi;
    private string composition = "";
    private TextComposition? activeComposition;
    internal bool HasComposition => activeComposition is not null;
    private TextComposition? discardedComposition;
    private bool imeCleanupScheduled;
    // The default WPF text store is shared by the UI thread. Its result can
    // arrive as a new, result-only composition on the pane taking focus.
    [ThreadStatic] private static TextComposition? terminatingComposition;
    private readonly TextBox imeInput;
    private readonly VisualCollection visuals;
    private readonly DispatcherTimer blink;
    private bool presentationEnabled = true;
    private bool cursorOn = true;
    private double fontSize = App.DefaultTerminalFontSize * 96 / 72;
    private const double HorizontalInset = 4;
    private double? outputScrollOffset;
    private readonly TranslateTransform viewportTranslation = new();
    private readonly RectangleGeometry viewportClip = new();

    public ScrollViewer Scroller { get; }
    internal TerminalSnapshot? Snapshot => snapshot;
    internal bool IsCurrentSnapshotRendered => snapshot is not null && snapshotVersion == renderedSnapshotVersion;
    internal event Action? ViewportRendered;
    internal TerminalMatch? Selection => selection;
    internal string AutomationFontFamily => font.Source;
    internal double AutomationFontPoints => fontSize * 72 / 96;
    public bool UsesFontFallback { get; private set; }
    public bool IsPresentationActive => presentationEnabled && IsVisible;
    public event Action? PresentationChanged;
    public event Action<bool>? ViewportChanged;
    public event Action<TerminalMouseInput>? MouseInput;
    private long mouseCapture;
    internal long ActiveMouseCapture { get; private set; }
    public event Action<long>? RemoteGestureStarted;
    public event Action<long>? RemoteGestureEnded;
    public event Action<long>? RemoteCaptureLost;
    public event Action? RemoteCaptureFailed;
    public Func<bool>? UnclampedMouse { get; set; }
    public double VerticalOffset => Scroller.VerticalOffset - viewportTranslation.Y;
    public double ExtentHeight => Scroller.ExtentHeight;
    public double ViewportHeight => Scroller.ViewportHeight;
    private bool FollowsEnd => AtEnd(Scroller.VerticalOffset, Scroller.ScrollableHeight);

    public TerminalSurface()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, false);
        ClipToBounds = true;
        imeInput = new TextBox
        {
            Background = Brushes.Transparent, Foreground = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), FontFamily = font, FontSize = fontSize, IsUndoEnabled = false,
            IsHitTestVisible = false, Opacity = 0.01,
        };
        // Let Windows translate these keys into text instead of the TextBox inserting a space directly.
        imeInput.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, Key.Space, ModifierKeys.None));
        imeInput.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, Key.Space, ModifierKeys.Shift));
        visuals = new VisualCollection(this) { imeInput };
        uint blinkTime = GetCaretBlinkTime();
        blink = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(blinkTime is 0 or uint.MaxValue ? 530 : Math.Clamp(blinkTime, 50, 5000)),
        };
        blink.Tick += (_, _) => { cursorOn = !cursorOn; InvalidateVisual(); };
        void UpdateBlink()
        {
            bool enabled = blinkTime is not (0 or uint.MaxValue) && connected && IsPresentationActive &&
                IsKeyboardFocusWithin && !HasComposition && snapshot is { CursorVisible: true, CursorBlink: true };
            if (enabled && !blink.IsEnabled) blink.Start();
            if (!enabled) blink.Stop();
            cursorOn = true;
        }
        IsVisibleChanged += (_, _) => UpdatePresentation();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateBlink();
        updateBlink = UpdateBlink;
        PreviewMouseDown += (_, e) =>
        {
            suppressContext = false;
            if ((!remoteGesture && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) || dragStart is not null || !CanReportMouse()) return;
            Point point = e.GetPosition(this);
            if (!remoteGesture && !IsActiveScreenPoint(point)) return;
            int button = Button(e.ChangedButton);
            if (button < 0) return;
            Focus();
            if (!remoteGesture && !CaptureMouse())
            {
                e.Handled = true;
                RemoteCaptureFailed?.Invoke();
                return;
            }
            if (remoteButtons.Contains(button)) return;
            if (!remoteGesture)
            {
                ActiveMouseCapture = ++mouseCapture;
                RemoteGestureStarted?.Invoke(ActiveMouseCapture);
            }
            remoteButtons.Add(button);
            gestureGeneration = snapshot!.Mouse.Generation;
            lastMouseCell = null;
            suppressContext = e.ChangedButton == MouseButton.Right;
            ReportMouse(point, button, release: false, motion: false);
            e.Handled = true;
        };
        PreviewMouseUp += (_, e) =>
        {
            int button = Button(e.ChangedButton);
            if (!remoteButtons.Remove(button)) return;
            if (snapshot?.Mouse.Generation == gestureGeneration)
                ReportMouse(e.GetPosition(this), button, release: true, motion: false);
            if (!remoteGesture)
            {
                EndMouseRoute();
                if (IsMouseCaptured) ReleaseMouseCapture();
            }
            e.Handled = true;
        };
        LostMouseCapture += (_, _) =>
        {
            dragStart = null;
            if (movingHost)
            {
                captureLostDuringMove |= remoteGesture;
                return;
            }
            CancelRemoteGesture(notify: true);
        };
        ContextMenuOpening += (_, e) =>
        {
            if (suppressContext || CanReportMouse() && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                e.Handled = true;
            suppressContext = false;
        };
        InputMethod.SetIsInputMethodEnabled(this, true);
        Scroller = new ScrollViewer
        {
            Content = this,
            Background = Brushes.Black,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
        };
        RenderTransform = viewportTranslation;
        Scroller.ScrollChanged += (_, change) =>
        {
            bool wasFollowing = AtEnd(change.VerticalOffset - change.VerticalChange,
                Math.Max(0, change.ExtentHeight - change.ExtentHeightChange -
                    (change.ViewportHeight - change.ViewportHeightChange)));
            if (wasFollowing && (change.ExtentHeightChange != 0 || change.ViewportHeightChange != 0) &&
                !FollowsEnd && outputScrollOffset is null)
            {
                outputScrollOffset = double.PositiveInfinity;
                Scroller.ScrollToBottom();
            }
            UpdateViewport();
            InvalidateVisual();
            if (change.VerticalChange != 0)
            {
                bool automatic = outputScrollOffset is { } offset &&
                    Math.Abs(change.VerticalOffset - Math.Min(offset, Scroller.ScrollableHeight)) < 0.01;
                outputScrollOffset = null;
                ViewportChanged?.Invoke(automatic);
            }
            else if (change.ViewportHeightChange != 0)
                ViewportChanged?.Invoke(false);
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, e) =>
        {
            if (e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off) ShowComposition(e);
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, e) => ShowComposition(e));
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, e) =>
        {
            if (ReferenceEquals(discardedComposition, e.TextComposition) ||
                terminatingComposition is { } terminating && terminating.GetType() == e.TextComposition.GetType())
            {
                e.Handled = true;
            }
            ClearComposition();
        });
        imeInput.TextChanged += (_, _) => ScheduleImeCleanup();
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            InvalidateVisual();
        };
        PreviewLostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject next &&
                (MainWindow.Ancestor<TerminalSurface>(next) is { } pane && pane != this ||
                    next is TabItem { IsSelected: false })) CancelComposition();
        };
    }

    public void SetPresentationEnabled(bool value)
    {
        if (presentationEnabled == value) return;
        presentationEnabled = value;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        updateBlink();
        if (IsPresentationActive) InvalidateVisual();
        PresentationChanged?.Invoke();
    }

    public void SetSnapshot(TerminalSnapshot value)
    {
        if (remoteGesture && value.Mouse.Generation != gestureGeneration) CancelRemoteGesture(notify: true);
        selection = selection is { } selected && snapshot is { } old ? value.MapMatchFrom(old, selected) : null;
        if (!ReferenceEquals(snapshot, value)) snapshotVersion++;
        snapshot = value;
        if (UIElementAutomationPeer.FromElement(this) is TerminalAutomationPeer automation) automation.SetDocument(value);
        updateBlink();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void UpdateSnapshotPosition(TerminalSnapshot value)
    {
        int previousCount = snapshot?.Count ?? 0;
        bool follow = FollowsEnd;
        UpdateMetrics();
        int row = (int)Math.Round(VerticalOffset / rowHeight);
        int? anchor = !follow && snapshot is { } previous ? value.MapViewportRowFrom(previous, row) : null;
        SetSnapshot(value);
        if (follow && value.Count != previousCount)
        {
            outputScrollOffset = double.PositiveInfinity;
            Scroller.ScrollToBottom();
        }
        else if (anchor is { } mapped && mapped != row)
        {
            double offset = mapped * rowHeight;
            outputScrollOffset = offset;
            Scroller.ScrollToVerticalOffset(offset);
        }
    }

    public void BeginHostMove()
    {
        captureLostDuringMove = false;
        movingHost = true;
    }

    public bool EndHostMove()
    {
        movingHost = false;
        if (!remoteGesture) return false;
        // A physical release delivered during the move is already reflected in
        // remoteButtons. Button state alone cannot bridge a gap in capture.
        if (!captureLostDuringMove && IsMouseCaptured && IsVisible && PresentationSource.FromVisual(this) is not null)
            return false;
        CancelRemoteGesture(notify: true);
        return true;
    }

    public void CancelRemoteGesture(bool notify = false)
    {
        if (!remoteGesture) return;
        remoteButtons.Clear();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (notify) RemoteCaptureLost?.Invoke(ActiveMouseCapture);
        EndMouseRoute();
    }

    private void EndMouseRoute()
    {
        long capture = ActiveMouseCapture;
        ActiveMouseCapture = 0;
        if (capture != 0) RemoteGestureEnded?.Invoke(capture);
    }

    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];
    public new bool Focus() => imeInput.Focus();
    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    internal void AutomationSelect(TerminalMatch? range)
    {
        selection = range;
        InvalidateVisual();
    }

    internal void ScrollTextRange(TerminalMatch range, bool top)
    {
        UpdateMetrics();
        int row = top ? range.StartRow : range.EndRow + 1;
        Scroller.ScrollToVerticalOffset((top ? row : row - VisibleRowCount) * rowHeight);
    }

    internal double[] TextRectangles(TerminalMatch range)
    {
        if (snapshot is null || range.StartRow == range.EndRow && range.StartOffset == range.EndOffset) return [];
        var result = new List<double>();
        var (first, end) = VisibleRows();
        for (int row = Math.Max(first, range.StartRow); row < Math.Min(end, range.EndRow + 1); row++)
        {
            double left = double.PositiveInfinity, right = 0;
            foreach (TerminalCell cell in snapshot.GetCells(row).Span)
            {
                if (!Covers(range, row, cell)) continue;
                left = Math.Min(left, cell.Column * cellWidth + HorizontalInset);
                right = Math.Max(right, (cell.Column + cell.Width) * cellWidth + HorizontalInset);
            }
            if (!double.IsFinite(left)) continue;
            Rect visible = viewportClip.Rect;
            left = Math.Max(left, visible.Left);
            right = Math.Min(right, visible.Right);
            if (left >= right) continue;
            var a = PointToScreen(new Point(left, row * rowHeight));
            var b = PointToScreen(new Point(right, Math.Min((row + 1) * rowHeight, visible.Bottom)));
            result.AddRange([a.X, a.Y, b.X - a.X, b.Y - a.Y]);
        }
        return result.ToArray();
    }

    internal (int Row, int Offset) TextBoundary(Point point)
    {
        UpdateMetrics();
        return snapshot is { Count: > 0 } ? Boundary(point) : (0, 0);
    }

    public void ConfigureFont(string family, double points)
    {
        string actual = FontCatalog.Resolve(family);
        UsesFontFallback = !string.Equals(actual, family, StringComparison.OrdinalIgnoreCase);
        font = new FontFamily(actual);
        fontSize = points * 96 / 72;
        imeInput.FontFamily = font;
        imeInput.FontSize = fontSize;
        dpi = 0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetConnected(bool value)
    {
        connected = value;
        updateBlink();
        if (!value) CancelComposition();
        if (!value) CancelRemoteGesture(notify: true);
        InvalidateVisual();
    }

    public void SetSearch(TerminalMatch? value, IReadOnlyList<TerminalMatch> matches, bool scroll)
    {
        search = value;
        searchMatches = matches;
        if (scroll && value is { } current)
        {
            UpdateMetrics();
            var (first, _) = VisibleRows();
            if (current.StartRow < first || current.EndRow - current.StartRow + 1 > VisibleRowCount)
                Scroller.ScrollToVerticalOffset(current.StartRow * rowHeight);
            else if (current.EndRow >= first + VisibleRowCount)
                Scroller.ScrollToVerticalOffset((current.EndRow + 1 - VisibleRowCount) * rowHeight);
        }
        InvalidateVisual();
    }

    public void SetHighlights(IReadOnlyDictionary<int, TerminalHighlightSpan[]> value)
    {
        highlights = value;
        InvalidateVisual();
    }

    public (int First, int End) VisibleRows()
    {
        UpdateMetrics();
        int count = snapshot?.Count ?? 0;
        int first = Math.Clamp((int)Math.Round(VerticalOffset / rowHeight), 0, count);
        int end = Math.Min(count, first + (int)Math.Ceiling(VisibleGridHeight / rowHeight - 1e-8));
        return (first, end);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateMetrics();
        imeInput.Arrange(CellRect(snapshot?.CursorRow ?? 0, snapshot?.CursorColumn ?? 0, 1));
        UpdateViewport();
        return finalSize;
    }

    private static int FittedCells(double available, double cellSize) =>
        (int)Math.Max(0, Math.Floor(available / cellSize + 1e-8));

    private int VisibleRowCount => FittedCells(ViewportHeight, rowHeight);
    // Leave the fractional height black at the end. In history it can show part of the next row.
    private double VisibleGridHeight => FollowsEnd ? VisibleRowCount * rowHeight : ViewportHeight;
    private static bool AtEnd(double offset, double limit) => offset >= limit - 0.01;

    private void UpdateViewport()
    {
        int columns = FittedCells(Scroller.ViewportWidth - 2 * HorizontalInset, cellWidth);
        double y = FittedOffset(Scroller.VerticalOffset, Scroller.ScrollableHeight, rowHeight);
        double x = FittedOffset(Scroller.HorizontalOffset, Scroller.ScrollableWidth, cellWidth);
        viewportTranslation.X = Scroller.HorizontalOffset - x;
        viewportTranslation.Y = Scroller.VerticalOffset - y;
        viewportClip.Rect = new Rect(HorizontalInset + x, y, columns * cellWidth, VisibleGridHeight);
        Clip = viewportClip;

        static double FittedOffset(double offset, double limit, double cellSize)
        {
            double units = offset / cellSize;
            return (AtEnd(offset, limit) ? Math.Ceiling(units - 1e-8) : Math.Floor(units + 1e-8)) * cellSize;
        }
    }

    public (int Columns, int Rows) ViewportGrid(double width, double height)
    {
        UpdateMetrics();
        return (Math.Clamp(FittedCells(width - 2 * HorizontalInset, cellWidth), 2, 1024),
            Math.Clamp(FittedCells(height, rowHeight), 1, 512));
    }

    public Size PaneMinimum()
    {
        UpdateMetrics();
        return new Size(Math.Max(160, cellWidth * 2 + HorizontalInset * 2) + SystemParameters.VerticalScrollBarWidth,
            Math.Max(96, rowHeight) + SystemParameters.HorizontalScrollBarHeight);
    }

    public void RevealCursor()
    {
        if (snapshot is not { } view) return;
        UpdateMetrics();
        var (first, _) = VisibleRows();
        if (view.CursorRow < first) Scroller.ScrollToVerticalOffset(view.CursorRow * rowHeight);
        else if (view.CursorRow >= first + VisibleRowCount)
            Scroller.ScrollToVerticalOffset((view.CursorRow + 1 - VisibleRowCount) * rowHeight);
        int firstColumn = (int)Math.Round((Scroller.HorizontalOffset - viewportTranslation.X) / cellWidth);
        int columns = FittedCells(Scroller.ViewportWidth - 2 * HorizontalInset, cellWidth);
        if (view.CursorColumn < firstColumn) Scroller.ScrollToHorizontalOffset(view.CursorColumn * cellWidth);
        else if (view.CursorColumn >= firstColumn + columns)
            Scroller.ScrollToHorizontalOffset((view.CursorColumn + 1 - columns) * cellWidth);
    }

    public void ScrollToEnd()
    {
        outputScrollOffset = null;
        Scroller.ScrollToBottom();
    }

    public void Copy()
    {
        if (snapshot is null || selection is not { } range) return;
        var text = new StringBuilder();
        for (int row = range.StartRow; row <= range.EndRow; row++)
        {
            int first = row == range.StartRow ? range.StartOffset : 0;
            int end = row == range.EndRow ? range.EndOffset : snapshot[row].Length;
            text.Append(snapshot[row].AsSpan(first, end - first));
            if (row < range.EndRow && !snapshot.IsSoftWrapped(row)) text.AppendLine();
        }
        if (text.Length > 0)
        {
            try { Clipboard.SetText(text.ToString()); }
            catch (ExternalException) { }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateMetrics();
        imeInput.Measure(new Size(cellWidth, rowHeight));
        int count = Math.Max(1, Math.Max(snapshot?.Count ?? 0, (snapshot?.CursorRow ?? 0) + 1));
        return new Size((snapshot?.Columns ?? 80) * cellWidth + HorizontalInset * 2, count * rowHeight);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        dpi = 0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (!presentationEnabled) return;
        drawing.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        if (snapshot is null) return;
        var (first, end) = VisibleRows();
        // ScrollViewer applies a bottom request after laying out the new extent.
        // Wait for ScrollChanged instead of expanding and drawing the old range.
        if (outputScrollOffset == double.PositiveInfinity && (!FollowsEnd || end < snapshot.Count)) return;
        var matches = new Dictionary<int, List<TerminalMatch>>();
        foreach (TerminalMatch match in searchMatches)
        {
            for (int row = Math.Max(first, match.StartRow); row < Math.Min(end, match.EndRow + 1); row++)
            {
                if (!matches.TryGetValue(row, out var list)) matches[row] = list = [];
                list.Add(match);
            }
        }
        TerminalCell? cursorCell = null;
        TerminalStyleFlags cursorFlags = default;
        uint cursorBackground = 0;
        var styledCells = new List<(TerminalCell Cell, uint Foreground, TerminalStyleFlags Flags, bool Current)>();
        for (int row = first; row < end; row++)
        {
            // Share horizontal cell translations across rows, including on the first view of a long scrollback.
            var rowPosition = new TranslateTransform(0, row * rowHeight);
            rowPosition.Freeze();
            drawing.PushTransform(rowPosition);
            highlights.TryGetValue(row, out var rules);
            matches.TryGetValue(row, out var found);
            int backgroundStart = 0, backgroundEnd = 0;
            uint runBackground = 0;
            styledCells.Clear();
            foreach (TerminalCell cell in snapshot.GetCells(row).Span)
            {
                TerminalStyle style = cell.Style;
                uint foreground = style.Foreground ?? 0xFFFFFF;
                uint background = style.Background ?? 0;
                var flags = style.Flags;
                if (flags.HasFlag(TerminalStyleFlags.Inverse)) (foreground, background) = (background, foreground);
                if (flags.HasFlag(TerminalStyleFlags.Faint)) foreground = (foreground >> 1) & 0x7F7F7F;
                if (rules is not null)
                {
                    foreach (TerminalHighlightSpan rule in rules)
                    {
                        if (rule.Start >= cell.TextOffset + cell.Text.Length || rule.End <= cell.TextOffset) continue;
                        foreground = rule.Style.Foreground ?? foreground;
                        background = rule.Style.Background ?? background;
                        if (rule.Style.Bold is { } bold) flags = Set(flags, TerminalStyleFlags.Bold, bold);
                        if (rule.Style.Underline is { } underline) flags = Set(flags, TerminalStyleFlags.Underline, underline);
                        break;
                    }
                }
                bool current = search is { } item && Covers(item, row, cell);
                if (current || (found?.Any(match => Covers(match, row, cell)) ?? false))
                    (foreground, background) = (0, 0xFFD54F);
                if (selection is { } selected && Covers(selected, row, cell))
                    (foreground, background) = (0xFFFFFF, 0x0050A0);
                if (row == snapshot.CursorRow && snapshot.CursorColumn >= cell.Column &&
                    snapshot.CursorColumn < cell.Column + cell.Width)
                {
                    cursorCell = cell;
                    cursorFlags = flags;
                    cursorBackground = background;
                }
                if (background != runBackground || cell.Column != backgroundEnd)
                {
                    PaintBackground();
                    backgroundStart = cell.Column;
                    runBackground = background;
                }
                backgroundEnd = cell.Column + cell.Width;
                styledCells.Add((cell, foreground, flags, current));
            }
            PaintBackground();
            foreach (var (cell, foreground, flags, current) in styledCells)
            {
                Rect rect = CellRect(0, cell.Column, cell.Width);
                if (!flags.HasFlag(TerminalStyleFlags.Conceal)) DrawGlyph(drawing, rect, cell.Text, foreground, flags);
                if (current) drawing.DrawRectangle(null, new Pen(Brushes.White, 1), rect);
            }
            drawing.Pop();

            void PaintBackground()
            {
                if (runBackground != 0)
                    drawing.DrawRectangle(BrushFor(runBackground), null,
                        CellRect(0, backgroundStart, backgroundEnd - backgroundStart));
            }
        }
        // The cursor can be on a blank row beyond the retained text.
        double cursorTop = snapshot.CursorRow * rowHeight;
        if (connected && composition.Length > 0 && cursorTop < viewportClip.Rect.Bottom &&
            cursorTop + rowHeight > viewportClip.Rect.Top)
        {
            Rect caret = CellRect(snapshot.CursorRow, cursorCell?.Column ?? snapshot.CursorColumn, 1);
            var text = Glyph(composition, 0xFFFFFF, TerminalStyleFlags.Underline);
            Rect overlay = new(caret.TopLeft, new Size(text.WidthIncludingTrailingWhitespace + 2, rowHeight));
            drawing.DrawRectangle(Brushes.Black, new Pen(Brushes.White, 1), overlay);
            drawing.DrawText(text, overlay.TopLeft);
        }
        else if (connected && snapshot.CursorVisible && cursorOn && snapshot.CursorRow >= first && snapshot.CursorRow < end)
        {
            Rect caret = CellRect(snapshot.CursorRow, cursorCell?.Column ?? snapshot.CursorColumn, cursorCell?.Width ?? 1);
            uint cursor = ContrastCursor(cursorBackground);
            if (IsKeyboardFocusWithin)
            {
                Rect shape = snapshot.CursorShape switch
                {
                    TerminalCursorShape.Underline => new Rect(caret.X, caret.Bottom - 2, caret.Width, 2),
                    TerminalCursorShape.Bar => new Rect(caret.X, caret.Y, 2, caret.Height),
                    _ => caret,
                };
                drawing.DrawRectangle(BrushFor(cursor), null, shape);
                if (snapshot.CursorShape == TerminalCursorShape.Block && cursorCell is { } cell &&
                    !cursorFlags.HasFlag(TerminalStyleFlags.Conceal))
                    DrawGlyph(drawing, caret, cell.Text, 0xFFFFFF ^ cursor, cursorFlags);
            }
            else drawing.DrawRectangle(null, new Pen(BrushFor(cursor), 1), caret);
        }
        renderedSnapshotVersion = snapshotVersion;
        ViewportRendered?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || remoteGesture) return;
        Focus();
        if (snapshot is not { Count: > 0 }) return;
        dragStart = Boundary(e.GetPosition(this));
        selection = null;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (CanReportMouse() && dragStart is null &&
            (remoteGesture || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            Point point = e.GetPosition(this);
            if (snapshot!.Mouse.Tracking is 1002 or 1003 && (remoteGesture || IsActiveScreenPoint(point)))
                ReportMouse(point, remoteGesture ? remoteButtons[^1] : 3, release: false, motion: true);
            return;
        }
        if (dragStart is not { } start || snapshot is not { Count: > 0 } || !IsMouseCaptured) return;
        var end = Boundary(e.GetPosition(this));
        if (end.CompareTo(start) < 0) (start, end) = (end, start);
        selection = new TerminalMatch(start.Row, start.Offset, end.Row, end.Offset);
        if (UIElementAutomationPeer.FromElement(this) is TerminalAutomationPeer automation) automation.SelectionChanged();
        Point view = e.GetPosition(Scroller);
        if (view.Y < 0) Scroller.LineUp();
        if (view.Y > ViewportHeight) Scroller.LineDown();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragStart is null) return;
        dragStart = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (CanReportMouse() && IsActiveScreenPoint(point) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int count = Math.Max(1, Math.Abs(e.Delta) / 120);
            for (int index = 0; index < count; index++)
                ReportMouse(point, e.Delta > 0 ? 4 : 5, release: false, motion: false);
            e.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    private bool CanReportMouse() => connected && snapshot is { Mouse.Tracking: > 0 } view &&
        (remoteGesture || view.IsAlternateScreen || FollowsEnd ||
            VisibleRows().First >= view.ScreenStartRow);

    private bool IsActiveScreenPoint(Point point) => snapshot is { } view && viewportClip.Rect.Contains(point) &&
        point.Y >= view.ScreenStartRow * rowHeight;

    private void ReportMouse(Point point, int button, bool release, bool motion)
    {
        if (snapshot is not { } view) return;
        UpdateMetrics();
        int column = (int)Math.Floor((point.X - HorizontalInset) / cellWidth);
        int row = (int)Math.Floor(point.Y / rowHeight) - view.ScreenStartRow;
        if (UnclampedMouse?.Invoke() != true)
        {
            column = Math.Clamp(column, 0, view.Columns - 1);
            row = Math.Clamp(row, 0, view.ScreenRows - 1);
        }
        if (motion && lastMouseCell == (button, column, row)) return;
        lastMouseCell = (button, column, row);
        ModifierKeys current = Keyboard.Modifiers;
        TerminalModifiers modifiers = TerminalModifiers.None;
        if (current.HasFlag(ModifierKeys.Shift)) modifiers |= TerminalModifiers.Shift;
        if (current.HasFlag(ModifierKeys.Alt)) modifiers |= TerminalModifiers.Alt;
        if (current.HasFlag(ModifierKeys.Control)) modifiers |= TerminalModifiers.Control;
        MouseInput?.Invoke(new TerminalMouseInput(button, column, row, release, motion, modifiers, view.Mouse.Generation));
    }

    private static int Button(MouseButton button) => button switch
    {
        MouseButton.Left => 0, MouseButton.Middle => 1, MouseButton.Right => 2, _ => -1,
    };

    private (int Row, int Offset) Boundary(Point point)
    {
        int row = Math.Clamp((int)Math.Floor(point.Y / rowHeight), 0, snapshot!.Count - 1);
        double column = (point.X - HorizontalInset) / cellWidth;
        foreach (TerminalCell cell in snapshot.GetCells(row).Span)
        {
            if (column < cell.Column + cell.Width / 2.0) return (row, cell.TextOffset);
        }
        return (row, snapshot[row].Length);
    }

    private void UpdateMetrics()
    {
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (pixels == dpi) return;
        dpi = pixels;
        glyphs.Clear();
        cellTranslations = [];
        translationMemory.Resize(0);
        var measure = Glyph("M", 0xFFFFFF, 0);
        cellWidth = Math.Max(1, measure.WidthIncludingTrailingWhitespace);
        rowHeight = Math.Max(1, Math.Ceiling(measure.Height));
    }

    private Rect CellRect(int row, int column, int width) =>
        new(HorizontalInset + column * cellWidth, row * rowHeight, width * cellWidth, rowHeight);

    private void DrawGlyph(DrawingContext drawing, Rect cell, string text, uint foreground, TerminalStyleFlags flags)
    {
        if (text == " ") return;
        var key = (text, foreground, flags);
        if (!glyphs.TryGetValue(key, out GlyphEntry? entry))
        {
            FormattedText formatted = Glyph(text, foreground, flags);
            if (!glyphs.TryGetValue(key, out entry))
            {
                drawing.PushTransform(CellTranslation(cell.TopLeft));
                drawing.DrawDrawing(BuildCellDrawing(formatted, cell.Width, cell.Height));
                drawing.Pop();
                return;
            }
        }
        bool wide = cell.Width > cellWidth;
        DrawingGroup layout = wide
            ? entry.Wide ??= BuildCellDrawing(entry.Text, cell.Width, cell.Height)
            : entry.Narrow ??= BuildCellDrawing(entry.Text, cell.Width, cell.Height);
        drawing.PushTransform(CellTranslation(cell.TopLeft));
        drawing.DrawDrawing(layout);
        drawing.Pop();
    }

    private TranslateTransform CellTranslation(Point position)
    {
        if (cellTranslations.TryGetValue(position, out var cached)) return cached;
        if (cellTranslations.Count >= 8192)
        {
            cellTranslations = [];
            translationMemory.Resize(0);
        }
        bool cache = true;
        try { translationMemory.Add(256); }
        catch (ResourceLimitException)
        {
            cellTranslations = [];
            translationMemory.Resize(0);
            cache = false;
        }
        var transform = new TranslateTransform(position.X, position.Y);
        transform.Freeze();
        if (cache) cellTranslations.Add(position, transform);
        return transform;
    }

    private static DrawingGroup BuildCellDrawing(FormattedText text, double width, double height)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            double measured = text.WidthIncludingTrailingWhitespace;
            if (measured > width) context.PushTransform(new ScaleTransform(width / measured, 1));
            context.DrawText(text, new Point());
            if (measured > width) context.Pop();
            context.Pop();
        }
        drawing.Freeze();
        return drawing;
    }

    private FormattedText Glyph(string text, uint foreground, TerminalStyleFlags flags)
    {
        var key = (text, foreground, flags);
        if (glyphs.TryGetValue(key, out var cached)) return cached.Text;
        if (glyphs.Count >= 4096) glyphs.Clear();
        ResourceReservation? memory;
        try { memory = ApplicationResources.LayoutCache.Reserve(8192 + text.Length * 32L); }
        catch (ResourceLimitException)
        {
            glyphs.Clear();
            memory = null;
        }
        if (text.Length != 0 && text[0] >= 128 &&
            NativeTerminalCore.HasDrawingMark(text.EnumerateRunes().First()))
            text = "\u25CC" + text;
        var typeface = new Typeface(font, flags.HasFlag(TerminalStyleFlags.Italic) ? FontStyles.Italic : FontStyles.Normal,
            flags.HasFlag(TerminalStyleFlags.Bold) ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var glyph = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, fontSize, BrushFor(foreground), dpi);
        if (flags.HasFlag(TerminalStyleFlags.Underline)) glyph.SetTextDecorations(TextDecorations.Underline);
        if (flags.HasFlag(TerminalStyleFlags.Strike)) glyph.SetTextDecorations(TextDecorations.Strikethrough);
        if (memory is not null) glyphs.Add(key, new GlyphEntry(glyph, memory));
        return glyph;
    }

    private Brush BrushFor(uint color)
    {
        if (color == 0) return Brushes.Black;
        if (color == 0xFFFFFF) return Brushes.White;
        if (brushes.TryGetValue(color, out Brush? cached)) return cached;
        if (brushes.Count >= 512) { brushes.Clear(); brushMemory.Resize(0); }
        bool cache = true;
        try { brushMemory.Add(256); }
        catch (ResourceLimitException) { brushes.Clear(); brushMemory.Resize(0); cache = false; }
        var brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        if (cache) brushes.Add(color, brush);
        return brush;
    }

    private sealed record GlyphEntry(FormattedText Text, ResourceReservation Memory)
    {
        public DrawingGroup? Narrow { get; set; }
        public DrawingGroup? Wide { get; set; }
    }

    private static bool Covers(TerminalMatch match, int row, TerminalCell cell) =>
        row >= match.StartRow && row <= match.EndRow &&
        (row != match.StartRow || cell.TextOffset + cell.Text.Length > match.StartOffset) &&
        (row != match.EndRow || cell.TextOffset < match.EndOffset);

    private static TerminalStyleFlags Set(TerminalStyleFlags flags, TerminalStyleFlags bit, bool enabled) =>
        enabled ? flags | bit : flags & ~bit;

    internal static uint ContrastCursor(uint rgb)
    {
        double luminance = 0.2126 * Linear(rgb >> 16) + 0.7152 * Linear(rgb >> 8) + 0.0722 * Linear(rgb);
        double adjusted = luminance + 0.05;
        return adjusted / 0.05 >= 1.05 / adjusted ? 0u : 0xFFFFFFu;

        static double Linear(uint component)
        {
            double srgb = (component & 255) / 255.0;
            return srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }
    }

    private void ShowComposition(TextCompositionEventArgs e)
    {
        if (ReferenceEquals(discardedComposition, e.TextComposition)) return;
        terminatingComposition = null;
        activeComposition = e.TextComposition;
        composition = e.TextComposition.CompositionText;
        updateBlink();
        InvalidateVisual();
    }

    public void CancelComposition()
    {
        if (activeComposition is not { } composing) return;
        // WPF can deliver a composition result after the focus change has finished.
        discardedComposition = composing;
        terminatingComposition = composing;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (ReferenceEquals(terminatingComposition, composing)) terminatingComposition = null;
        }));
        try { composing.Complete(); }
        catch (ArgumentException) when (ReferenceEquals(activeComposition, composing)) { }
        finally { ClearComposition(); }
    }

    private void ClearComposition()
    {
        bool wasComposing = HasComposition;
        activeComposition = null;
        composition = "";
        ScheduleImeCleanup();
        if (!wasComposing) return;
        updateBlink();
        InvalidateVisual();
    }

    private void ScheduleImeCleanup()
    {
        if (HasComposition || imeCleanupScheduled || imeInput.Text.Length == 0) return;
        // WPF replays IME edits and events as a batch. Clear the input only after it finishes.
        imeCleanupScheduled = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            imeCleanupScheduled = false;
            if (!HasComposition && imeInput.Text.Length != 0) imeInput.Clear();
        }));
    }

    private readonly Action updateBlink;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetCaretBlinkTime();
}
