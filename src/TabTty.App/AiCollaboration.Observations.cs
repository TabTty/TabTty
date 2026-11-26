using System.Diagnostics;
using System.Text;
using TabTty.Core;
using TabTty.AiTransport;
using System.Text.Json;
using static TabTty.Core.TerminalHistory;

namespace TabTty.App;

internal sealed partial class AiCollaboration
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;
    private readonly ResourceQuota screenMemory = new(64 * MiB, collectOnPressure: false);
    private readonly ResourceQuota observationWork = TerminalHistory.WorkQuota;
    private sealed record Region(int Row, int Column, int Rows, int Columns);
    private sealed record ScreenRow(int row, int first_column, int last_column, ScreenCell[] cells,
        TerminalHighlightSpan[]? highlights, long Id = 0, long Revision = 0);
    private sealed record ScreenCell(int column, int width, string text, uint foreground, uint background, int flags, bool blank);
    private sealed record ScreenCapture(AiTerminalState State, Region Region, ScreenRow[] Lines, long Ordinal,
        DateTimeOffset At, long Expires, string Id, string Observation, string? HighlightStatus, ResourceReservation Memory) : IDisposable
    {
        public void Dispose() => Memory.Dispose();
    }
    private sealed record ScreenCursor(string Capture, int Line, int Cell);
    private async Task<object> ReadStateAsync(Sharing share, Arguments args, CancellationToken token)
    {
        var peer = share.Peer;
        ScreenCapture capture;
        string captureKey;
        int first = 0, firstCell = 0;
        if (args.Has("cursor"))
        {
            args.Only(1);
            var cursor = Resolve<ScreenCursor>(peer, share, args.String("cursor"));
            captureKey = cursor.Capture;
            capture = Resolve<ScreenCapture>(peer, share, captureKey);
            first = cursor.Line;
            firstCell = cursor.Cell;
        }
        else
        {
            args.Only();
            Region? region = null;
            if (args.Has("region"))
            {
                var input = args.Object("region");
                input.Only();
                region = new Region(input.Integer("row"), input.Integer("column"),
                    input.Integer("rows"), input.Integer("columns"));
            }
            bool highlights = args.Boolean("include_highlights");
            if (peer.CapturesInProgress + peer.Handles.Values.Count(handle => handle.Value is ScreenCapture) >= 2)
                throw new ToolException("resource_limit");
            var memory = screenMemory.Reserve(0);
            peer.CapturesInProgress++;
            var control = share.Control;
            long completed = share.Completed;
            bool wasIdle = control?.Sending is null;
            try
            {
                var data = await share.Session.ObserveAiAsync((model, state) =>
                {
                    var requested = region ?? new Region(1, 1, state.Rows, state.Columns);
                    if (requested.Row - 1 + requested.Rows > state.Rows ||
                        requested.Column - 1 + requested.Columns > state.Columns)
                        throw new ToolException("invalid_request");
                    try { return (State: state, Region: requested, Lines: CaptureScreen(model, requested, memory, token)); }
                    catch (ScreenRegionTooLargeException)
                    {
                        throw new ToolException("region_required", new { rows = state.Rows, columns = state.Columns,
                            suggested_region = new { row = 1, column = 1, rows = 1, columns = Math.Min(80, state.Columns) } });
                    }
                }, token);
                string? highlightStatus = highlights ? "current" : null;
                if (highlights && !data.State.IsAlternate && share.Tab.HasAiHighlightRules)
                {
                    int overlayBytes = 0;
                    foreach (var (row, index) in data.Lines.Select((row, index) => (row, index)))
                    {
                        var overlay = share.Tab.CaptureAiHighlightRow(row.row);
                        if (overlay is { } cached && cached.Id == row.Id && cached.Revision == row.Revision &&
                            cached.Spans.Length <= 128 && overlayBytes + cached.Spans.Length * 128 <= 64 * KiB &&
                            memory.Size + cached.Spans.Length * 128 <= 16 * MiB)
                        {
                            int charged = cached.Spans.Length * 128;
                            memory.Add(charged);
                            overlayBytes += charged;
                            data.Lines[index] = row with { highlights = cached.Spans.ToArray() };
                        }
                        else highlightStatus = "pending";
                    }
                }
                long expires = Now + HandleLifetime;
                string observation = Store(peer, share, new Observation(data.State, control?.Id, completed,
                    wasIdle && control == share.Control && control?.Sending is null && completed == share.Completed), expires);
                capture = new ScreenCapture(data.State, data.Region, data.Lines, ++share.CaptureOrdinal, clock.GetUtcNow(),
                    expires, NewId(), observation, highlightStatus, memory);
                captureKey = Store(peer, share, capture, expires, keepObservation: observation);
            }
            catch { memory.Dispose(); throw; }
            finally { peer.CapturesInProgress--; }
        }

        var lines = new List<ScreenRow>();
        int bytes = 0, nextRow = first, nextCell = firstCell;
        while (nextRow < capture.Lines.Length && bytes < 326 * KiB)
        {
            ScreenRow row = capture.Lines[nextRow];
            int start = nextCell;
            int overlays = row.highlights is { Length: > 0 } spans
                ? spans.Length * 136 + 32 : 0;
            if (lines.Count > 0 && bytes + overlays + 300 > 326 * KiB) break;
            bytes += overlays + 160;
            while (nextCell < row.cells.Length)
            {
                int size = 140 + MirroredTextCost(row.cells[nextCell].text);
                if (bytes + size > 326 * KiB && (lines.Count > 0 || nextCell > start)) break;
                bytes += size;
                nextCell++;
            }
            if (start == nextCell && row.cells.Length > 0) break;
            ScreenCell[] cells = row.cells[start..nextCell];
            lines.Add(new ScreenRow(row.row, cells.FirstOrDefault()?.column ?? row.first_column,
                cells.LastOrDefault() is { } last ? last.column + last.width - 1 : row.last_column, cells,
                row.highlights));
            if (nextCell != row.cells.Length) break;
            nextRow++;
            nextCell = 0;
        }
        string? next = nextRow < capture.Lines.Length
            ? Store(peer, share, new ScreenCursor(captureKey, nextRow, nextCell), capture.Expires,
                keepCapture: captureKey, keepObservation: capture.Observation) : null;
        if (next is null) RemoveHandle(peer, captureKey);
        var state = capture.State;
        return Ok(share, new
        {
            source = state.IsAlternate ? "alternate" : "normal", rows = state.Rows, columns = state.Columns,
            cursor = new { row = state.CursorRow + 1, column = Math.Min(state.CursorColumn + 1, state.Columns),
                visible = state.CursorVisible, style = state.CursorStyle.ToString().ToLowerInvariant() },
            modes = new { application_cursor = state.ApplicationCursor, application_keypad = state.ApplicationKeypad,
                bracketed_paste = state.BracketedPaste, mouse_tracking = state.Mouse.Tracking,
                mouse_format = state.Mouse.SgrCoordinates ? "sgr" : "classic" },
            layout_epoch = state.LayoutEpoch, observation = capture.Observation, capture_id = capture.Id,
            capture_ordinal = capture.Ordinal, captured_at = capture.At, expires_in_ms = Math.Max(0, capture.Expires - Now),
            requested_region = new { row = capture.Region.Row, column = capture.Region.Column,
                rows = capture.Region.Rows, columns = capture.Region.Columns },
            coverage = lines.Select(line => new { line.row, line.first_column, line.last_column }).ToArray(),
            lines = lines.Select(line => new { line.row, line.first_column, line.last_column, line.cells,
                highlights = line.highlights?.Select(span => new { start = span.Start, end = span.End,
                    foreground = span.Style.Foreground, background = span.Style.Background,
                    bold = span.Style.Bold, underline = span.Style.Underline }) }),
            next_cursor = next, highlight_status = capture.HighlightStatus,
        });
    }

    private static ScreenRow[] CaptureScreen(TerminalOutput model, Region region, ResourceReservation memory,
        CancellationToken token)
    {
        const int limit = 16 * MiB;
        Charge(64 + region.Rows * 8L);
        var lines = new ScreenRow[region.Rows];
        for (int i = 0; i < lines.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            int row = region.Row + i;
            int first = region.Column, last = first + region.Columns - 1;
            var cells = new List<ScreenCell>();
            int next = first;
            var rowIdentity = model.ReadAiScreenRow(row - 1, first, last, cell =>
            {
                int column = cell.Column + 1;
                if (column + cell.Width - 1 < first || column > last) return;
                if (next < column) Add(next, column - next, " ", default, true);
                if (cell.IsErased && cell.Text == " " && cells.LastOrDefault() is { blank: true } previous &&
                    previous.column + previous.width == column && previous.foreground == (cell.Style.Foreground ?? 0xFFFFFF) &&
                    previous.background == (cell.Style.Background ?? 0) && previous.flags == (int)cell.Style.Flags)
                    cells[^1] = previous with { width = previous.width + cell.Width };
                else Add(column, cell.Width, cell.Text, cell.Style, cell.IsErased);
                next = column + cell.Width;
            });
            if (next <= last) Add(next, last - next + 1, " ", default, true);
            Charge(96 + cells.Count * 16L);
            lines[i] = new ScreenRow(row, Math.Min(first, cells[0].column),
                Math.Max(last, next - 1), cells.ToArray(), null, rowIdentity.Id, rowIdentity.Revision);

            void Add(int column, int width, string text, TerminalStyle style, bool blank)
            {
                long size = 112 + text.Length * 2L;
                Charge(size);
                cells.Add(new ScreenCell(column, width, text, style.Foreground ?? 0xFFFFFF, style.Background ?? 0,
                    (int)style.Flags, blank));
            }
        }
        return lines;

        void Charge(long bytes)
        {
            if (bytes > limit - memory.Size) throw new ScreenRegionTooLargeException();
            memory.Add(bytes);
        }
    }

    private async Task<object> ReadHistoryAsync(Sharing share, Arguments args, CancellationToken token)
    {
        Span? requested = null;
        string? sourceId = null;
        Point? position = null;
        int rowLimit, byteLimit;
        if (args.Has("cursor"))
        {
            args.Only(1);
            var cursor = Resolve<HistoryCursor>(share.Peer, share, sourceId = args.String("cursor"));
            requested = cursor.Range;
            position = cursor.Position;
            (rowLimit, byteLimit) = (requested.Rows, requested.Bytes);
        }
        else
        {
            args.Only();
            if (args.Has("range")) requested = Resolve<Span>(share.Peer, share, sourceId = args.String("range"));
            rowLimit = args.Integer("max_rows", requested?.Rows);
            byteLimit = args.Integer("max_text_bytes", requested?.Bytes);
        }
        var work = observationWork.Reserve(4 * MiB);
        try
        {
            for (int allowance = PageOutputBudget; allowance >= PageOutputBudget / 2; allowance /= 2)
            {
                HistoryPage page = await HistoryCall(share.Session.ReadRetainedHistoryAsync(
                    requested, position, rowLimit, byteLimit, allowance, token));
                var staged = new StagedHistory(work) { Source = sourceId };
                var body = Ok(share, new
                {
                    source = "normal", capture_ordinal = ++share.CaptureOrdinal,
                    fragments = page.Fragments.Select(fragment =>
                        fragment with { logical_line_id = share.PublicLineId(fragment.logical_line_id) }).ToArray(),
                    range = page.Range is { } range ? staged.Add(range) : null,
                    earlier_range = page.Earlier is { } earlier ? staged.Add(earlier) : null,
                    next_cursor = page.Next is { } next && page.Range is { } original
                        ? staged.Add(new HistoryCursor(original, next)) : null,
                });
                if (await FitsHistoryResultAsync(body, token)) return new HistoryResult(body, staged, page.Dependencies);
                // Keep the same captured range when formatting requires a smaller page.
                requested ??= page.Range;
            }
            throw new ToolException("resource_limit");
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    private async Task<object> SearchHistoryAsync(Sharing share, Arguments args, CancellationToken token)
    {
        SearchCursor? cursor = null;
        string? sourceId = null;
        string query;
        bool caseSensitive;
        int limit, context;
        if (args.Has("cursor"))
        {
            args.Only(1);
            cursor = Resolve<SearchCursor>(share.Peer, share, sourceId = args.String("cursor"));
            (query, caseSensitive, limit, context) = (cursor.Query, cursor.CaseSensitive, cursor.Limit, cursor.Context);
        }
        else
        {
            args.Only();
            query = args.String("query");
            if (!McpArguments.IsQuery(query))
                throw new ToolException("invalid_request");
            caseSensitive = args.Boolean("case_sensitive");
            limit = args.Integer("limit");
            context = args.Integer("context_lines");
        }
        var work = observationWork.Reserve(8 * MiB);
        try
        {
            long started = Stopwatch.GetTimestamp();
            for (int allowance = PageOutputBudget; allowance >= PageOutputBudget / 2; allowance /= 2)
            {
                SearchPage page = await HistoryCall(share.Session.SearchRetainedHistoryAsync(
                    cursor, query, caseSensitive, limit, context, started, allowance, token));
                var staged = new StagedHistory(work) { Source = sourceId };
                var result = Ok(share, new
                {
                    source = "normal",
                    matches = page.Matches.Select(match => new { text = match.Text,
                        logical_line_id = share.PublicLineId(match.Position.Line), content_offset = match.Position.Offset,
                        context_range = staged.Add(match.Context) }).ToArray(),
                    scanned_range = page.Scanned is { } scanned ? staged.Add(scanned) : null,
                    scan_complete = page.Next is null,
                    next_cursor = page.Next is { } next ? staged.Add(next) : null,
                });
                if (await FitsHistoryResultAsync(result, token)) return new HistoryResult(result, staged, page.Dependencies);
                cursor ??= page.Start;
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 100) break;
            }
            throw new ToolException("resource_limit");
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    private static Task<bool> FitsHistoryResultAsync(object body, CancellationToken token) => Task.Run(() =>
    {
        var content = JsonSerializer.SerializeToElement(body, AiFrames.JsonOptions);
        return AiFrames.Serialize(AiFrames.ToolResult(content, modern: true)).Length <= AiFrames.MaxResult;
    }, token);

    private static async Task<T> HistoryCall<T>(Task<T> task)
    {
        try { return await task; }
        catch (HistoryException error) { throw new ToolException(error.Message, error.Message == "busy" ? new { retry_after_ms = 50 } : null); }
    }

    internal sealed class StagedHistory(ResourceReservation work) : IDisposable
    {
        public void Dispose() => work.Dispose();
        public string? Source { get; init; }
        public Dictionary<string, object> Handles { get; } = [];
        public string Add(object value)
        {
            string id = NewId();
            Handles.Add(id, value);
            return id;
        }
    }

    private sealed record HistoryResult(object Body, StagedHistory Staged, Span[] Dependencies);

    private sealed class ScreenRegionTooLargeException : Exception;

    private static int MirroredTextCost(ReadOnlySpan<char> text)
    {
        int size = 0;
        foreach (char value in text)
        {
            // The result includes a structured string and the serialized JSON string.
            size += value switch
            {
                '\\' => 6,
                '"' => 12,
                >= ' ' and <= '~' when value is not ('<' or '>' or '&' or '\'' or '+' or '`') => 1,
                _ => 12,
            };
        }
        return size;
    }
}
