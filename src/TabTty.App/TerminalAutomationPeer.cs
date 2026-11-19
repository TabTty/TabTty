using System.Text;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using TabTty.Core;

namespace TabTty.App;

internal sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    private const long SnapshotBudget = 64 * 1024 * 1024;
    private static long snapshotBytes;
    private static readonly ConditionalWeakTable<TerminalSnapshot, Document> Documents = new();
    private static readonly Document EmptyDocument = new(null);
    private readonly TerminalSurface surface;
    private readonly List<WeakReference<TextRange>> ranges = [];
    private Document document;

    public TerminalAutomationPeer(TerminalSurface owner) : base(owner)
    {
        surface = owner;
        document = owner.Snapshot is { } view ? DocumentFor(view) : EmptyDocument;
    }

    protected override string GetClassNameCore() => "TabTty.Terminal";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => AutomationProperties.GetName(surface) is { Length: > 0 } name
        ? name : App.Text("Terminal");
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool HasKeyboardFocusCore() => surface.IsKeyboardFocusWithin;
    protected override void SetFocusCore() => surface.Focus();
    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Text ? this : base.GetPattern(patternInterface);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider DocumentRange => OnUi(() => Range(0, document.Length));
    public ITextRangeProvider[] GetSelection() => OnUi(() => new ITextRangeProvider[]
    {
        surface.Selection is { } selection ? Range(document.Offset(selection.StartRow, selection.StartOffset),
            document.Offset(selection.EndRow, selection.EndOffset)) : Caret(),
    });
    public ITextRangeProvider[] GetVisibleRanges() => OnUi(() =>
    {
        var (first, end) = surface.VisibleRows();
        return Enumerable.Range(first, end - first).Select(row =>
            (ITextRangeProvider)Range(document.Offset(row, 0), document.Offset(row, document.View![row].Length))).ToArray();
    });
    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        throw new ArgumentException("The terminal has no text-provider children.", nameof(childElement));
    public ITextRangeProvider RangeFromPoint(Point screenLocation) => OnUi(() =>
    {
        var point = surface.TextBoundary(surface.PointFromScreen(screenLocation));
        int offset = document.Offset(point.Row, point.Offset);
        return (ITextRangeProvider)Range(offset, offset);
    });
    public void SetDocument(TerminalSnapshot updated)
    {
        Document previous = document;
        var next = DocumentFor(updated);
        foreach (var reference in ranges)
        {
            if (!reference.TryGetTarget(out TextRange? range)) continue;
            if (previous.View is not { Count: > 0 } old)
            {
                range.Start = range.End = 0;
                continue;
            }
            TerminalMatch before = previous.Match(range.Start, range.End);
            TerminalMatch? mapped = updated.MapMatchFrom(old, before);
            if (mapped is { } valid)
            {
                range.Start = next.Offset(valid.StartRow, valid.StartOffset);
                range.End = next.Offset(valid.EndRow, valid.EndOffset);
            }
            else
            {
                var start = updated.MapMatchFrom(old, before with { EndRow = before.StartRow, EndOffset = before.StartOffset });
                var end = updated.MapMatchFrom(old, before with { StartRow = before.EndRow, StartOffset = before.EndOffset });
                range.Start = start is { } a ? next.Offset(a.StartRow, a.StartOffset) : 0;
                range.End = end is { } b ? next.Offset(b.EndRow, b.EndOffset) : range.Start;
                if (range.End < range.Start) range.End = range.Start;
            }
        }
        ranges.RemoveAll(reference => !reference.TryGetTarget(out _));
        document = next;
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    }

    public void SelectionChanged() => RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);

    private TextRange Caret()
    {
        TerminalSnapshot? view = document.View;
        if (view is null || view.Count == 0) return Range(0, 0);
        int row = Math.Min(view.CursorRow, view.Count - 1);
        int offset = view[row].Length;
        foreach (TerminalCell cell in view.GetCells(row).Span)
            if (view.CursorColumn <= cell.Column)
            {
                offset = cell.TextOffset;
                break;
            }
        int position = document.Offset(row, offset);
        return Range(position, position);
    }

    private TextRange Range(int start, int end)
    {
        ranges.RemoveAll(reference => !reference.TryGetTarget(out _));
        if (ranges.Count >= 4096) throw new InvalidOperationException("The terminal text-range budget is exhausted.");
        var result = new TextRange(this, Math.Clamp(start, 0, document.Length), Math.Clamp(end, 0, document.Length));
        ranges.Add(new WeakReference<TextRange>(result));
        return result;
    }

    private T OnUi<T>(Func<T> action) => Dispatcher.CheckAccess() ? action() : Dispatcher.Invoke(action);
    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }

    private string ReadText(int start, int end, int maximum)
    {
        if (maximum < -1) throw new ArgumentOutOfRangeException(nameof(maximum));
        int count = Math.Min(end - start, maximum < 0 ? end - start : maximum);
        long reservation = (long)count * sizeof(char) + 64;
        long owned = Interlocked.Add(ref snapshotBytes, reservation);
        try
        {
            if (owned > SnapshotBudget) throw new InvalidOperationException("The UI Automation snapshot budget is exhausted.");
            return document.Read(start, count);
        }
        finally { Interlocked.Add(ref snapshotBytes, -reservation); }
    }

    private static Document DocumentFor(TerminalSnapshot view) => Documents.GetValue(view, static value => new Document(value));

    private sealed class Document
    {
        private readonly int[] starts;
        public TerminalSnapshot? View { get; }
        public int Length { get; }

        public Document(TerminalSnapshot? view)
        {
            View = view;
            starts = new int[view?.Count ?? 0];
            long length = 0;
            for (int row = 0; row < starts.Length; row++)
            {
                starts[row] = checked((int)length);
                length += view![row].Length;
                if (row + 1 < starts.Length && !view.IsSoftWrapped(row)) length++;
            }
            Length = checked((int)length);
        }

        public int Offset(int row, int offset) => starts.Length == 0 ? 0 : row >= starts.Length ? Length :
            starts[Math.Max(0, row)] + Math.Clamp(offset, 0, View![Math.Max(0, row)].Length);

        public (int Row, int Offset) Point(int offset)
        {
            if (starts.Length == 0) return (0, 0);
            offset = Math.Clamp(offset, 0, Length);
            int row = Array.BinarySearch(starts, offset);
            if (row < 0) row = Math.Max(0, ~row - 1);
            while (row + 1 < starts.Length && starts[row + 1] == offset) row++;
            return (row, Math.Min(offset - starts[row], View![row].Length));
        }

        public TerminalMatch Match(int start, int end)
        {
            var first = Point(start);
            var last = Point(end);
            return new TerminalMatch(first.Row, first.Offset, last.Row, last.Offset);
        }

        public string Read(int start, int count) => string.Create(count, (Document: this, Start: start),
            static (destination, state) =>
            {
                Document document = state.Document;
                if (destination.IsEmpty || document.View is null) return;
                var point = document.Point(state.Start);
                int row = point.Row, offset = point.Offset;
                while (!destination.IsEmpty && row < document.View.Count)
                {
                    ReadOnlySpan<char> content = document.View[row].AsSpan(offset);
                    int length = Math.Min(content.Length, destination.Length);
                    content[..length].CopyTo(destination);
                    destination = destination[length..];
                    if (destination.IsEmpty) break;
                    if (row + 1 < document.View.Count && !document.View.IsSoftWrapped(row))
                    {
                        destination[0] = '\n';
                        destination = destination[1..];
                    }
                    row++;
                    offset = 0;
                }
            });
    }

    private sealed class TextRange : ITextRangeProvider
    {
        private readonly TerminalAutomationPeer peer;
        internal int Start;
        internal int End;
        public TextRange(TerminalAutomationPeer peer, int start, int end)
        {
            this.peer = peer;
            Start = start;
            End = end;
        }
        public ITextRangeProvider Clone() => peer.OnUi(() => peer.Range(Start, End));
        public bool Compare(ITextRangeProvider range) => peer.OnUi(() =>
            range is TextRange other && other.peer == peer && other.Start == Start && other.End == End);
        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
            TextPatternRangeEndpoint targetEndpoint) => peer.OnUi(() =>
            Position(endpoint).CompareTo(Other(targetRange).Position(targetEndpoint)));
        public void ExpandToEnclosingUnit(TextUnit unit) => peer.OnUi(() =>
        {
            string text = peer.ReadText(0, peer.document.Length, -1);
            (Start, End) = Enclosing(text, Start, unit);
        });
        public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) =>
            Equals(GetAttributeValue(attributeId), value) ? Clone() : null;
        public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase) => peer.OnUi(() =>
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length == 0) return null;
            string value = peer.ReadText(Start, End, -1);
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int index = backward ? value.LastIndexOf(text, comparison) : value.IndexOf(text, comparison);
            return index < 0 ? null : (ITextRangeProvider)peer.Range(Start + index, Start + index + text.Length);
        });
        public object GetAttributeValue(int attributeId) => peer.OnUi(() =>
            attributeId == TextPatternIdentifiers.IsReadOnlyAttribute.Id ? (object)true :
            attributeId == TextPatternIdentifiers.FontNameAttribute.Id ? peer.surface.AutomationFontFamily :
            attributeId == TextPatternIdentifiers.FontSizeAttribute.Id ? peer.surface.AutomationFontPoints :
            AutomationElement.NotSupported);
        public double[] GetBoundingRectangles() => peer.OnUi(() =>
            peer.surface.TextRectangles(peer.document.Match(Start, End)));
        public IRawElementProviderSimple GetEnclosingElement() => peer.OnUi(() => peer.ProviderFromPeer(peer));
        public string GetText(int maxLength) => peer.OnUi(() => peer.ReadText(Start, End, maxLength));
        public int Move(TextUnit unit, int count) => peer.OnUi(() =>
        {
            string text = peer.ReadText(0, peer.document.Length, -1);
            bool empty = Start == End;
            (int first, _) = Enclosing(text, Start, unit);
            int actual = MovePoint(text, ref first, unit, count);
            Start = first;
            End = empty ? first : Enclosing(text, first, unit).End;
            return actual;
        });
        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) => peer.OnUi(() =>
        {
            string text = peer.ReadText(0, peer.document.Length, -1);
            int point = Position(endpoint);
            int actual = MovePoint(text, ref point, unit, count);
            Set(endpoint, point);
            return actual;
        });
        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
            TextPatternRangeEndpoint targetEndpoint) => peer.OnUi(() => Set(endpoint, Other(targetRange).Position(targetEndpoint)));
        public void Select() => peer.OnUi(() =>
        {
            peer.surface.AutomationSelect(peer.document.Match(Start, End));
            peer.SelectionChanged();
        });
        public void AddToSelection() => Select();
        public void RemoveFromSelection() => peer.OnUi(() =>
        {
            peer.surface.AutomationSelect(null);
            peer.SelectionChanged();
        });
        public void ScrollIntoView(bool alignToTop) => peer.OnUi(() =>
            peer.surface.ScrollTextRange(peer.document.Match(Start, End), alignToTop));
        public IRawElementProviderSimple[] GetChildren() => [];

        private int Position(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? Start : End;
        private TextRange Other(ITextRangeProvider range) => range is TextRange other && other.peer == peer
            ? other : throw new ArgumentException("Text ranges belong to different terminals.", nameof(range));
        private void Set(TextPatternRangeEndpoint endpoint, int value)
        {
            if (endpoint == TextPatternRangeEndpoint.Start)
            {
                Start = value;
                End = Math.Max(End, Start);
            }
            else
            {
                End = value;
                Start = Math.Min(Start, End);
            }
        }

        private (int Start, int End) Enclosing(string text, int point, TextUnit unit)
        {
            if (text.Length == 0) return (0, 0);
            point = Math.Min(point, text.Length - 1);
            if (unit is TextUnit.Document or TextUnit.Format) return (0, text.Length);
            if (unit == TextUnit.Page)
            {
                var (first, end) = peer.surface.VisibleRows();
                return (peer.document.Offset(first, 0), end < (peer.document.View?.Count ?? 0)
                    ? peer.document.Offset(end, 0) : text.Length);
            }
            if (unit is TextUnit.Line or TextUnit.Paragraph)
            {
                int first = point == 0 ? 0 : text.LastIndexOf('\n', point - 1) + 1;
                int last = text.IndexOf('\n', point);
                return (first, last < 0 ? text.Length : last + 1);
            }
            if (unit == TextUnit.Word)
            {
                bool white = char.IsWhiteSpace(text[point]);
                int first = point, last = point + 1;
                while (first > 0 && char.IsWhiteSpace(text[first - 1]) == white) first--;
                while (last < text.Length && char.IsWhiteSpace(text[last]) == white) last++;
                return (first, last);
            }
            var (row, offset) = peer.document.Point(point);
            var (start, spanEnd) = peer.document.View!.StoredSpanAt(row, offset);
            int absolute = peer.document.Offset(row, start);
            return spanEnd == start ? (absolute, absolute + 1) : (absolute, peer.document.Offset(row, spanEnd));
        }

        private int MovePoint(string text, ref int point, TextUnit unit, int count)
        {
            int actual = 0;
            int direction = Math.Sign(count);
            for (long remaining = Math.Abs((long)count); remaining > 0; remaining--)
            {
                int previous = point;
                if (direction > 0)
                {
                    if (point >= text.Length) break;
                    point = Enclosing(text, point, unit).End;
                }
                else
                {
                    if (point <= 0) break;
                    point = Enclosing(text, point - 1, unit).Start;
                }
                if (point == previous) break;
                actual += direction;
            }
            return actual;
        }
    }
}
