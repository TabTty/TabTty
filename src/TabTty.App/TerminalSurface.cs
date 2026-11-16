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
    private readonly TextBox imeInput;
    private readonly VisualCollection visuals;
    private readonly DispatcherTimer blink;
    private bool presentationEnabled = true;
    private bool cursorOn = true;
    private double fontSize = 16;
    private const double Inset = 4;
    private double? outputScrollOffset;

    public ScrollViewer Scroller { get; }
    internal TerminalSnapshot? Snapshot => snapshot;
    internal bool IsCurrentSnapshotRendered => snapshot is not null && snapshotVersion == renderedSnapshotVersion;
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
    public double VerticalOffset => Scroller.VerticalOffset;
    public double ExtentHeight => Scroller.ExtentHeight;
    public double ViewportHeight => Scroller.ViewportHeight;

    public TerminalSurface()
    {
        Focusable = true;
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
                IsKeyboardFocusWithin && snapshot is { CursorVisible: true, CursorBlink: true };
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
        Scroller.ScrollChanged += (_, change) =>
        {
            InvalidateVisual();
            if (change.VerticalChange != 0)
            {
                bool automatic = outputScrollOffset is { } offset &&
                    Math.Abs(change.VerticalOffset - Math.Min(offset, Scroller.ScrollableHeight)) < 0.01;
                outputScrollOffset = null;
                ViewportChanged?.Invoke(automatic);
            }
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, e) =>
        {
            discardedComposition = null;
            ShowComposition(e);
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, e) => ShowComposition(e));
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, e) =>
        {
            if (ReferenceEquals(discardedComposition, e.TextComposition))
            {
                discardedComposition = null;
                e.Handled = true;
            }
            ClearComposition();
        });
        imeInput.TextChanged += (_, _) =>
        {
            if (composition.Length == 0 && imeInput.Text.Length != 0) imeInput.Clear();
        };
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
        bool follow = VerticalOffset >= ExtentHeight - ViewportHeight - 1;
        UpdateMetrics();
        double position = Math.Max(0, (VerticalOffset - Inset) / rowHeight);
        int row = (int)position;
        int? anchor = !follow && snapshot is { } previous ? value.MapViewportRowFrom(previous, row) : null;
        SetSnapshot(value);
        if (follow && (value.Count != previousCount || VerticalOffset < ExtentHeight - ViewportHeight))
        {
            outputScrollOffset = double.PositiveInfinity;
            Scroller.ScrollToBottom();
        }
        else if (anchor is { } mapped && mapped != row)
        {
            double offset = mapped == 0 ? 0 : Inset + (mapped + position - row) * rowHeight;
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
        Scroller.ScrollToVerticalOffset(top ? row * rowHeight + Inset : row * rowHeight + Inset - ViewportHeight);
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
                left = Math.Min(left, cell.Column * cellWidth + Inset);
                right = Math.Max(right, (cell.Column + cell.Width) * cellWidth + Inset);
            }
            if (!double.IsFinite(left)) continue;
            var a = PointToScreen(new Point(left, Math.Max(VerticalOffset, row * rowHeight + Inset)));
            var b = PointToScreen(new Point(right, Math.Min(VerticalOffset + ViewportHeight, (row + 1) * rowHeight + Inset)));
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
            double top = current.StartRow * rowHeight + Inset;
            double bottom = (current.EndRow + 1) * rowHeight + Inset;
            if (top < VerticalOffset || bottom - top > ViewportHeight)
                Scroller.ScrollToVerticalOffset(top);
            else if (bottom > VerticalOffset + ViewportHeight)
                Scroller.ScrollToVerticalOffset(bottom - ViewportHeight);
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
        int first = Math.Clamp((int)((VerticalOffset - Inset) / rowHeight), 0, count);
        int end = Math.Clamp((int)Math.Ceiling((VerticalOffset + ViewportHeight) / rowHeight) + 1, first, count);
        return (first, end);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateMetrics();
        imeInput.Arrange(CellRect(snapshot?.CursorRow ?? 0, snapshot?.CursorColumn ?? 0, 1));
        return finalSize;
    }

    public (int Columns, int Rows) ViewportGrid(double width, double height)
    {
        UpdateMetrics();
        return (Math.Clamp((int)((width - Inset * 2) / cellWidth), 2, 1024),
            Math.Clamp((int)((height - Inset * 2) / rowHeight), 1, 512));
    }

    public Size PaneMinimum()
    {
        UpdateMetrics();
        return new Size(Math.Max(160, cellWidth * 2 + Inset * 2) + SystemParameters.VerticalScrollBarWidth,
            Math.Max(96, rowHeight + Inset * 2) + SystemParameters.HorizontalScrollBarHeight);
    }

    public void RevealCursor()
    {
        if (snapshot is not { } view) return;
        UpdateMetrics();
        double top = Inset + view.CursorRow * rowHeight;
        if (top < VerticalOffset) Scroller.ScrollToVerticalOffset(top);
        else if (top + rowHeight > VerticalOffset + ViewportHeight)
            Scroller.ScrollToVerticalOffset(top + rowHeight - ViewportHeight);
        double left = Inset + view.CursorColumn * cellWidth;
        double offset = Scroller.HorizontalOffset;
        if (left < offset) Scroller.ScrollToHorizontalOffset(left);
        else if (left + cellWidth > offset + Scroller.ViewportWidth)
            Scroller.ScrollToHorizontalOffset(left + cellWidth - Scroller.ViewportWidth);
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
        return new Size((snapshot?.Columns ?? 80) * cellWidth + Inset * 2, count * rowHeight + Inset * 2);
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
        var matches = new Dictionary<int, List<TerminalMatch>>();
        foreach (TerminalMatch match in searchMatches)
        {
            for (int row = Math.Max(first, match.StartRow); row < Math.Min(end, match.EndRow + 1); row++)
            {
                if (!matches.TryGetValue(row, out var list)) matches[row] = list = [];
                list.Add(match);
            }
        }
        for (int row = first; row < end; row++)
        {
            highlights.TryGetValue(row, out var rules);
            matches.TryGetValue(row, out var found);
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
                Rect rect = CellRect(row, cell.Column, cell.Width);
                if (background != 0) drawing.DrawRectangle(BrushFor(background), null, rect);
                if (!flags.HasFlag(TerminalStyleFlags.Conceal)) DrawGlyph(drawing, rect, cell.Text, foreground, flags);
                if (current) drawing.DrawRectangle(null, new Pen(Brushes.White, 1), rect);
            }
        }
        if (connected && snapshot.CursorVisible && cursorOn && snapshot.CursorRow >= first && snapshot.CursorRow <= end)
        {
            Rect caret = CellRect(snapshot.CursorRow, snapshot.CursorColumn, 1);
            uint background = 0;
            TerminalCell? content = null;
            if (snapshot.CursorRow < snapshot.Count)
            {
                foreach (TerminalCell cell in snapshot.GetCells(snapshot.CursorRow).Span)
                {
                    if (snapshot.CursorColumn < cell.Column || snapshot.CursorColumn >= cell.Column + cell.Width) continue;
                    background = cell.Style.Flags.HasFlag(TerminalStyleFlags.Inverse)
                        ? cell.Style.Foreground ?? 0xFFFFFF : cell.Style.Background ?? 0;
                    content = cell;
                    caret = CellRect(snapshot.CursorRow, cell.Column, cell.Width);
                    break;
                }
            }
            uint cursor = Luminance(background) >= 128 ? 0u : 0xFFFFFFu;
            if (IsKeyboardFocusWithin)
            {
                Rect shape = snapshot.CursorShape switch
                {
                    TerminalCursorShape.Underline => new Rect(caret.X, caret.Bottom - 2, caret.Width, 2),
                    TerminalCursorShape.Bar => new Rect(caret.X, caret.Y, 2, caret.Height),
                    _ => caret,
                };
                drawing.DrawRectangle(BrushFor(cursor), null, shape);
                if (snapshot.CursorShape == TerminalCursorShape.Block && content is { } cell &&
                    !cell.Style.Flags.HasFlag(TerminalStyleFlags.Conceal))
                    DrawGlyph(drawing, caret, cell.Text, 0xFFFFFF ^ cursor, cell.Style.Flags);
            }
            else drawing.DrawRectangle(null, new Pen(BrushFor(cursor), 1), caret);
            if (composition.Length > 0)
            {
                var text = Glyph(composition, 0xFFFFFF, TerminalStyleFlags.Underline);
                Rect overlay = new(caret.TopLeft, new Size(text.WidthIncludingTrailingWhitespace + 2, rowHeight));
                drawing.DrawRectangle(Brushes.Black, new Pen(Brushes.White, 1), overlay);
                drawing.DrawText(text, overlay.TopLeft);
            }
        }
        renderedSnapshotVersion = snapshotVersion;
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
        (remoteGesture || view.IsAlternateScreen || VerticalOffset >= ExtentHeight - ViewportHeight - 1 ||
            VisibleRows().First >= view.ScreenStartRow);

    private bool IsActiveScreenPoint(Point point) => snapshot is { } view && point.Y >= Inset + view.ScreenStartRow * rowHeight;

    private void ReportMouse(Point point, int button, bool release, bool motion)
    {
        if (snapshot is not { } view) return;
        UpdateMetrics();
        int column = (int)Math.Floor((point.X - Inset) / cellWidth);
        int row = (int)Math.Floor((point.Y - Inset) / rowHeight) - view.ScreenStartRow;
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
        int row = Math.Clamp((int)Math.Floor((point.Y - Inset) / rowHeight), 0, snapshot!.Count - 1);
        double column = (point.X - Inset) / cellWidth;
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
        new(Inset + column * cellWidth, Inset + row * rowHeight, width * cellWidth, rowHeight);

    private void DrawGlyph(DrawingContext drawing, Rect cell, string text, uint foreground, TerminalStyleFlags flags)
    {
        if (text == " ") return;
        var key = (text, foreground, flags);
        FormattedText formatted = Glyph(text, foreground, flags);
        if (!glyphs.TryGetValue(key, out GlyphEntry? entry))
        {
            drawing.PushTransform(CellTranslation(cell.TopLeft));
            drawing.DrawDrawing(BuildCellDrawing(formatted, cell.Width, cell.Height));
            drawing.Pop();
            return;
        }
        bool wide = cell.Width > cellWidth;
        DrawingGroup layout = wide
            ? entry.Wide ??= BuildCellDrawing(formatted, cell.Width, cell.Height)
            : entry.Narrow ??= BuildCellDrawing(formatted, cell.Width, cell.Height);
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
        if (text.EnumerateRunes().FirstOrDefault() is var rune &&
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.SpacingCombiningMark)
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

    private static int Luminance(uint rgb) => ((int)(rgb >> 16) * 30 + (int)((rgb >> 8) & 255) * 59 +
        (int)(rgb & 255) * 11) / 100;

    private void ShowComposition(TextCompositionEventArgs e)
    {
        if (ReferenceEquals(discardedComposition, e.TextComposition)) return;
        activeComposition = e.TextComposition;
        composition = e.TextComposition.CompositionText;
        InvalidateVisual();
    }

    public void CancelComposition()
    {
        if (activeComposition is not { } composing) return;
        // WPF can deliver a composition result after the focus change has finished.
        discardedComposition = composing;
        try { composing.Complete(); }
        catch (ArgumentException) when (ReferenceEquals(activeComposition, composing)) { }
        finally { ClearComposition(); }
    }

    private void ClearComposition()
    {
        activeComposition = null;
        if (composition.Length == 0) return;
        composition = "";
        InvalidateVisual();
    }

    private readonly Action updateBlink;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetCaretBlinkTime();
}
