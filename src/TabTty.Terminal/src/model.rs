use crate::unicode::Unicode;
use crate::{
    account, CellView, Effects, FormatView, ReceiveObservation, RowView, ScreenRowView,
    ScreenState, SnapshotView, Stats, TabView, LIVE_MODELS,
};
#[cfg(windows)]
use crate::{legacy_receive::Receiver, windows_icu};
#[cfg(test)]
use crate::{CursorView, EditingView, ExportRow, ExportState, PageView, SourceView};
use std::collections::hash_map::DefaultHasher;
use std::collections::VecDeque;
use std::hash::{Hash, Hasher};
use std::mem::size_of;
#[cfg(test)]
use std::ptr;
use std::sync::atomic::{AtomicI64, Ordering};

#[cfg(test)]
#[path = "admission_tests.rs"]
mod admission_tests;
#[path = "history.rs"]
mod history;
#[path = "reflow.rs"]
mod reflow;
pub use history::{HistoryProof, HistoryRowView, HistorySlice};
// Native identities are process-unique and disjoint from C#'s positive row IDs.
static NEXT_ROW_ID: AtomicI64 = AtomicI64::new(-1);
fn next_row_id() -> i64 {
    NEXT_ROW_ID
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |id| id.checked_sub(1))
        .expect("Native terminal row identity space exhausted.")
}
const NULL: u16 = u16::MAX;
const RECORDING_EFFECT_LIMIT: usize = 256 * 1024;
pub const UNSUPPORTED: i32 = 2;
pub const RESOURCE: i32 = 3;
#[derive(Clone, Copy, Default, PartialEq, Eq)]
struct Style {
    fg: u32,
    bg: u32,
    flags: u32,
}

struct RecordingBuffer {
    bytes: Vec<u8>,
    refused: bool,
}

impl RecordingBuffer {
    fn new() -> Self {
        Self {
            bytes: Vec::new(),
            refused: false,
        }
    }

    fn clear(&mut self) {
        self.bytes.clear();
        self.refused = false;
    }

    fn append(&mut self, bytes: &[u8]) {
        if self.refused {
            return;
        }
        if bytes.len() > RECORDING_EFFECT_LIMIT - self.bytes.len() {
            self.refuse();
        } else {
            self.bytes.extend_from_slice(bytes);
        }
    }

    fn refuse(&mut self) {
        self.bytes.clear();
        self.refused = true;
    }

    fn scalar(&mut self, scalar: u32) {
        let mut bytes = [0; 4];
        self.append(
            char::from_u32(scalar)
                .unwrap()
                .encode_utf8(&mut bytes)
                .as_bytes(),
        );
    }
}
impl Style {
    fn erase(self) -> Self {
        Self {
            flags: self.flags & 16,
            ..self
        }
    }
}
impl CellView {
    fn blank(s: Style) -> Self {
        Self {
            revision: 0,
            offset: 0,
            length: NULL,
            width: 0,
            foreground: s.fg,
            background: s.bg,
            flags: s.flags,
        }
    }
    fn style(self) -> Style {
        Style {
            fg: self.foreground,
            bg: self.background,
            flags: self.flags,
        }
    }
}
struct Row {
    id: i64,
    logical_id: i64,
    logical_offset: i64,
    revision: i64,
    content_revision: i64,
    submitted_revision: i64,
    tab_revision: u64,
    submitted_tab_revision: u64,
    logical_cluster_offset: i64,
    cells: Vec<CellView>,
    arena: Vec<u16>,
    plain: Vec<u16>,
    submitted: Option<Vec<u16>>,
    tabs: Vec<(usize, usize)>,
    formats: Vec<FormatView>,
    recorded_columns: Vec<TabView>,
    excluded: bool,
    soft: bool,
    dirty: bool,
}
impl Row {
    fn new(id: i64, columns: usize) -> Self {
        Self {
            id,
            logical_id: id,
            logical_offset: 0,
            revision: 1,
            content_revision: 1,
            submitted_revision: -1,
            tab_revision: 0,
            submitted_tab_revision: 0,
            logical_cluster_offset: 0,
            cells: Vec::with_capacity(columns),
            arena: Vec::with_capacity(columns * 2),
            plain: Vec::new(),
            submitted: None,
            tabs: Vec::new(),
            formats: Vec::new(),
            recorded_columns: Vec::new(),
            excluded: false,
            soft: false,
            dirty: true,
        }
    }
    fn recycle(&mut self, id: i64) {
        self.id = id;
        self.logical_id = id;
        self.logical_offset = 0;
        self.revision = 1;
        self.content_revision = 1;
        self.submitted_revision = -1;
        self.tab_revision = 0;
        self.submitted_tab_revision = 0;
        self.logical_cluster_offset = 0;
        self.cells.clear();
        self.arena.clear();
        self.plain.clear();
        self.submitted = None;
        self.tabs.clear();
        self.formats.clear();
        self.recorded_columns.clear();
        self.excluded = false;
        self.soft = false;
        self.dirty = true;
    }
    fn bytes(&self) -> usize {
        self.cells.capacity() * size_of::<CellView>()
            + self.arena.capacity() * 2
            + self.plain.capacity() * 2
            + self.submitted.as_ref().map_or(0, |s| s.capacity() * 2)
            + self.tabs.capacity() * size_of::<(usize, usize)>()
            + self.formats.capacity() * size_of::<FormatView>()
            + self.recorded_columns.capacity() * size_of::<TabView>()
    }
    fn pending_units(&self) -> u64 {
        if self.excluded
            || self.submitted.is_some()
                && self.submitted_revision == self.content_revision
                && self.submitted_tab_revision == self.tab_revision
        {
            0
        } else {
            self.potential_units()
        }
    }
    fn potential_units(&self) -> u64 {
        (self.arena.len()
            + self.cells.len()
            + self.tabs.last().map_or(0, |t| t.1)
            + self.format_units()) as u64
    }
    fn contains_text(&self) -> bool {
        !self.formats.is_empty() || self.cells.iter().any(|cell| cell.width > 0)
    }
    fn format_units(&self) -> usize {
        self.formats.len() + self.formats.last().map_or(0, |mark| mark.column as usize)
    }
    fn add_format(&mut self, column: usize, revision: i64) -> Result<(), i32> {
        let column = column as u32;
        let start = self.formats.partition_point(|mark| mark.column < column);
        let end = self.formats.partition_point(|mark| mark.column <= column);
        if end - start >= 256 {
            return Err(RESOURCE);
        }
        self.formats.insert(
            end,
            FormatView {
                revision,
                column,
                scalar: 0xfeff,
            },
        );
        self.invalidate();
        Ok(())
    }
    fn invalidate(&mut self) {
        self.revision += 1;
        self.content_revision += 1;
        self.recorded_columns.clear();
        self.dirty = true;
    }
    fn text(&self, c: CellView) -> &[u16] {
        if c.length == NULL {
            &[]
        } else {
            &self.arena[c.offset as usize..c.offset as usize + c.length as usize]
        }
    }
    fn clear_cells(&mut self, mut start: usize, mut end: usize, revision: i64) {
        self.invalidate();
        self.tabs.retain(|&(a, b)| !(a < end && b > start));
        if start >= self.cells.len() || end <= start {
            return;
        }
        end = end.min(self.cells.len());
        if self.cells[start].width == -1 {
            start -= 1;
        }
        if end < self.cells.len() && self.cells[end].width == -1 {
            end += 1;
        }
        for cell in &mut self.cells[start..end] {
            if cell.width != 0 {
                *cell = CellView {
                    revision,
                    ..CellView::blank(Style::default())
                };
            } else {
                let revision = cell.revision;
                *cell = CellView {
                    revision,
                    ..CellView::blank(Style::default())
                };
            }
        }
        while self
            .cells
            .last()
            .map_or(false, |c| c.width == 0 && c.style() == Style::default())
        {
            self.cells.pop();
        }
    }
    fn compact_arena(&mut self) {
        let mut compact = Vec::with_capacity(self.cells.len() * 2);
        for c in &mut self.cells {
            if c.length != NULL {
                let start = c.offset as usize;
                let len = c.length as usize;
                c.offset = compact.len() as u32;
                compact.extend_from_slice(&self.arena[start..start + len]);
            }
        }
        self.arena = compact;
    }
    fn write(
        &mut self,
        column: usize,
        value: &[u16],
        width: usize,
        style: Style,
        revision: i64,
    ) -> Result<(), i32> {
        if column < self.cells.len() {
            let old = self.cells[column];
            if old.width as usize == width && self.text(old) == value {
                if old.style() != style {
                    self.revision += 1;
                    for index in column..column + width {
                        self.cells[index].foreground = style.fg;
                        self.cells[index].background = style.bg;
                        self.cells[index].flags = style.flags;
                    }
                }
                return Ok(());
            }
        }
        self.clear_cells(column, column + width, revision);
        if self.arena.len() > self.cells.len().max(128) * 8 {
            self.compact_arena();
        }
        if self.arena.len() + value.len() > 1024 * 1024 || value.len() > 512 {
            return Err(RESOURCE);
        }
        if self.cells.len() < column + width {
            self.cells
                .resize(column + width, CellView::blank(Style::default()));
        }
        let offset = self.arena.len() as u32;
        self.arena.extend_from_slice(value);
        self.cells[column] = CellView {
            revision,
            offset,
            length: value.len() as u16,
            width: width as i16,
            foreground: style.fg,
            background: style.bg,
            flags: style.flags,
        };
        if width == 2 {
            self.cells[column + 1] = CellView {
                width: -1,
                ..CellView::blank(style)
            };
        }
        Ok(())
    }
    fn erase(&mut self, mut start: usize, mut end: usize, style: Style, revision: i64) {
        if start < self.cells.len() && self.cells[start].width == -1 {
            start -= 1;
        }
        if end < self.cells.len() && self.cells[end].width == -1 {
            end += 1;
        }
        self.clear_cells(start, end, revision);
        self.formats
            .retain(|mark| !(start..end).contains(&(mark.column as usize)));
        if style != Style::default() {
            if self.cells.len() < end {
                self.cells.resize(end, CellView::blank(Style::default()));
            }
            for cell in &mut self.cells[start..end] {
                *cell = CellView {
                    revision: cell.revision,
                    ..CellView::blank(style)
                };
            }
        }
    }
    fn shift_cells(
        &mut self,
        start: usize,
        count: usize,
        columns: usize,
        insert: bool,
        style: Style,
        revision: i64,
    ) {
        if insert {
            if self.cells.get(start).map_or(false, |cell| cell.width == -1) {
                self.clear_cells(start, start + 1, revision);
            }
            self.cells.resize(
                self.cells.len().max(start) + count,
                CellView::blank(Style::default()),
            );
            let old_end = self.cells.len() - count;
            self.cells.copy_within(start..old_end, start + count);
            self.cells[start..start + count].fill(CellView::blank(style));
            if self.cells.len() > columns {
                if self.cells[columns].width == -1 {
                    self.cells[columns - 1] = CellView::blank(Style::default());
                }
                self.cells.truncate(columns);
            }
        } else {
            self.clear_cells(start, start + count, revision);
            if start < self.cells.len() {
                let end = (start + count).min(self.cells.len());
                self.cells.drain(start..end);
            }
            if style != Style::default() {
                self.cells.resize(
                    self.cells.len().max(columns - count),
                    CellView::blank(Style::default()),
                );
                self.cells.resize(columns, CellView::blank(style));
            }
        }
        self.tabs.clear();
        for cell in self.cells.iter_mut().skip(start) {
            cell.revision = revision;
        }
        for mark in &mut self.formats {
            let column = mark.column as usize;
            if column >= start {
                mark.revision = revision;
                mark.column = if insert {
                    column + count
                } else if column >= start + count {
                    column - count
                } else {
                    columns + 1
                } as u32;
            }
        }
        self.formats.retain(|mark| mark.column <= columns as u32);
        self.tab_revision += 1;
        self.invalidate();
    }
    fn add_tab(&mut self, start: usize, end: usize) {
        if end != (start / 8 + 1) * 8 {
            return;
        }
        if self.cells[start.min(self.cells.len())..end.min(self.cells.len())]
            .iter()
            .any(|c| c.width != 0)
        {
            return;
        }
        if let Some(t) = self.tabs.iter_mut().find(|t| t.0 == start) {
            if t.1 != end {
                self.tab_revision += 1;
            }
            t.1 = end;
        } else {
            self.tab_revision += 1;
            self.tabs.push((start, end));
            self.tabs.sort_unstable();
        }
    }
    fn serialize(&self, preserve_tabs: bool, prefix: Option<usize>) -> Vec<u16> {
        self.serialize_columns(preserve_tabs, prefix, false)
    }
    fn serialize_columns(
        &self,
        preserve_tabs: bool,
        prefix: Option<usize>,
        pending_only: bool,
    ) -> Vec<u16> {
        let mut end = self.text_end();
        if preserve_tabs {
            end = end.max(self.tabs.last().map_or(0, |t| t.1));
        }
        end = end.max(self.formats.last().map_or(0, |mark| mark.column as usize));
        let mut output = Vec::with_capacity(end + self.formats.len());
        let mut marks = self.formats.iter().peekable();
        let mut column = 0;
        while column <= end {
            let recorded = pending_only
                && self
                    .recorded_columns
                    .iter()
                    .any(|span| (span.start..span.end).contains(&(column as u32)));
            while let Some(mark) = marks.next_if(|mark| mark.column as usize == column) {
                if !recorded {
                    output.push(mark.scalar as u16);
                }
            }
            if preserve_tabs {
                if let (Some(prefix), Some(&(_, to))) =
                    (prefix, self.tabs.iter().find(|t| t.0 == column))
                {
                    if (!pending_only || self.recorded_columns.is_empty())
                        && to - column == 8 - (prefix + column) % 8
                        && marks.peek().map_or(true, |mark| mark.column as usize >= to)
                    {
                        output.push(9);
                        column = to;
                        continue;
                    }
                }
            }
            if column == end {
                break;
            }
            if recorded {
                // Each occupied column belongs to the recorded span as a unit.
            } else if column >= self.cells.len() || self.cells[column].width == 0 {
                output.push(32);
            } else if self.cells[column].width > 0 {
                output.extend_from_slice(self.text(self.cells[column]));
            }
            column += 1;
        }
        output
    }
    fn refresh_plain(&mut self) {
        if self.dirty {
            self.plain = self.serialize(false, None);
            self.dirty = false;
        }
    }
    fn text_end(&self) -> usize {
        self.cells
            .iter()
            .rposition(|cell| cell.width != 0)
            .map_or(0, |last| last + 1)
            .max(self.formats.last().map_or(0, |mark| mark.column as usize))
    }
}
#[derive(Default)]
struct Source {
    #[cfg(windows)]
    legacy: Option<Receiver>,
    pending: [u8; 4],
    pending_count: usize,
    started: bool,
    previous_cr: bool,
    escape: u8,
    osc: bool,
    string_bytes: usize,
    parameters: Vec<u8>,
    last_graphic: Vec<u32>,
    last_was_graphic: bool,
    charset: Charset,
    pending_interval: u32,
    escape_interval: u32,
    previous_interval: u32,
}

#[derive(Clone, Copy, Default)]
struct Charset(u8);

impl Charset {
    fn designate(&mut self, slot: u8, graphics: bool) {
        self.0 = (self.0 & !(1 << slot)) | ((graphics as u8) << slot);
    }

    fn invoke(&mut self, second: bool) {
        self.0 = (self.0 & 3) | ((second as u8) << 2);
    }

    fn graphics(self) -> bool {
        self.0 & (1 << (self.0 >> 2)) != 0
    }

    fn graphic(self, scalar: u32) -> u32 {
        const GRAPHICS: [char; 32] = [
            ' ', '◆', '▒', '␉', '␌', '␍', '␊', '°', '±', '␤', '␋', '┘', '┐', '┌', '└', '┼', '⎺',
            '⎻', '─', '⎼', '⎽', '├', '┤', '┴', '┬', '│', '≤', '≥', 'π', '≠', '£', '·',
        ];
        if self.graphics() && (0x5f..=0x7e).contains(&scalar) {
            GRAPHICS[(scalar - 0x5f) as usize] as u32
        } else {
            scalar
        }
    }
}

struct Page {
    screen: VecDeque<Row>,
    row: usize,
    column: usize,
    wrap: bool,
    columns: usize,
    rows: usize,
}

impl Page {
    #[cfg(test)]
    fn view(&self, first: usize) -> PageView {
        PageView {
            first: first as u32,
            count: self.screen.len() as u32,
            row: self.row as u32,
            column: self.column as u32,
            columns: self.columns as u32,
            rows: self.rows as u32,
            wrap: self.wrap as u32,
        }
    }
}

const DEFAULT_TABS: [u64; 16] = [
    0x0101010101010100,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
    0x0101010101010101,
];

#[derive(Clone, Copy)]
struct SavedCursor {
    row: usize,
    column: usize,
    style: Style,
    origin: bool,
    auto_wrap: bool,
    wrap_pending: bool,
    charset: Charset,
}

impl Default for SavedCursor {
    fn default() -> Self {
        Self {
            row: 0,
            column: 0,
            style: Style::default(),
            origin: false,
            auto_wrap: true,
            wrap_pending: false,
            charset: Charset::default(),
        }
    }
}
#[derive(Clone, Default)]
struct RecordedPosition {
    column: usize,
    cluster: Vec<u32>,
    known: bool,
    initialized: bool,
}
struct PreparedOperation {
    valid: bool,
    generation: u64,
    operation: u32,
    source: usize,
    flags: u32,
    length: usize,
    required: u64,
    payload: [u8; 1024],
}

pub struct PreparedResize {
    owner: i64,
    generation: u64,
    columns: usize,
    rows: usize,
    page: ResizePage,
}

enum ResizePage {
    Unchanged,
    Alternate,
    HeightOnly,
    Normal(reflow::Reflow),
}
impl Default for PreparedOperation {
    fn default() -> Self {
        Self {
            valid: false,
            generation: 0,
            operation: 0,
            source: 0,
            flags: 0,
            length: 0,
            required: 0,
            payload: [0; 1024],
        }
    }
}
impl RecordedPosition {
    fn append(&mut self, c: u32, u: &Unicode, ambiguous: usize) {
        if !self.initialized {
            self.initialized = true;
            self.known = true;
        }
        if matches!(c, 9 | 10 | 13) {
            self.column = 0;
            self.cluster.clear();
            self.known = true;
            return;
        }
        if !self.known {
            return;
        }
        if !self.cluster.is_empty() && !u.joins(&self.cluster, c) {
            self.cluster.clear();
        }
        if self.cluster.len() == 256 {
            self.known = false;
            self.cluster.clear();
            return;
        }
        let before = if self.cluster.is_empty() {
            0
        } else {
            u.width(&self.cluster, ambiguous)
        };
        self.cluster.push(c);
        let after = u.width(&self.cluster, ambiguous);
        self.column = self.column.wrapping_add(after).wrapping_sub(before) & 7;
    }
    fn column(&self) -> Option<usize> {
        if !self.initialized || self.known {
            Some(self.column)
        } else {
            None
        }
    }
}
pub struct Model {
    id: i64,
    unicode: Unicode,
    columns: usize,
    screen_rows: usize,
    retained: usize,
    ambiguous: usize,
    decoder_workspace: u64,
    history: VecDeque<Row>,
    history_index: Option<history::Archive>,
    archived_during_operation: usize,
    screen: VecDeque<Row>,
    spares: Vec<Row>,
    history_bytes: usize,
    history_pending_units: u64,
    history_cell_capacity: u64,
    cursor_row: usize,
    column: usize,
    wrap_pending: bool,
    style: Style,
    cluster: Vec<u32>,
    layout_epoch: i64,
    cluster_row: i64,
    cluster_column: usize,
    cluster_width: usize,
    cluster_style: Style,
    sources: [Source; 3],
    logging: Option<u32>,
    recording_interval: u32,
    record_unit: bool,
    finished: bool,
    failed: bool,
    auto_wrap: bool,
    origin: bool,
    insert: bool,
    application_cursor: bool,
    application_keypad: bool,
    mouse_tracking: u32,
    mouse_sgr: bool,
    mouse_generation: i32,
    margin_top: usize,
    margin_bottom: usize,
    tab_stops: [u64; 16],
    saved_cursor: SavedCursor,
    saved_position: Option<(usize, usize)>,
    parked: Option<Page>,
    alternate: Option<Page>,
    normal_return: Option<SavedCursor>,
    alternate_cr: Option<usize>,
    active_source: usize,
    line_feed_returns: bool,
    bracketed_paste: bool,
    cursor_visible: bool,
    cursor_blink: bool,
    cursor_shape: u32,
    bells: u32,
    terminal_log: RecordingBuffer,
    text_log: RecordingBuffer,
    observe: bool,
    observations: Vec<ReceiveObservation>,
    replies: Vec<u8>,
    position: RecordedPosition,
    view_rows: Vec<RowView>,
    feed_bytes: u64,
    feed_batches: u64,
    snapshots: u64,
    unsupported: u64,
    accounted: u64,
    peak_owned: u64,
    limit: u64,
    quota: u64,
    generation: u64,
    content_stamp: i64,
    prepared: PreparedOperation,
    #[cfg(test)]
    export_rows: Vec<ExportRow>,
    #[cfg(test)]
    export_tabs: Vec<TabView>,
    #[cfg(test)]
    export_sources: [SourceView; 3],
    #[cfg(test)]
    export_editing: EditingView,
}
impl Model {
    pub fn new(columns: usize, rows: usize, retained: usize, ambiguous: usize) -> Self {
        let mut screen = VecDeque::with_capacity(rows);
        screen.push_back(Row::new(next_row_id(), columns));
        let mut result = Self {
            id: screen[0].id,
            unicode: Unicode::new(),
            columns,
            screen_rows: rows,
            retained,
            ambiguous,
            decoder_workspace: 0,
            history: VecDeque::with_capacity(retained),
            history_index: None,
            archived_during_operation: 0,
            screen,
            spares: Vec::with_capacity(2),
            history_bytes: 0,
            history_pending_units: 0,
            history_cell_capacity: 0,
            cursor_row: 0,
            column: 0,
            wrap_pending: false,
            style: Style::default(),
            cluster: Vec::with_capacity(256),
            layout_epoch: 0,
            cluster_row: 0,
            cluster_column: 0,
            cluster_width: 0,
            cluster_style: Style::default(),
            sources: [
                Source::default(),
                Source::default(),
                Source {
                    started: true,
                    ..Source::default()
                },
            ],
            logging: None,
            recording_interval: 0,
            record_unit: true,
            finished: false,
            failed: false,
            auto_wrap: true,
            origin: false,
            insert: false,
            application_cursor: false,
            application_keypad: false,
            mouse_tracking: 0,
            mouse_sgr: false,
            mouse_generation: 0,
            margin_top: 0,
            margin_bottom: rows - 1,
            tab_stops: DEFAULT_TABS,
            saved_cursor: SavedCursor::default(),
            saved_position: None,
            parked: None,
            alternate: None,
            normal_return: None,
            alternate_cr: None,
            active_source: 0,
            line_feed_returns: false,
            bracketed_paste: false,
            cursor_visible: true,
            cursor_blink: true,
            cursor_shape: 0,
            bells: 0,
            terminal_log: RecordingBuffer::new(),
            text_log: RecordingBuffer::new(),
            observe: false,
            observations: Vec::new(),
            replies: Vec::with_capacity(2048),
            position: RecordedPosition::default(),
            view_rows: Vec::new(),
            feed_bytes: 0,
            feed_batches: 0,
            snapshots: 0,
            unsupported: 0,
            accounted: 0,
            peak_owned: 0,
            limit: u64::MAX,
            quota: 0,
            generation: 0,
            content_stamp: 0,
            prepared: PreparedOperation::default(),
            #[cfg(test)]
            export_rows: Vec::new(),
            #[cfg(test)]
            export_tabs: Vec::new(),
            #[cfg(test)]
            export_sources: [SourceView::empty(); 3],
            #[cfg(test)]
            export_editing: EditingView {
                margin_top: 0,
                margin_bottom: 0,
                position_row: -1,
                position_column: 0,
                saved_row: 0,
                saved_column: 0,
                saved_fg: 0,
                saved_bg: 0,
                saved_style: 0,
                saved_flags: 0,
                tab_stops: DEFAULT_TABS,
                parked: PageView::default(),
                alternate: PageView::default(),
                normal_return: CursorView::default(),
            },
        };
        LIVE_MODELS.fetch_add(1, Ordering::Relaxed);
        result.update_accounting();
        result
    }
    fn owned(&self) -> u64 {
        (size_of::<Self>()
            + self.history.capacity() * size_of::<Row>()
            + self.screen.capacity() * size_of::<Row>()
            + self.spares.capacity() * size_of::<Row>()
            + self.history_bytes
            + self.screen.iter().map(Row::bytes).sum::<usize>()
            + self.spares.iter().map(Row::bytes).sum::<usize>()
            + [&self.parked, &self.alternate]
                .into_iter()
                .flatten()
                .map(|page| {
                    page.screen.capacity() * size_of::<Row>()
                        + page.screen.iter().map(Row::bytes).sum::<usize>()
                })
                .sum::<usize>()
            + self.cluster.capacity() * 4
            + self.position.cluster.capacity() * 4
            + self
                .sources
                .iter()
                .map(|s| s.parameters.capacity() + s.last_graphic.capacity() * 4)
                .sum::<usize>()
            + self.terminal_log.bytes.capacity()
            + self.text_log.bytes.capacity()
            + self.observations.capacity() * size_of::<ReceiveObservation>()
            + self.replies.capacity()
            + self.view_rows.capacity() * size_of::<RowView>()
            + self.export_workspace()) as u64
            + self.decoder_workspace
            + self
                .history_index
                .as_ref()
                .map_or(0, |_| history::Archive::required(self.retained))
    }

    #[cfg(windows)]
    pub fn use_legacy_decoder(&mut self, page: u32, shift_kana: bool) -> Result<(), i32> {
        self.sources[0].legacy = Some(Receiver::new(page, shift_kana)?);
        self.sources[1].legacy = Some(Receiver::new(page, shift_kana)?);
        self.sources[0].started = true;
        self.sources[1].started = true;
        self.decoder_workspace = crate::legacy_receive::workspace_bytes(page);
        self.update_accounting();
        Ok(())
    }
    fn export_workspace(&self) -> usize {
        #[cfg(test)]
        {
            return self.export_rows.capacity() * size_of::<ExportRow>()
                + self.export_tabs.capacity() * size_of::<TabView>();
        }
        #[cfg(not(test))]
        0
    }

    fn update_accounting(&mut self) {
        self.invalidate_prepared();
        let owned = self.owned();
        account(self.accounted, owned);
        self.accounted = owned;
        self.peak_owned = self.peak_owned.max(owned);
        let cells = self.history_cell_capacity
            + self
                .screen
                .iter()
                .chain(self.spares.iter())
                .map(|r| r.cells.capacity() as u64)
                .sum::<u64>();
        self.quota = owned + cells * (64 - size_of::<CellView>() as u64);
    }
    pub fn stats(&mut self) -> Stats {
        Stats {
            feed_bytes: self.feed_bytes,
            feed_batches: self.feed_batches,
            snapshot_exports: self.snapshots,
            unsupported: self.unsupported,
            owned_bytes: self.accounted,
            peak_owned_bytes: self.peak_owned,
            rows: (self.history.len() + self.screen.len()) as u64,
        }
    }
    pub fn effects(&self) -> Effects {
        Effects {
            text: self.text_log.bytes.as_ptr(),
            text_length: self.text_log.bytes.len() as u32,
            reserved0: ((self.bracketed_paste as u32) << 4)
                | ((self.application_cursor as u32) << 5)
                | ((self.application_keypad as u32) << 6)
                | ((self.parked.is_some() as u32) << 7)
                | (self.bells << 8),
            terminal: self.terminal_log.bytes.as_ptr(),
            terminal_length: self.terminal_log.bytes.len() as u32,
            reply_length: self.replies.len() as u32,
            replies: self.replies.as_ptr(),
            mouse_tracking: self.mouse_tracking | ((self.mouse_sgr as u32) << 31),
            mouse_generation: self.mouse_generation,
            layout_epoch: self.layout_epoch,
            observations: self.observations.as_ptr(),
            observation_count: self.observations.len() as u32,
            recording_refused: (self.text_log.refused || self.terminal_log.refused) as u32,
        }
    }
    fn start_call(&mut self, flags: u32) -> Result<(), i32> {
        self.invalidate_prepared();
        self.content_stamp = self.content_stamp.checked_add(1).ok_or(RESOURCE)?;
        self.archived_during_operation = 0;
        self.clear_effects();
        if self.failed {
            return Err(UNSUPPORTED);
        }
        self.observe = flags & 4 != 0;
        let flags = flags & 3;
        if let Some(prior) = self.logging {
            if prior != flags {
                self.unsupported += 1;
                self.failed = true;
                return Err(UNSUPPORTED);
            }
        } else {
            self.logging = Some(flags);
        }
        Ok(())
    }
    fn finish_result(&mut self, result: Result<(), i32>) -> Result<(), i32> {
        if let Err(code) = result {
            self.failed = true;
            if code == UNSUPPORTED {
                self.unsupported += 1;
            }
        } else if self.archived_during_operation != 0 {
            // Once history is requested, keep newly archived rows indexed and
            // work through any backlog faster than live output adds to it.
            self.index_archived_rows(self.archived_during_operation.saturating_mul(2));
        }
        self.update_accounting();
        result
    }
    pub fn feed(&mut self, index: usize, payload: &[u8], flags: u32) -> Result<(), i32> {
        self.start_call(flags)?;
        if self.finished || index > 2 {
            return Err(1);
        }
        self.feed_batches += 1;
        self.feed_bytes += payload.len() as u64;
        self.active_source = index;
        let mut source = std::mem::take(&mut self.sources[index]);
        let result = self.decode(&mut source, payload, false);
        self.sources[index] = source;
        self.finish_result(result)
    }
    pub fn complete(&mut self, flags: u32) -> Result<(), i32> {
        self.start_call(flags)?;
        if self.finished {
            return Ok(());
        }
        let mut result = Ok(());
        for index in 0..2 {
            self.active_source = index;
            let mut source = std::mem::take(&mut self.sources[index]);
            result = self.decode(&mut source, &[], true);
            self.sources[index] = source;
            if result.is_err() {
                break;
            }
        }
        self.finished = true;
        self.flush_alternate_cr();
        self.finish_result(result)
    }
    pub fn flush(&mut self, flags: u32) -> Result<(), i32> {
        self.start_call(flags)?;
        if flags & 2 != 0 {
            if self.parked.is_some() {
                self.flush_alternate_cr();
            } else {
                self.flush_normal_text();
            }
        }
        self.update_accounting();
        Ok(())
    }

    pub fn begin_recording(&mut self, flags: u32, interval: u32) -> Result<(), i32> {
        if self.finished || self.failed || flags == 0 || flags & !3 != 0 {
            return Err(1);
        }
        if self.logging.map_or(false, |active| active != 0) {
            return Err(1);
        }
        self.logging = Some(flags);
        self.recording_interval = interval;
        self.position = RecordedPosition::default();
        if flags & 2 != 0 {
            let screen = if let Some(page) = &mut self.parked {
                &mut page.screen
            } else {
                &mut self.screen
            };
            let mut carry = false;
            for row in self.history.iter_mut().chain(screen.iter_mut()) {
                row.excluded = carry || row.contains_text();
                carry = row.soft && row.excluded;
            }
            carry = false;
            for row in self.history.iter_mut().chain(screen.iter_mut()).rev() {
                row.excluded |= row.soft && carry;
                carry = row.excluded;
            }
            self.history_pending_units = self.history.iter().map(Row::pending_units).sum();
        }
        self.update_accounting();
        Ok(())
    }

    pub fn cutoff_recording(&mut self, flags: u32, interval: u32, abort: bool) -> Result<(), i32> {
        if abort {
            self.clear_effects();
            self.alternate_cr = None;
        } else {
            self.admit(2, 0, &[], flags)?;
            self.flush(flags)?;
        }
        self.logging = Some(0);
        self.recording_interval = interval;
        self.update_accounting();
        Ok(())
    }
    pub fn prepare_resize(&self, columns: usize, rows: usize) -> Result<PreparedResize, i32> {
        let rows = rows.min(self.retained);
        if self.resize_required(columns, rows) > self.limit {
            return Err(5);
        }
        let page = if columns == self.columns && rows == self.screen_rows {
            ResizePage::Unchanged
        } else if self.parked.is_some() {
            ResizePage::Alternate
        } else if self.can_resize_height_only(columns) {
            ResizePage::HeightOnly
        } else {
            ResizePage::Normal(reflow::normal(
                self,
                &self.screen,
                self.cursor_row,
                self.column,
                self.wrap_pending,
                columns,
            )?)
        };
        Ok(PreparedResize {
            owner: self.id,
            generation: self.generation,
            columns,
            rows,
            page,
        })
    }

    pub fn apply_resize(&mut self, plan: PreparedResize) -> Result<(), i32> {
        if plan.owner != self.id || plan.generation != self.generation {
            return Err(1);
        }
        self.clear_effects();
        match plan.page {
            ResizePage::Unchanged => return Ok(()),
            ResizePage::Alternate => self.clip_alternate(plan.columns, plan.rows),
            ResizePage::HeightOnly => self.resize_normal_height(plan.rows),
            ResizePage::Normal(reflow) => self.apply_reflow(reflow, plan.columns, plan.rows),
        }
        self.layout_epoch = self.layout_epoch.wrapping_add(1);
        self.update_accounting();
        Ok(())
    }

    #[cfg(test)]
    pub fn resize(&mut self, columns: usize, rows: usize) -> Result<(), i32> {
        self.apply_resize(self.prepare_resize(columns, rows)?)
    }

    pub fn resize_required(&self, columns: usize, rows: usize) -> u64 {
        let staged = self.quota + size_of::<Option<PreparedResize>>() as u64;
        if columns == self.columns && rows == self.screen_rows || self.parked.is_some() {
            return staged;
        }
        if self.can_resize_height_only(columns) {
            let total = self.history.len() + self.screen.len();
            let visible = total.min(rows);
            let archived = total - visible;
            let growth = |needed: usize, capacity: usize| {
                if needed > capacity {
                    (needed + 1).next_power_of_two().max(capacity * 2) * size_of::<Row>()
                } else {
                    0
                }
            };
            return staged
                + growth(visible, self.screen.capacity()) as u64
                + growth(archived, self.history.capacity()) as u64;
        }
        self.reflow_required(&self.screen, columns)
    }

    fn can_resize_height_only(&self, columns: usize) -> bool {
        columns == self.columns && self.cursor_row < self.screen.len()
    }

    fn resize_normal_height(&mut self, rows: usize) {
        let cursor = self.history.len() + self.cursor_row;
        let archived = (self.history.len() + self.screen.len()).saturating_sub(rows);
        if archived != self.history.len() {
            self.history_index = None;
        }
        while self.history.len() < archived {
            let row = self.screen.pop_front().unwrap();
            self.history_bytes += row.bytes();
            self.history_pending_units += row.pending_units();
            self.history_cell_capacity += row.cells.capacity() as u64;
            self.history.push_back(row);
        }
        while self.history.len() > archived {
            let row = self.history.pop_back().unwrap();
            self.history_bytes -= row.bytes();
            self.history_pending_units -= row.pending_units();
            self.history_cell_capacity -= row.cells.capacity() as u64;
            self.screen.push_front(row);
        }
        self.cursor_row = cursor.saturating_sub(archived).min(self.screen.len() - 1);
        self.screen_rows = rows;
        self.margin_top = 0;
        self.margin_bottom = rows - 1;
        if self.cluster_row != 0 && !self.screen.iter().any(|row| row.id == self.cluster_row) {
            self.cluster_row = 0;
        }
    }

    fn reflow_required(&self, screen: &VecDeque<Row>, columns: usize) -> u64 {
        let content = self
            .history
            .iter()
            .chain(screen)
            .map(|r| r.cells.len() + r.format_units())
            .sum::<usize>();
        let fragments = (self.history.len() + screen.len() + 2 + content / (columns - 1))
            .min(self.retained + 1);
        let copy = self
            .history
            .iter()
            .chain(screen)
            .map(Row::bytes)
            .sum::<usize>();
        self.quota
            + (copy as u64 * 6)
            + (fragments as u64 * (columns as u64 * 96 + 512))
            + 2 * 1024 * 1024
    }

    fn clear_effects(&mut self) {
        self.text_log.clear();
        self.terminal_log.clear();
        self.replies.clear();
        self.observations.clear();
        self.bells = 0;
    }

    fn apply_reflow(&mut self, mut planned: reflow::Reflow, columns: usize, rows: usize) {
        self.history_index = None;
        let first = planned.rows.len().saturating_sub(rows);
        self.screen = planned.rows.split_off(first);
        self.history = planned.rows;
        self.history_bytes = self.history.iter().map(Row::bytes).sum();
        self.history_pending_units = self.history.iter().map(Row::pending_units).sum();
        self.history_cell_capacity = self
            .history
            .iter()
            .map(|row| row.cells.capacity() as u64)
            .sum();
        self.cursor_row = planned
            .cursor
            .saturating_sub(first)
            .min(self.screen.len() - 1);
        self.column = planned.column.min(columns - 1);
        self.wrap_pending = self.auto_wrap && planned.column == columns;
        self.cluster_row = 0;
        if let Some((row, column)) = planned.cluster {
            if self.screen.iter().any(|r| r.id == row) {
                self.cluster_row = row;
                self.cluster_column = column;
            }
        }
        if planned.text.refused {
            self.text_log.refuse();
        } else {
            self.text_log.append(&planned.text.bytes);
        }
        self.position = planned.position;
        self.columns = columns;
        self.screen_rows = rows;
        self.margin_top = 0;
        self.margin_bottom = self.screen_rows - 1;
    }

    fn clip_alternate(&mut self, columns: usize, rows: usize) {
        self.screen.truncate(rows);
        for row in &mut self.screen {
            if row.cells.len() > columns {
                if row.cells[columns].width == -1 {
                    row.cells[columns - 1] = CellView::blank(Style::default());
                }
                row.cells.truncate(columns);
            }
            row.tabs.retain(|&(_, end)| end <= columns);
            row.formats.retain(|mark| mark.column as usize <= columns);
            row.soft = false;
            row.invalidate();
        }
        self.cluster_row = 0;
        self.cursor_row = self.cursor_row.min(rows - 1);
        self.column = self.column.min(columns - 1);
        self.wrap_pending = false;
        self.columns = columns;
        self.screen_rows = rows;
        self.margin_top = 0;
        self.margin_bottom = rows - 1;
    }
    fn decode(&mut self, source: &mut Source, payload: &[u8], flush: bool) -> Result<(), i32> {
        #[cfg(windows)]
        if let Some(mut receiver) = source.legacy.take() {
            let requires_apartment = receiver.requires_apartment();
            let mut batch = || {
                for byte in payload
                    .iter()
                    .copied()
                    .map(Some)
                    .chain(flush.then_some(None))
                {
                    for item in receiver
                        .decode_byte(byte, source.escape != 0, self.recording_interval)?
                        .into_scalars()
                    {
                        self.apply(source, item.scalar, item.interval)?;
                    }
                }
                Ok(())
            };
            let result = if requires_apartment {
                windows_icu::in_apartment(batch)
            } else {
                batch()
            };
            source.legacy = Some(receiver);
            return result;
        }
        for &byte in payload {
            let mut interval = if source.pending_count == 0 {
                source.pending_interval = self.recording_interval;
                self.recording_interval
            } else {
                source.pending_interval
            };
            for scalar in decode_utf8_byte(&mut source.pending, &mut source.pending_count, byte)? {
                self.emit(source, scalar, interval)?;
                interval = self.recording_interval;
                source.pending_interval = interval;
            }
        }
        if let Some(scalar) = finish_utf8(flush, &mut source.pending_count) {
            self.emit(source, scalar, source.pending_interval)?;
        }
        Ok(())
    }
    fn emit(&mut self, source: &mut Source, c: u32, interval: u32) -> Result<(), i32> {
        if suppress_initial_bom(&mut source.started, c) {
            return Ok(());
        }
        self.apply(source, c, interval)
    }
    fn operation_scalar(&mut self, c: u32) {
        if self.record_unit && self.logging.unwrap_or(0) & 1 != 0 {
            self.terminal_log.scalar(c);
        }
    }
    fn operation_bytes(&mut self, bytes: &[u8]) {
        if self.record_unit && self.logging.unwrap_or(0) & 1 != 0 {
            self.terminal_log.append(bytes);
        }
    }
    fn text_scalar(&mut self, scalar: u32) {
        self.text_log.scalar(scalar);
        self.position.append(scalar, &self.unicode, self.ambiguous);
    }

    fn flush_normal_text(&mut self) {
        for index in 0..self.history.len() {
            self.submit_history(index, false);
        }
        for index in 0..self.screen.len() {
            self.submit_screen(index, false);
        }
    }

    fn flush_alternate_cr(&mut self) {
        if self.alternate_cr.take().is_some() {
            self.text_scalar(10);
        }
    }

    fn recording_alternate(&self) -> bool {
        self.record_unit && self.parked.is_some() && self.logging.unwrap_or(0) & 2 != 0
    }

    fn apply(&mut self, source: &mut Source, c: u32, interval: u32) -> Result<(), i32> {
        if self.observe && self.active_source < 2 {
            let visible = source.escape == 0
                || source.escape != 3 && source.escape != 4 && matches!(c, 9 | 10 | 13);
            self.observations.push(ReceiveObservation {
                scalar: c,
                flags: self.active_source as u32 | ((visible as u32) << 1),
            });
        }
        if is_control(c) {
            source.last_was_graphic = false;
        }
        let crlf = source.previous_cr && source.previous_interval == interval;
        source.previous_cr = c == 13;
        source.previous_interval = interval;
        self.record_unit = interval == self.recording_interval;
        if c == 27 {
            source.escape_interval = interval;
        }
        let alternate_crlf = self.alternate_cr == Some(self.active_source) && c == 10;
        if !alternate_crlf {
            self.flush_alternate_cr();
        }
        match interpret_scalar(
            c,
            &mut source.escape,
            &mut source.osc,
            &mut source.string_bytes,
            Parameters::Live(&mut source.parameters),
        )? {
            Unit::Ignored => return Ok(()),
            Unit::Bell => {
                self.bells += 1;
                return Ok(());
            }
            Unit::Escape(command) => {
                self.record_unit &= source.escape_interval == self.recording_interval;
                return self.escape(source, c as u8, command);
            }
            Unit::Csi(Csi::Repeat(count)) => {
                self.record_unit &= source.escape_interval == self.recording_interval;
                let mut spelling = [0; 256];
                let spelling = copy_graphic(&source.last_graphic, &mut spelling);
                if spelling.is_empty() {
                    return Ok(());
                }
                for _ in 0..count {
                    for &scalar in spelling {
                        self.print(source, scalar)?;
                    }
                }
                return Ok(());
            }
            Unit::Csi(command) => {
                self.record_unit &= source.escape_interval == self.recording_interval;
                return self.csi(source, c as u8, command);
            }
            Unit::Control => {}
            Unit::Graphic => return self.print(source, source.charset.graphic(c)),
        }
        match c {
            13 => {
                self.operation_scalar(c);
                if self.recording_alternate() {
                    self.alternate_cr = Some(self.active_source);
                }
                self.cluster_row = 0;
                self.column = 0;
                self.wrap_pending = false;
                return Ok(());
            }
            8 => {
                self.operation_scalar(c);
                self.cluster_row = 0;
                self.column = self.column.saturating_sub(1);
                self.wrap_pending = false;
                return Ok(());
            }
            9 => {
                self.operation_scalar(c);
                if self.recording_alternate() {
                    self.text_scalar(c);
                }
                self.cluster_row = 0;
                self.ensure_cursor();
                let next = self.next_tab(self.column);
                self.screen[self.cursor_row].excluded |= !self.record_unit;
                self.screen[self.cursor_row].add_tab(self.column, next);
                self.column = next;
                self.wrap_pending = false;
                return Ok(());
            }
            10 | 11 | 12 => {
                self.operation_scalar(c);
                if c == 10 && self.record_unit && self.logging.unwrap_or(0) & 2 != 0 {
                    if self.parked.is_some() {
                        if alternate_crlf {
                            self.text_scalar(13);
                        }
                        self.alternate_cr = None;
                        self.text_scalar(10);
                    } else {
                        self.record_line_feed(crlf);
                    }
                }
                self.move_down(self.line_feed_returns);
                return Ok(());
            }
            14 | 15 => {
                source.charset.invoke(c == 14);
                self.operation_scalar(c);
                return Ok(());
            }
            _ => {}
        }
        Ok(())
    }
    fn print(&mut self, source: &mut Source, c: u32) -> Result<(), i32> {
        if !source.last_was_graphic || !self.unicode.joins(&source.last_graphic, c) {
            source.last_graphic.clear();
        }
        if source.last_graphic.len() == 256 {
            return Err(RESOURCE);
        }
        source.last_graphic.push(c);
        source.last_was_graphic = true;
        self.operation_scalar(c);
        if self.recording_alternate() {
            self.text_scalar(c);
        }
        if c == 0xfeff {
            self.cluster_row = 0;
            self.ensure_cursor();
            self.screen[self.cursor_row].excluded |= !self.record_unit;
            return self.screen[self.cursor_row]
                .add_format(self.column + self.wrap_pending as usize, self.content_stamp);
        }
        if self.cluster_row != 0 && self.unicode.joins(&self.cluster, c) {
            if self.cluster.len() == 256 {
                return Err(RESOURCE);
            }
            self.cluster.push(c);
            if !self.record_unit {
                if let Some(row) = self
                    .screen
                    .iter_mut()
                    .find(|row| row.id == self.cluster_row)
                {
                    row.excluded = true;
                }
            }
            return self.extend_cluster();
        }
        self.cluster.clear();
        self.cluster.push(c);
        let width = self.unicode.width(&self.cluster, self.ambiguous);
        if self.wrap_pending || (self.auto_wrap && self.column + width > self.columns) {
            self.advance(true);
            self.column = 0;
            self.wrap_pending = false;
        } else if self.column + width > self.columns {
            self.column = self.columns - width;
        }
        self.ensure_cursor();
        self.cluster_row = self.screen[self.cursor_row].id;
        self.cluster_column = self.column;
        self.cluster_width = width;
        self.cluster_style = self.style;
        self.screen[self.cursor_row].excluded |= !self.record_unit;
        if self.insert {
            self.screen[self.cursor_row].shift_cells(
                self.column,
                width,
                self.columns,
                true,
                self.style.erase(),
                self.content_stamp,
            );
        }
        let mut text = [0u16; 2];
        let value = char::from_u32(c).unwrap().encode_utf16(&mut text);
        self.screen[self.cursor_row].write(
            self.column,
            value,
            width,
            self.style,
            self.content_stamp,
        )?;
        self.after_cluster();
        Ok(())
    }
    fn extend_cluster(&mut self) -> Result<(), i32> {
        let index = self
            .screen
            .iter()
            .position(|r| r.id == self.cluster_row)
            .ok_or(RESOURCE)?;
        let width = self.unicode.width(&self.cluster, self.ambiguous);
        self.screen[index].clear_cells(
            self.cluster_column,
            self.cluster_column + self.cluster_width,
            self.content_stamp,
        );
        if self.cluster_column + width > self.columns {
            if self.auto_wrap {
                self.advance(true);
                self.cluster_row = self.screen[self.cursor_row].id;
                self.cluster_column = 0;
            } else {
                self.cluster_column = self.columns - width;
            }
        }
        self.cluster_width = width;
        let mut text = [0u16; 512];
        let mut length = 0;
        for &c in &self.cluster {
            length += char::from_u32(c)
                .unwrap()
                .encode_utf16(&mut text[length..])
                .len();
        }
        let index = self
            .screen
            .iter()
            .position(|r| r.id == self.cluster_row)
            .ok_or(RESOURCE)?;
        self.screen[index].write(
            self.cluster_column,
            &text[..length],
            width,
            self.cluster_style,
            self.content_stamp,
        )?;
        self.after_cluster();
        Ok(())
    }
    fn after_cluster(&mut self) {
        let next = self.cluster_column + self.cluster_width;
        self.wrap_pending = self.auto_wrap && next == self.columns;
        self.column = (self.columns - 1).min(next);
    }
    fn new_row(&mut self) -> Row {
        let id = next_row_id();
        if let Some(mut row) = self.spares.pop() {
            row.recycle(id);
            row
        } else {
            Row::new(id, self.columns)
        }
    }
    fn ensure_cursor(&mut self) {
        while self.screen.len() <= self.cursor_row {
            let row = self.new_row();
            self.screen.push_back(row);
            self.retire();
        }
    }
    fn advance(&mut self, soft: bool) {
        self.ensure_cursor();
        if self.screen[self.cursor_row].soft != soft {
            self.screen[self.cursor_row].soft = soft;
            self.screen[self.cursor_row].revision += 1;
        }
        let prev_id = self.screen[self.cursor_row].logical_id;
        let prev_offset = self.screen[self.cursor_row].logical_offset;
        let prev_len = if soft {
            self.screen[self.cursor_row].refresh_plain();
            self.screen[self.cursor_row].plain.len()
        } else {
            0
        };
        let prev_cluster_offset = self.screen[self.cursor_row].logical_cluster_offset;
        let excluded = self.screen[self.cursor_row].excluded;
        let prev_clusters = if soft {
            self.unicode
                .count_utf16(&self.screen[self.cursor_row].plain)
        } else {
            0
        };
        if self.cursor_row == self.margin_bottom {
            self.scroll_region(self.margin_top, self.margin_bottom, 1, false);
        } else if self.cursor_row < self.screen_rows - 1 {
            self.cursor_row += 1;
            self.ensure_cursor();
        }
        if soft {
            self.screen[self.cursor_row].excluded |= excluded;
            self.screen[self.cursor_row].logical_id = prev_id;
            self.screen[self.cursor_row].logical_offset = prev_offset + prev_len as i64;
            self.screen[self.cursor_row].logical_cluster_offset =
                prev_cluster_offset + prev_clusters as i64;
        }
    }
    fn scroll_region(&mut self, top: usize, bottom: usize, count: usize, down: bool) {
        self.cluster_row = 0;
        while self.screen.len() <= bottom {
            let row = self.new_row();
            self.screen.push_back(row);
        }
        for _ in 0..count.min(bottom - top + 1) {
            let old = self.screen.remove(if down { bottom } else { top }).unwrap();
            if !down && top == 0 && bottom == self.screen_rows - 1 && self.parked.is_none() {
                self.history_bytes += old.bytes();
                self.history_pending_units += old.pending_units();
                self.history_cell_capacity += old.cells.capacity() as u64;
                self.history.push_back(old);
                self.archived_during_operation += 1;
            }
            let mut row = self.new_row();
            row.erase(0, self.columns, self.style.erase(), self.content_stamp);
            self.screen.insert(if down { top } else { bottom }, row);
            self.retire();
        }
    }
    fn move_down(&mut self, left: bool) {
        self.cluster_row = 0;
        self.advance(false);
        if left {
            self.column = 0;
        }
        self.wrap_pending = false;
    }
    fn retire(&mut self) {
        if self.parked.is_some() {
            return;
        }
        while self.history.len() + self.screen.len() > self.retained {
            if self.history.is_empty() {
                break;
            }
            let mut old = self.history.pop_front().unwrap();
            if let Some(index) = &mut self.history_index {
                index.remove_front(old.logical_id);
            }
            self.history_bytes -= old.bytes();
            self.history_pending_units -= old.pending_units();
            self.history_cell_capacity -= old.cells.capacity() as u64;
            if self.logging.unwrap_or(0) & 2 != 0 {
                submit(
                    &mut old,
                    false,
                    &mut self.text_log,
                    &mut self.position,
                    &self.unicode,
                    self.ambiguous,
                );
            }
            if self.spares.len() < 2 {
                old.recycle(0);
                self.spares.push(old);
            }
        }
    }
    fn submit_history(&mut self, index: usize, boundary: bool) -> bool {
        let before = self.history[index].bytes();
        let pending = self.history[index].pending_units();
        let changed = submit(
            &mut self.history[index],
            boundary,
            &mut self.text_log,
            &mut self.position,
            &self.unicode,
            self.ambiguous,
        );
        self.history_bytes = self.history_bytes - before + self.history[index].bytes();
        self.history_pending_units =
            self.history_pending_units - pending + self.history[index].pending_units();
        changed
    }
    fn submit_screen(&mut self, index: usize, boundary: bool) -> bool {
        submit(
            &mut self.screen[index],
            boundary,
            &mut self.text_log,
            &mut self.position,
            &self.unicode,
            self.ambiguous,
        )
    }
    fn record_line_feed(&mut self, crlf: bool) {
        self.ensure_cursor();
        let mut first = self.cursor_row;
        while first > 0 && self.screen[first - 1].soft {
            first -= 1;
        }
        if (first..=self.cursor_row).any(|index| self.screen[index].excluded) {
            return;
        }
        let mut changed = false;
        if first == 0 {
            let mut start = self.history.len();
            while start > 0 && self.history[start - 1].soft {
                start -= 1;
            }
            if (start..self.history.len()).any(|index| self.history[index].excluded) {
                return;
            }
            for index in start..self.history.len() {
                changed |= self.submit_history(index, true);
            }
        }
        for index in first..=self.cursor_row {
            changed |= self.submit_screen(index, true);
        }
        if changed {
            if crlf {
                self.text_log.append(&[13]);
                self.position.append(13, &self.unicode, self.ambiguous);
            }
            self.text_log.append(&[10]);
            self.position.append(10, &self.unicode, self.ambiguous);
        }
    }
    fn next_tab(&self, column: usize) -> usize {
        ((column + 1)..self.columns)
            .find(|&stop| self.tab_stops[stop / 64] & (1 << (stop % 64)) != 0)
            .unwrap_or(self.columns - 1)
    }

    fn previous_tab(&self, column: usize) -> usize {
        (0..column)
            .rev()
            .find(|&stop| self.tab_stops[stop / 64] & (1 << (stop % 64)) != 0)
            .unwrap_or(0)
    }

    fn capture_cursor(&self, charset: Charset) -> SavedCursor {
        SavedCursor {
            row: self.cursor_row,
            column: self.column,
            style: self.style,
            origin: self.origin,
            auto_wrap: self.auto_wrap,
            wrap_pending: self.wrap_pending,
            charset,
        }
    }

    fn restore_cursor(&mut self, source: &mut Source, saved: SavedCursor) {
        self.cluster_row = 0;
        self.cursor_row = saved.row.min(self.screen_rows - 1);
        self.column = saved.column.min(self.columns - 1);
        self.style = saved.style;
        self.origin = saved.origin;
        self.auto_wrap = saved.auto_wrap;
        self.wrap_pending = saved.wrap_pending && self.column == self.columns - 1;
        source.charset = saved.charset;
    }

    fn reset(&mut self, charset: &mut Charset, hard: bool) {
        self.layout_epoch = self.layout_epoch.wrapping_add(1);
        self.cluster_row = 0;
        self.style = Style::default();
        self.insert = false;
        self.origin = false;
        self.line_feed_returns = false;
        self.application_cursor = false;
        self.application_keypad = false;
        self.auto_wrap = true;
        self.wrap_pending = false;
        self.margin_top = 0;
        self.margin_bottom = self.screen_rows - 1;
        self.saved_cursor = SavedCursor::default();
        self.saved_position = None;
        self.cursor_visible = true;
        if hard {
            self.flush_alternate_cr();
            self.parked = None;
            self.alternate = None;
            self.normal_return = None;
            self.screen.clear();
            let row = self.new_row();
            self.screen.push_back(row);
            self.clear_history();
            self.cursor_row = 0;
            self.column = 0;
            self.cursor_shape = 0;
            self.cursor_blink = true;
            self.bracketed_paste = false;
            self.mouse_tracking = 0;
            self.mouse_sgr = false;
            self.mouse_generation = self.mouse_generation.wrapping_add(1);
            self.tab_stops = DEFAULT_TABS;
            self.sources
                .iter_mut()
                .for_each(|source| source.charset = Charset::default());
            *charset = Charset::default();
        }
    }

    fn clear_history(&mut self) {
        self.history_index = None;
        self.history.clear();
        self.history_bytes = 0;
        self.history_pending_units = 0;
        self.history_cell_capacity = 0;
    }

    fn take_page(&mut self) -> Page {
        Page {
            screen: std::mem::take(&mut self.screen),
            row: self.cursor_row,
            column: self.column,
            wrap: self.wrap_pending,
            columns: self.columns,
            rows: self.screen_rows,
        }
    }

    fn use_page(&mut self, page: Page) {
        self.screen = page.screen;
        self.cursor_row = page.row;
        self.column = page.column;
        self.wrap_pending = page.wrap;
    }

    fn select_screen(&mut self, source: &mut Source, mode: u32, enable: bool) -> Result<(), i32> {
        if enable && self.observe && self.active_source < 2 {
            self.observations.push(ReceiveObservation {
                scalar: 0,
                flags: 4,
            });
        }
        if self.parked.is_some() == enable {
            return Ok(());
        }
        self.cluster_row = 0;
        if enable {
            if self.logging.unwrap_or(0) & 2 != 0 {
                self.flush_normal_text();
            }
            if mode == 1049 {
                self.normal_return = Some(self.capture_cursor(source.charset));
                self.alternate = None;
            }
            self.parked = Some(self.take_page());
            if let Some(alternate) = self.alternate.take() {
                let changed =
                    alternate.columns != self.columns || alternate.rows != self.screen_rows;
                self.use_page(alternate);
                if changed {
                    self.clip_alternate(self.columns, self.screen_rows);
                }
            } else {
                let row = self.new_row();
                self.screen.push_back(row);
                self.cursor_row = 0;
                self.column = 0;
                self.wrap_pending = false;
            }
        } else {
            let parked = self.parked.as_ref().unwrap();
            let changed = parked.columns != self.columns || parked.rows != self.screen_rows;
            let planned = if changed {
                Some(reflow::normal(
                    self,
                    &parked.screen,
                    parked.row,
                    parked.column,
                    parked.wrap,
                    self.columns,
                )?)
            } else {
                None
            };
            self.flush_alternate_cr();
            let normal = self.parked.take().unwrap();
            self.alternate = if mode == 1047 {
                None
            } else {
                Some(self.take_page())
            };
            self.use_page(normal);
            if let Some(planned) = planned {
                self.apply_reflow(planned, self.columns, self.screen_rows);
            }
            if mode == 1049 {
                if let Some(saved) = self.normal_return.take() {
                    self.style = saved.style;
                    self.origin = saved.origin;
                    self.auto_wrap = saved.auto_wrap;
                    source.charset = saved.charset;
                }
            }
        }
        self.layout_epoch = self.layout_epoch.wrapping_add(1);
        Ok(())
    }

    fn escape(&mut self, source: &mut Source, final_byte: u8, command: Escape) -> Result<(), i32> {
        match command {
            Escape::Index(left) => self.move_down(left),
            Escape::ReverseIndex => {
                self.cluster_row = 0;
                self.wrap_pending = false;
                if self.cursor_row == self.margin_top {
                    self.scroll_region(self.margin_top, self.margin_bottom, 1, true);
                } else {
                    self.cursor_row = self.cursor_row.saturating_sub(1);
                }
            }
            Escape::Save => self.saved_cursor = self.capture_cursor(source.charset),
            Escape::Restore => self.restore_cursor(source, self.saved_cursor),
            Escape::Tab => self.tab_stops[self.column / 64] |= 1 << (self.column % 64),
            Escape::Keypad(application) => self.application_keypad = application,
            Escape::Reset => self.reset(&mut source.charset, true),
            Escape::Designate(slot, graphics) => {
                source.charset.designate(slot, graphics);
                self.operation_bytes(&[27, b'(' + slot, final_byte]);
                return Ok(());
            }
        }
        self.operation_bytes(&[27, final_byte]);
        Ok(())
    }

    fn csi(&mut self, source: &mut Source, final_byte: u8, command: Csi) -> Result<(), i32> {
        if let Csi::Reply(reply) = command {
            use std::io::Write;
            let length_at = self.replies.len();
            self.replies.push(0);
            match reply {
                Reply::Status => self.replies.extend_from_slice(b"\x1b[0n"),
                Reply::Attributes => self.replies.extend_from_slice(b"\x1b[?1;2c"),
                Reply::Position(private) => {
                    let prefix = if private { "?" } else { "" };
                    write!(
                        self.replies,
                        "\x1b[{}{};{}R",
                        prefix,
                        self.cursor_row as isize
                            - if self.origin {
                                self.margin_top as isize
                            } else {
                                0
                            }
                            + 1,
                        self.column + 1
                    )
                    .map_err(|_| 3)?;
                }
            }
            self.replies[length_at] = (self.replies.len() - length_at - 1) as u8;
            return Ok(());
        }
        if let Csi::CursorStyle(style) = command {
            self.cursor_shape = match style {
                3 | 4 => 1,
                5 | 6 => 2,
                _ => 0,
            };
            self.cursor_blink = matches!(style, 0 | 1 | 3 | 5);
        } else if matches!(command, Csi::Rendition) {
            if !apply_sgr(&source.parameters, &mut self.style) {
                return Ok(());
            }
        } else if let Csi::Modes(modes, set, private) = command {
            self.operation_bytes(if private { b"\x1b[?" } else { b"\x1b[" });
            for (index, mode) in modes.into_iter().flatten().enumerate() {
                match mode {
                    Mode::Wrap => {
                        self.auto_wrap = set;
                        if !set {
                            self.wrap_pending = false;
                        }
                    }
                    Mode::Cursor => self.cursor_visible = set,
                    Mode::Paste => self.bracketed_paste = set,
                    Mode::Newline => self.line_feed_returns = set,
                    Mode::Insert => self.insert = set,
                    Mode::ApplicationCursor => self.application_cursor = set,
                    Mode::Mouse(tracking) => {
                        self.mouse_tracking = if set { tracking } else { 0 };
                        self.mouse_generation = self.mouse_generation.wrapping_add(1);
                    }
                    Mode::MouseCoordinates => {
                        self.mouse_sgr = set;
                        self.mouse_generation = self.mouse_generation.wrapping_add(1);
                    }
                    Mode::Screen(mode) => self.select_screen(source, mode, set)?,
                    Mode::Origin => {
                        self.origin = set;
                        self.cursor_row = if set { self.margin_top } else { 0 };
                        self.column = 0;
                        self.cluster_row = 0;
                        self.wrap_pending = false;
                    }
                    Mode::SaveCursor => {
                        if set {
                            self.saved_cursor = self.capture_cursor(source.charset);
                        } else {
                            self.restore_cursor(source, self.saved_cursor);
                        }
                    }
                }
                if index != 0 {
                    self.operation_bytes(b";");
                }
                self.operation_bytes(mode.spelling());
            }
            self.operation_bytes(&[final_byte]);
            return Ok(());
        } else if matches!(command, Csi::Reset) {
            self.reset(&mut source.charset, false);
        } else if let Csi::Edit(v) = command {
            let count = v[0].max(1);
            let (top, bottom) = if self.origin {
                (self.margin_top, self.margin_bottom)
            } else {
                (0, self.screen_rows - 1)
            };
            match final_byte {
                b'H' | b'f' => {
                    self.cursor_row = (top + v[0].max(1) - 1).min(bottom);
                    self.column = v[1].max(1).min(self.columns) - 1;
                }
                b'A' => self.cursor_row = self.cursor_row.saturating_sub(count).max(top),
                b'B' => self.cursor_row = (self.cursor_row + count).min(bottom),
                b'C' => self.column = (self.column + count).min(self.columns - 1),
                b'D' => self.column = self.column.saturating_sub(count),
                b'E' => {
                    self.cursor_row = (self.cursor_row + count).min(bottom);
                    self.column = 0;
                }
                b'F' => {
                    self.cursor_row = self.cursor_row.saturating_sub(count).max(top);
                    self.column = 0;
                }
                b'G' | 96 => self.column = count.min(self.columns) - 1,
                b'd' => self.cursor_row = (top + count - 1).min(bottom),
                b'K' if v[0] <= 2 => {
                    self.cluster_row = 0;
                    self.ensure_cursor();
                    let (start, end) = match v[0] {
                        0 => (self.column, self.columns),
                        1 => (0, self.column + 1),
                        _ => (0, self.columns),
                    };
                    self.screen[self.cursor_row].erase(
                        start,
                        end,
                        self.style.erase(),
                        self.content_stamp,
                    );
                    self.operation_csi(&source.parameters, final_byte);
                    return Ok(());
                }
                b'X' => {
                    self.ensure_cursor();
                    self.screen[self.cursor_row].erase(
                        self.column,
                        self.column + count.min(self.columns - self.column),
                        self.style.erase(),
                        self.content_stamp,
                    );
                }
                b'@' | b'P' => {
                    self.ensure_cursor();
                    self.screen[self.cursor_row].shift_cells(
                        self.column,
                        count.min(self.columns - self.column),
                        self.columns,
                        final_byte == b'@',
                        self.style.erase(),
                        self.content_stamp,
                    );
                }
                b'L' | b'M' => {
                    if (self.margin_top..=self.margin_bottom).contains(&self.cursor_row) {
                        self.scroll_region(
                            self.cursor_row,
                            self.margin_bottom,
                            count,
                            final_byte == b'L',
                        );
                    }
                }
                b'S' | b'T' => {
                    self.scroll_region(
                        self.margin_top,
                        self.margin_bottom,
                        count,
                        final_byte == b'T',
                    );
                }
                b'r' => {
                    let margin_top = v[0].max(1) - 1;
                    let margin_bottom = if v[1] == 0 {
                        self.screen_rows - 1
                    } else {
                        v[1] - 1
                    };
                    if margin_top >= margin_bottom || margin_bottom >= self.screen_rows {
                        return Ok(());
                    }
                    self.margin_top = margin_top;
                    self.margin_bottom = margin_bottom;
                    self.cursor_row = if self.origin { margin_top } else { 0 };
                    self.column = 0;
                }
                b'I' | b'Z' => {
                    for _ in 0..count.min(self.columns) {
                        let next = if final_byte == b'I' {
                            self.next_tab(self.column)
                        } else {
                            self.previous_tab(self.column)
                        };
                        if next == self.column {
                            break;
                        }
                        self.column = next;
                    }
                }
                b'g' => {
                    if v[0] == 3 {
                        self.tab_stops = [0; 16];
                    } else {
                        self.tab_stops[self.column / 64] &= !(1 << (self.column % 64));
                    }
                }
                b's' => self.saved_position = Some((self.cursor_row, self.column)),
                b'u' => {
                    if let Some((row, column)) = self.saved_position {
                        self.cursor_row = row.min(self.screen_rows - 1);
                        self.column = column.min(self.columns - 1);
                    }
                }
                b'J' if v[0] == 2 => {
                    self.cluster_row = 0;
                    while self.screen.len() < self.screen_rows {
                        let row = self.new_row();
                        self.screen.push_back(row);
                    }
                    for row in &mut self.screen {
                        row.erase(0, self.columns, self.style.erase(), self.content_stamp);
                    }
                    self.operation_csi(&source.parameters, final_byte);
                    return Ok(());
                }
                b'J' if v[0] == 3 => {
                    if self.parked.is_none() {
                        self.clear_history();
                    }
                    self.operation_csi(&source.parameters, final_byte);
                    return Ok(());
                }
                b'J' if v[0] <= 1 => {
                    self.ensure_cursor();
                    for (index, row) in self.screen.iter_mut().enumerate() {
                        if index == self.cursor_row {
                            row.erase(
                                if v[0] == 0 { self.column } else { 0 },
                                if v[0] == 0 {
                                    self.columns
                                } else {
                                    self.column + 1
                                },
                                self.style.erase(),
                                self.content_stamp,
                            );
                        } else if (v[0] == 0 && index > self.cursor_row)
                            || (v[0] == 1 && index < self.cursor_row)
                        {
                            row.erase(0, self.columns, self.style.erase(), self.content_stamp);
                        }
                    }
                }
                _ => return Err(UNSUPPORTED),
            }
            self.cluster_row = 0;
            self.wrap_pending = false;
        }
        self.operation_csi(&source.parameters, final_byte);
        Ok(())
    }
    fn operation_csi(&mut self, p: &[u8], f: u8) {
        self.operation_bytes(&[27, 91]);
        self.operation_bytes(p);
        self.operation_bytes(&[f]);
    }
    pub fn screen_state(&self) -> ScreenState {
        let flags = (self.cursor_visible as u32)
            | ((self.parked.is_some() as u32) << 1)
            | ((self.application_cursor as u32) << 2)
            | ((self.application_keypad as u32) << 3)
            | ((self.bracketed_paste as u32) << 4);
        let mouse_tracking = self.mouse_tracking | ((self.mouse_sgr as u32) << 31);
        let mut hash = DefaultHasher::new();
        (
            self.layout_epoch,
            self.cursor_row,
            self.column,
            flags,
            self.cursor_shape,
            mouse_tracking,
            self.mouse_generation,
        )
            .hash(&mut hash);
        for row in &self.screen {
            (row.id, row.revision).hash(&mut hash);
        }
        ScreenState {
            layout_epoch: self.layout_epoch,
            revision: hash.finish() as i32,
            columns: self.columns as i32,
            rows: self.screen_rows as i32,
            cursor_row: self.cursor_row as i32,
            cursor_column: self.column as i32,
            flags,
            cursor_shape: self.cursor_shape,
            mouse_tracking,
            mouse_generation: self.mouse_generation,
        }
    }

    pub fn screen_row(&self, row: u32, first: u32, end: u32) -> Result<Option<ScreenRowView>, i32> {
        if row as usize >= self.screen_rows || first >= end || end as usize > self.columns {
            return Err(1);
        }
        let row = match self.screen.get(row as usize) {
            Some(row) => row,
            None => return Ok(None),
        };
        let first = if row
            .cells
            .get(first as usize)
            .map_or(false, |cell| cell.width < 0)
        {
            first - 1
        } else {
            first
        };
        let begin_format = row.formats.partition_point(|mark| mark.column < first);
        let end_format = row.formats.partition_point(|mark| {
            mark.column < end || (end as usize == self.columns && mark.column == end)
        });
        let cells =
            &row.cells[(first as usize).min(row.cells.len())..(end as usize).min(row.cells.len())];
        let formats = &row.formats[begin_format..end_format];
        Ok(Some(ScreenRowView {
            id: row.id,
            revision: row.revision,
            cells: cells.as_ptr(),
            arena: row.arena.as_ptr(),
            formats: formats.as_ptr(),
            cell_count: cells.len() as u32,
            format_count: formats.len() as u32,
            first,
            end,
        }))
    }

    pub fn snapshot_copy_required(&self) -> u64 {
        // Per row: snapshot slot and string headers, compact and expanded cell
        // slots, and UTF-16 units in the arena, row text and expanded strings.
        let history = self.parked.is_none().then(|| self.history.iter());
        256 + history
            .into_iter()
            .flatten()
            .chain(self.screen.iter())
            .map(|row| {
                384 + row.cells.len() as u64 * 154
                    + row.arena.len() as u64 * 6
                    + row.format_units() as u64 * 2
            })
            .sum::<u64>()
    }

    pub fn snapshot(&mut self) -> Result<SnapshotView, i32> {
        self.invalidate_prepared();
        self.snapshots += 1;
        self.view_rows.clear();
        let mut previous = None;
        let history_size = if self.parked.is_none() {
            self.history_bytes = 0;
            for row in &mut self.history {
                row.refresh_plain();
                normalize_row(row, &mut previous);
                self.view_rows.push(view(row));
                self.history_bytes += row.bytes();
            }
            self.history.len()
        } else {
            0
        };
        if let Some(first) = self.screen.front_mut() {
            if self.parked.is_some() {
                first.logical_id = first.id;
                first.logical_offset = 0;
                first.logical_cluster_offset = 0;
            }
        }
        for row in &mut self.screen {
            row.refresh_plain();
            normalize_row(row, &mut previous);
            self.view_rows.push(view(row));
        }
        let empty = history_size == 0
            && self.screen.len() == 1
            && self.screen[0].cells.is_empty()
            && self.screen[0].tabs.is_empty()
            && self.screen[0].formats.is_empty();
        self.update_accounting();
        Ok(SnapshotView {
            rows: self.view_rows.as_ptr(),
            row_count: if empty {
                0
            } else {
                self.view_rows.len() as u32
            },
            cursor_row: (history_size + self.cursor_row) as i32,
            cursor_column: self.column as i32,
            columns: self.columns as i32,
            screen_rows: self.screen_rows as i32,
            screen_start: history_size as i32,
            flags: (if self.cursor_visible { 1 } else { 0 })
                | (if self.cursor_blink { 2 } else { 0 })
                | (if self.parked.is_some() { 4 } else { 0 }),
            cursor_shape: self.cursor_shape,
            mouse_tracking: self.mouse_tracking | ((self.mouse_sgr as u32) << 31),
            mouse_generation: self.mouse_generation,
        })
    }
    #[cfg(test)]
    pub fn export(&mut self) -> Result<ExportState, i32> {
        #[cfg(windows)]
        if self.sources[0].legacy.is_some() {
            return Err(UNSUPPORTED);
        }
        let mut snapshot = self.snapshot()?;
        snapshot.screen_start = self.history.len() as i32;
        self.export_rows.clear();
        self.export_tabs.clear();
        let primary_count = self.history.len() + self.screen.len();
        let parked = self
            .parked
            .as_ref()
            .map_or(PageView::default(), |page| page.view(primary_count));
        let alternate = self.alternate.as_ref().map_or(PageView::default(), |page| {
            page.view(primary_count + parked.count as usize)
        });
        let rows = self
            .history
            .iter()
            .chain(&self.screen)
            .chain(self.parked.iter().flat_map(|page| &page.screen))
            .chain(self.alternate.iter().flat_map(|page| &page.screen));
        self.export_tabs
            .reserve(rows.clone().map(|r| r.tabs.len()).sum::<usize>());
        for row in rows {
            self.export_tabs
                .extend(row.tabs.iter().map(|&(start, end)| TabView {
                    start: start as u32,
                    end: end as u32,
                }));
        }
        let mut tab_index = 0;
        let mut previous: Option<(i64, i64, usize, bool)> = None;
        let last_history = self.history.len();
        let mut normal_previous = None;
        self.history_bytes = 0;
        let rows = self
            .history
            .iter_mut()
            .chain(&mut self.screen)
            .chain(self.parked.iter_mut().flat_map(|page| &mut page.screen))
            .chain(self.alternate.iter_mut().flat_map(|page| &mut page.screen));
        for (index, row) in rows.enumerate() {
            if parked.count != 0 && index == last_history {
                normal_previous = previous;
                previous = None;
            }
            if parked.count != 0 && index == primary_count {
                previous = normal_previous;
            }
            if alternate.count != 0 && index == alternate.first as usize {
                previous = None;
            }
            row.refresh_plain();
            if let Some((_, offset, count, soft)) = previous {
                row.logical_cluster_offset = if soft { offset + count as i64 } else { 0 };
            }
            let count = self.unicode.count_utf16(&row.plain);
            previous = Some((row.logical_id, row.logical_cluster_offset, count, row.soft));
            if index < last_history {
                self.history_bytes += row.bytes();
            }
            self.export_rows.push(ExportRow {
                view: view(row),
                tabs: unsafe { self.export_tabs.as_ptr().add(tab_index) },
                submitted: row.submitted.as_ref().map_or(ptr::null(), |s| s.as_ptr()),
                content_revision: row.content_revision,
                logical_cluster_offset: row.logical_cluster_offset,
                submitted_revision: row.submitted_revision,
                tab_count: row.tabs.len() as u32,
                submitted_length: row.submitted.as_ref().map_or(u32::MAX, |s| s.len() as u32),
                formats: row.formats.as_ptr(),
                format_count: row.formats.len() as u32,
                recorded: row.recorded_columns.as_ptr(),
                recorded_count: row.recorded_columns.len() as u32,
                excluded: row.excluded as u32,
            });
            tab_index += row.tabs.len();
        }
        for (index, source) in self.sources.iter().enumerate() {
            self.export_sources[index] = SourceView {
                pending: source.pending.as_ptr(),
                parameters: source.parameters.as_ptr(),
                last_graphic: source.last_graphic.as_ptr(),
                pending_count: source.pending_count as u32,
                parameter_count: source.parameters.len() as u32,
                last_graphic_count: source.last_graphic.len() as u32,
                flags: (source.started as u32)
                    | ((source.previous_cr as u32) << 1)
                    | ((source.last_was_graphic as u32) << 2)
                    | ((source.osc as u32) << 3)
                    | ((source.charset.0 as u32) << 4),
                escape: source.escape as u32,
                reserved: source.string_bytes as u32,
                pending_interval: source.pending_interval,
                escape_interval: source.escape_interval,
                previous_interval: source.previous_interval,
            };
        }
        self.export_editing = EditingView {
            margin_top: self.margin_top as u32,
            margin_bottom: self.margin_bottom as u32,
            position_row: self.saved_position.map_or(-1, |position| position.0 as i32),
            position_column: self.saved_position.map_or(0, |position| position.1 as u32),
            saved_row: self.saved_cursor.row as u32,
            saved_column: self.saved_cursor.column as u32,
            saved_fg: self.saved_cursor.style.fg,
            saved_bg: self.saved_cursor.style.bg,
            saved_style: self.saved_cursor.style.flags,
            saved_flags: (self.saved_cursor.origin as u32)
                | ((self.saved_cursor.auto_wrap as u32) << 1)
                | ((self.saved_cursor.wrap_pending as u32) << 2)
                | ((self.saved_cursor.charset.0 as u32) << 3),
            tab_stops: self.tab_stops,
            parked,
            alternate,
            normal_return: self
                .normal_return
                .map_or(CursorView::default(), |state| CursorView {
                    row: state.row as u32,
                    column: state.column as u32,
                    fg: state.style.fg,
                    bg: state.style.bg,
                    style: state.style.flags,
                    flags: (state.origin as u32)
                        | ((state.auto_wrap as u32) << 1)
                        | ((state.wrap_pending as u32) << 2)
                        | ((state.charset.0 as u32) << 3),
                    active: 1,
                }),
        };
        self.update_accounting();
        Ok(ExportState {
            snapshot,
            rows: self.export_rows.as_ptr(),
            sources: self.export_sources.as_ptr(),
            cluster: self.cluster.as_ptr(),
            recorded_cluster: self.position.cluster.as_ptr(),
            cluster_row: self.cluster_row,
            cluster_count: self.cluster.len() as u32,
            cluster_column: self.cluster_column as u32,
            cluster_width: self.cluster_width as u32,
            cursor_row: self.cursor_row as u32,
            style_foreground: self.style.fg,
            style_background: self.style.bg,
            style_flags: self.style.flags,
            cluster_foreground: self.cluster_style.fg,
            cluster_background: self.cluster_style.bg,
            cluster_flags: self.cluster_style.flags,
            mode_flags: (self.auto_wrap as u32)
                | ((self.line_feed_returns as u32) << 1)
                | ((self.wrap_pending as u32) << 2)
                | ((self.finished as u32) << 3)
                | ((self.bracketed_paste as u32) << 4)
                | ((self.application_cursor as u32) << 5)
                | ((self.application_keypad as u32) << 6)
                | ((self.origin as u32) << 7)
                | ((self.insert as u32) << 8),
            recorded_column: self.position.column as u32,
            recorded_known: (!self.position.initialized || self.position.known) as u32,
            recorded_count: self.position.cluster.len() as u32,
            row_count: primary_count as u32,
            source_count: 3,
            reserved0: self.export_rows.len() as u32,
            reserved1: self.alternate_cr.map_or(0, |source| source as u32 + 1),
            editing: &self.export_editing,
        })
    }

    pub fn creation_required(columns: usize, rows: usize, retained: usize) -> u64 {
        // VecDeque may round to a power-of-two allocation on older Rust toolchains.
        let row_capacity = (retained + 1).next_power_of_two() + (rows + 1).next_power_of_two() + 4;
        (size_of::<Self>() + row_capacity * size_of::<Row>() + columns * 80 + 128 * 1024) as u64
    }
    pub fn set_limit(&mut self, limit: u64) -> Result<(), i32> {
        if limit < self.quota {
            return Err(1);
        }
        self.limit = limit;
        Ok(())
    }
    fn invalidate_prepared(&mut self) {
        self.generation = self.generation.wrapping_add(1);
        self.prepared.valid = false;
    }
    pub fn preflight(
        &mut self,
        operation: u32,
        source: usize,
        payload: &[u8],
        flags: u32,
    ) -> Result<u64, i32> {
        let prepared = &self.prepared;
        if prepared.valid
            && prepared.generation == self.generation
            && prepared.operation == operation
            && prepared.source == source
            && prepared.flags == flags
            && prepared.length == payload.len()
            && prepared.payload[..prepared.length] == *payload
        {
            return Ok(prepared.required);
        }
        self.prepared.valid = false;
        let required = self.estimate_operation(operation, source, payload, flags)?;
        if payload.len() <= self.prepared.payload.len() {
            self.prepared.payload[..payload.len()].copy_from_slice(payload);
            self.prepared.generation = self.generation;
            self.prepared.operation = operation;
            self.prepared.source = source;
            self.prepared.flags = flags;
            self.prepared.length = payload.len();
            self.prepared.required = required;
            self.prepared.valid = true;
        }
        Ok(required)
    }
    fn estimate_operation(
        &self,
        operation: u32,
        source: usize,
        payload: &[u8],
        flags: u32,
    ) -> Result<u64, i32> {
        if self.failed && operation <= 2 {
            return Err(UNSUPPORTED);
        }
        let pending_replies = (flags >> 8) as usize;
        let last_operation = if cfg!(test) { 4 } else { 3 };
        if operation > last_operation
            || source > 2
            || flags & 0xf8 != 0
            || pending_replies > MAX_REPLIES
        {
            return Err(1);
        }
        let observe = flags & 4 != 0;
        let flags = flags & 3;
        if operation <= 2 {
            if self.logging.map_or(false, |prior| prior != flags) {
                return Err(UNSUPPORTED);
            }
        }
        let mut revisits_rows = false;
        let mut row_moves = 0;
        let mut repeated_bytes = 0;
        let mut screen_changes = 0;
        if operation == 0 {
            if self.finished || payload.len() > 1024 * 1024 {
                return Err(UNSUPPORTED);
            }
            let mut probe = Probe::from_source(&self.sources[source], self);
            probe.decode_source(&self.sources[source], payload, false)?;
            if probe.reply_count + pending_replies > MAX_REPLIES {
                return Err(4);
            }
            revisits_rows = probe.revisits_rows;
            row_moves = probe.row_moves.min(self.retained) as u64;
            repeated_bytes = probe.repeated_bytes as u64;
            screen_changes = probe.screen_changes as u64;
        } else if operation == 1 {
            let mut probe = Probe::from_source(&self.sources[0], self);
            probe.decode_source(&self.sources[0], &[], true)?;
            probe.replace_source(&self.sources[1]);
            probe.decode_source(&self.sources[1], &[], true)?;
        }
        let owned = self.quota;
        if operation == 2 && flags & 2 == 0 {
            return Ok(owned);
        }
        // Screen edits can change an already submitted row before a later LF.
        // Include its existing tail, even though the old revision is recorded.
        let pending = self.history_pending_units
            + self
                .screen
                .iter()
                .enumerate()
                .map(|(index, row)| {
                    if operation == 0 && (revisits_rows || index >= self.cursor_row) {
                        row.potential_units()
                    } else {
                        row.pending_units()
                    }
                })
                .sum::<u64>()
            + self
                .parked
                .iter()
                .flat_map(|p| &p.screen)
                .map(Row::potential_units)
                .sum::<u64>();
        let required = if operation <= 2 {
            let bytes = if operation == 0 {
                payload.len() as u64 + repeated_bytes
            } else if operation == 1 && !self.finished {
                16
            } else {
                0
            };
            let line_feeds = if operation == 0 {
                payload.iter().filter(|&&b| b == 10).count() as u64
            } else {
                0
            };
            let text_effect = if flags & 2 != 0 {
                (pending + bytes) * (line_feeds + screen_changes + 1) * 3 + 2
            } else {
                0
            };
            let terminal_effect = if flags & 1 != 0 { bytes * 3 + 8192 } else { 0 };
            let recording = text_effect.min(RECORDING_EFFECT_LIMIT as u64)
                + terminal_effect.min(RECORDING_EFFECT_LIMIT as u64);
            let observations = if observe {
                (2 * payload.len() as u64 + 64) * 32
            } else {
                0
            };
            let row_growth = if operation <= 1 {
                (bytes + self.screen_rows as u64 + row_moves + screen_changes + 1)
                    * (self.columns as u64 * 80 + size_of::<Row>() as u64 + 128)
                    * 2
            } else {
                0
            };
            let reflow_growth = self
                .parked
                .as_ref()
                .filter(|page| {
                    screen_changes != 0
                        && (page.columns != self.columns || page.rows != self.screen_rows)
                })
                .map_or(0, |page| {
                    self.reflow_required(&page.screen, self.columns) - owned
                });
            owned * 5
                + row_growth
                + reflow_growth
                + bytes * 8192
                + recording * 4
                + observations
                + 128 * 1024
        } else {
            let row_list = self
                .history
                .iter()
                .chain(self.screen.iter())
                .chain(self.parked.iter().flat_map(|p| &p.screen))
                .chain(self.alternate.iter().flat_map(|p| &p.screen));
            let rows = row_list.clone().count() as u64;
            let text = row_list
                .clone()
                .filter(|r| r.dirty)
                .map(|r| (r.arena.len() + r.cells.len() + r.format_units()) as u64)
                .sum::<u64>();
            let snapshot = owned + text * 4 + rows * size_of::<RowView>() as u64 * 2 + 128 * 1024;
            #[cfg(test)]
            if operation == 4 {
                let tabs = row_list.map(|r| r.tabs.len() as u64).sum::<u64>();
                return Ok(snapshot
                    + rows * size_of::<ExportRow>() as u64 * 2
                    + tabs * size_of::<TabView>() as u64 * 2);
            }
            snapshot
        };
        Ok(required)
    }
    pub fn admit(
        &mut self,
        operation: u32,
        source: usize,
        payload: &[u8],
        flags: u32,
    ) -> Result<(), i32> {
        let required = self.preflight(operation, source, payload, flags)?;
        if required > self.limit {
            Err(UNSUPPORTED)
        } else {
            Ok(())
        }
    }

    pub fn owned_bytes(&self) -> u64 {
        self.accounted
    }
    pub fn quota_bytes(&self) -> u64 {
        self.quota
    }
}
impl Drop for Model {
    fn drop(&mut self) {
        account(self.accounted, 0);
        LIVE_MODELS.fetch_sub(1, Ordering::Relaxed);
    }
}
fn normalize_row(row: &mut Row, previous: &mut Option<(i64, i64, usize, bool)>) {
    if let Some((id, offset, len, soft)) = *previous {
        row.logical_id = if soft { id } else { row.id };
        row.logical_offset = if soft { offset + len as i64 } else { 0 };
    }
    *previous = Some((
        row.logical_id,
        row.logical_offset,
        row.plain.len(),
        row.soft,
    ));
}
fn view(r: &Row) -> RowView {
    RowView {
        id: r.id,
        logical_id: r.logical_id,
        logical_offset: r.logical_offset,
        display_revision: r.revision,
        cells: r.cells.as_ptr(),
        text: r.plain.as_ptr(),
        cell_text: r.arena.as_ptr(),
        cell_count: r.cells.len() as u32,
        text_length: r.plain.len() as u32,
        cell_text_length: r.arena.len() as u32,
        soft_wrap: r.soft as u32,
    }
}
fn submit(
    row: &mut Row,
    boundary: bool,
    output: &mut RecordingBuffer,
    pos: &mut RecordedPosition,
    u: &Unicode,
    ambiguous: usize,
) -> bool {
    if row.excluded || output.refused {
        return false;
    }
    if row.tabs.is_empty()
        && row.submitted.is_some()
        && row.submitted_revision == row.content_revision
    {
        return false;
    }
    let revision = row.serialize(true, None);
    if row
        .submitted
        .as_ref()
        .map_or(false, |prior| prior == &revision)
    {
        row.submitted_revision = row.content_revision;
        row.submitted_tab_revision = row.tab_revision;
        return false;
    }
    let serialized = if row.tabs.is_empty() && row.recorded_columns.is_empty() {
        None
    } else {
        Some(row.serialize_columns(true, pos.column(), !row.recorded_columns.is_empty()))
    };
    for c in char::decode_utf16(serialized.as_ref().unwrap_or(&revision).iter().copied()) {
        let scalar = c.unwrap_or(char::REPLACEMENT_CHARACTER) as u32;
        output.scalar(scalar);
        if output.refused {
            break;
        }
        pos.append(scalar, u, ambiguous);
    }
    if !revision.is_empty() || boundary {
        row.submitted = Some(revision);
        row.submitted_revision = row.content_revision;
        row.submitted_tab_revision = row.tab_revision;
    }
    row.recorded_columns.clear();
    true
}

// Match the managed parser: ESC inside OSC ends the ignored string and the
// next scalar is interpreted as an ordinary ESC continuation, not only ST.
fn consume_control_string(
    escape: &mut u8,
    osc: bool,
    string_bytes: &mut usize,
    scalar: u32,
) -> Result<bool, i32> {
    if *escape == 4 {
        *escape = 1;
        return Ok(false);
    }
    if *escape != 3 {
        return Ok(false);
    }
    if matches!(scalar, 24 | 26) || (scalar == 7 && osc) {
        *escape = 0;
    } else if scalar == 27 {
        *escape = 4;
    } else {
        *string_bytes += char::from_u32(scalar)
            .unwrap_or(char::REPLACEMENT_CHARACTER)
            .len_utf8();
        if *string_bytes > 1024 * 1024 {
            return Err(RESOURCE);
        }
    }
    Ok(true)
}

fn is_control(scalar: u32) -> bool {
    scalar < 32 || (127..=159).contains(&scalar)
}

enum Unit {
    Ignored,
    Bell,
    Graphic,
    Control,
    Escape(Escape),
    Csi(Csi),
}

enum Escape {
    Index(bool),
    ReverseIndex,
    Save,
    Restore,
    Tab,
    Keypad(bool),
    Reset,
    Designate(u8, bool),
}

enum Parameters<'a> {
    Live(&'a mut Vec<u8>),
    Preview(&'a mut [u8; 4096], &'a mut usize),
}

impl Parameters<'_> {
    fn as_slice(&self) -> &[u8] {
        match self {
            Self::Live(bytes) => bytes,
            Self::Preview(bytes, length) => &bytes[..**length],
        }
    }

    fn clear(&mut self) {
        match self {
            Self::Live(bytes) => bytes.clear(),
            Self::Preview(_, length) => **length = 0,
        }
    }

    fn push(&mut self, byte: u8) -> Result<(), i32> {
        if self.as_slice().len() == 4096 {
            return Err(RESOURCE);
        }
        match self {
            Self::Live(bytes) => bytes.push(byte),
            Self::Preview(bytes, length) => {
                bytes[**length] = byte;
                **length += 1;
            }
        }
        Ok(())
    }
}

fn interpret_scalar(
    scalar: u32,
    escape: &mut u8,
    osc: &mut bool,
    string_bytes: &mut usize,
    mut parameters: Parameters<'_>,
) -> Result<Unit, i32> {
    if *escape == 4 {
        parameters.clear();
    }
    if consume_control_string(escape, *osc, string_bytes, scalar)? {
        return Ok(Unit::Ignored);
    }
    match scalar {
        27 => {
            *escape = 1;
            parameters.clear();
            return Ok(Unit::Ignored);
        }
        24 | 26 => {
            *escape = 0;
            return Ok(Unit::Ignored);
        }
        7 => return Ok(Unit::Bell),
        8..=15 => return Ok(Unit::Control),
        _ if is_control(scalar) => return Ok(Unit::Ignored),
        _ => {}
    }
    match *escape {
        0 => return Ok(Unit::Graphic),
        1 => {
            *escape = 0;
            match scalar {
                91 => *escape = 2,
                93 | 80 | 88 | 94 | 95 => {
                    *escape = 3;
                    *osc = scalar == 93;
                    *string_bytes = 0;
                }
                92 => {}
                68 | 69 => return Ok(Unit::Escape(Escape::Index(scalar == 69))),
                77 => return Ok(Unit::Escape(Escape::ReverseIndex)),
                55 => return Ok(Unit::Escape(Escape::Save)),
                56 => return Ok(Unit::Escape(Escape::Restore)),
                72 => return Ok(Unit::Escape(Escape::Tab)),
                61 | 62 => return Ok(Unit::Escape(Escape::Keypad(scalar == 61))),
                99 => return Ok(Unit::Escape(Escape::Reset)),
                0x20..=0x2f => {
                    parameters.push(scalar as u8)?;
                    *escape = 5;
                }
                _ => {}
            }
        }
        5 if (0x20..=0x2f).contains(&scalar) => parameters.push(scalar as u8)?,
        5 => {
            *escape = 0;
            let slot = match parameters.as_slice() {
                b"(" => 0,
                b")" => 1,
                _ => return Ok(Unit::Ignored),
            };
            if matches!(scalar, 48 | 66) {
                return Ok(Unit::Escape(Escape::Designate(slot, scalar == 48)));
            }
        }
        _ if (0x40..=0x7e).contains(&scalar) => {
            *escape = 0;
            return parse_csi(parameters.as_slice(), scalar as u8);
        }
        _ if (0x20..=0x3f).contains(&scalar) => parameters.push(scalar as u8)?,
        _ => *escape = 0,
    }
    Ok(Unit::Ignored)
}

// This preflight copies only decoder/parser state into fixed stack storage.
// It neither edits terminal rows nor emits/consumes log buffers.
struct Probe<'a> {
    unicode: &'a Unicode,
    initial_screen: &'a VecDeque<Row>,
    extra_screen: Option<&'a VecDeque<Row>>,
    alternate_active: bool,
    screen_changes: usize,
    return_charset: Option<Charset>,
    formats_remaining: Option<usize>,
    source_graphic: [u32; 256],
    source_count: usize,
    source_was_graphic: bool,
    cluster: [u32; 256],
    cluster_count: usize,
    cluster_active: bool,
    charset: Charset,
    saved_charset: Charset,
    revisits_rows: bool,
    row_moves: usize,
    reply_count: usize,
    repeated_bytes: usize,
    pending: [u8; 4],
    pending_count: usize,
    started: bool,
    escape: u8,
    osc: bool,
    string_bytes: usize,
    parameters: [u8; 4096],
    parameter_count: usize,
}
impl<'a> Probe<'a> {
    fn from_source(source: &Source, model: &'a Model) -> Self {
        let mut result = Self {
            unicode: &model.unicode,
            initial_screen: &model.screen,
            extra_screen: model
                .parked
                .as_ref()
                .or(model.alternate.as_ref())
                .map(|p| &p.screen),
            alternate_active: model.parked.is_some(),
            screen_changes: 0,
            return_charset: model.normal_return.map(|state| state.charset),
            formats_remaining: None,
            source_graphic: [0; 256],
            source_count: source.last_graphic.len(),
            source_was_graphic: source.last_was_graphic,
            cluster: [0; 256],
            cluster_count: model.cluster.len(),
            cluster_active: model.cluster_row != 0,
            charset: source.charset,
            saved_charset: model.saved_cursor.charset,
            revisits_rows: false,
            row_moves: 0,
            reply_count: 0,
            repeated_bytes: 0,
            pending: source.pending,
            pending_count: source.pending_count,
            started: source.started,
            escape: source.escape,
            osc: source.osc,
            string_bytes: source.string_bytes,
            parameters: [0; 4096],
            parameter_count: source.parameters.len(),
        };
        result.parameters[..source.parameters.len()].copy_from_slice(&source.parameters);
        result.source_graphic[..source.last_graphic.len()].copy_from_slice(&source.last_graphic);
        result.cluster[..model.cluster.len()].copy_from_slice(&model.cluster);
        result
    }
    fn replace_source(&mut self, source: &Source) {
        self.pending = source.pending;
        self.pending_count = source.pending_count;
        self.started = source.started;
        self.escape = source.escape;
        self.osc = source.osc;
        self.string_bytes = source.string_bytes;
        self.parameter_count = source.parameters.len();
        self.parameters[..self.parameter_count].copy_from_slice(&source.parameters);
        self.source_count = source.last_graphic.len();
        self.source_was_graphic = source.last_was_graphic;
        self.charset = source.charset;
        self.source_graphic[..self.source_count].copy_from_slice(&source.last_graphic);
    }
    fn graphic(&mut self, c: u32) -> Result<(), i32> {
        if !self.source_was_graphic
            || !self
                .unicode
                .joins(&self.source_graphic[..self.source_count], c)
        {
            self.source_count = 0;
        }
        if self.source_count == 256 {
            return Err(UNSUPPORTED);
        }
        self.source_graphic[self.source_count] = c;
        self.source_count += 1;
        self.source_was_graphic = true;
        if c == 0xfeff {
            // Charge all marks to the busiest starting cell. Cursor movement can
            // only spread them across more cells during this operation.
            let remaining = self.formats_remaining.get_or_insert_with(|| {
                let mut maximum = 0;
                for row in self
                    .initial_screen
                    .iter()
                    .chain(self.extra_screen.into_iter().flatten())
                {
                    let mut previous = u32::MAX;
                    let mut run = 0;
                    for mark in &row.formats {
                        run = if mark.column == previous { run + 1 } else { 1 };
                        previous = mark.column;
                        maximum = maximum.max(run);
                    }
                }
                256usize.saturating_sub(maximum)
            });
            *remaining = remaining.checked_sub(1).ok_or(RESOURCE)?;
            self.cluster_active = false;
            return Ok(());
        }
        if !self.cluster_active || !self.unicode.joins(&self.cluster[..self.cluster_count], c) {
            self.cluster_count = 0;
        }
        if self.cluster_count == 256 {
            return Err(UNSUPPORTED);
        }
        self.cluster[self.cluster_count] = c;
        self.cluster_count += 1;
        self.cluster_active = true;
        Ok(())
    }
    fn decode_source(&mut self, _source: &Source, payload: &[u8], flush: bool) -> Result<(), i32> {
        #[cfg(windows)]
        if let Some(legacy) = &_source.legacy {
            let mut batch = || {
                let mut preview = legacy.clone_state()?;
                for byte in payload
                    .iter()
                    .copied()
                    .map(Some)
                    .chain(flush.then_some(None))
                {
                    for item in preview
                        .decode_byte(byte, self.escape != 0, 0)?
                        .into_scalars()
                    {
                        self.emit(item.scalar)?;
                    }
                }
                Ok(())
            };
            return if legacy.requires_apartment() {
                windows_icu::in_apartment(batch)
            } else {
                batch()
            };
        }
        self.decode(payload, flush)
    }

    fn decode(&mut self, payload: &[u8], flush: bool) -> Result<(), i32> {
        let mut offset = 0;
        while offset < payload.len() {
            if self.pending_count == 0
                && self.escape == 0
                && !self.charset.graphics()
                && (0x20..=0x7e).contains(&payload[offset])
            {
                let first = offset;
                while offset < payload.len() && (0x20..=0x7e).contains(&payload[offset]) {
                    offset += 1;
                }
                // The first ASCII scalar may still join a preceding Prepend
                // cluster, including its size limit. Every subsequent pair
                // of printable ASCII scalars is an unconditional boundary.
                self.emit(payload[first] as u32)?;
                if offset - first > 1 {
                    let last = payload[offset - 1] as u32;
                    self.source_graphic[0] = last;
                    self.source_count = 1;
                    self.source_was_graphic = true;
                    self.cluster[0] = last;
                    self.cluster_count = 1;
                    self.cluster_active = true;
                }
                continue;
            }
            let byte = payload[offset];
            offset += 1;
            for scalar in decode_utf8_byte(&mut self.pending, &mut self.pending_count, byte)
                .map_err(|_| UNSUPPORTED)?
            {
                self.emit(scalar)?;
            }
        }
        if let Some(scalar) = finish_utf8(flush, &mut self.pending_count) {
            self.emit(scalar)?;
        }
        Ok(())
    }
    fn emit(&mut self, c: u32) -> Result<(), i32> {
        if suppress_initial_bom(&mut self.started, c) {
            return Ok(());
        }
        if is_control(c) {
            self.source_was_graphic = false;
        }
        let unit = interpret_scalar(
            c,
            &mut self.escape,
            &mut self.osc,
            &mut self.string_bytes,
            Parameters::Preview(&mut self.parameters, &mut self.parameter_count),
        )
        .map_err(|_| UNSUPPORTED)?;
        match unit {
            Unit::Ignored | Unit::Bell | Unit::Csi(Csi::Rendition | Csi::CursorStyle(_)) => {}
            Unit::Escape(command) => match command {
                Escape::Designate(slot, graphics) => self.charset.designate(slot, graphics),
                Escape::Save => self.saved_charset = self.charset,
                Escape::Restore => {
                    self.charset = self.saved_charset;
                    self.cluster_active = false;
                }
                Escape::Index(_) | Escape::ReverseIndex => self.cluster_active = false,
                Escape::Reset => {
                    self.charset = Charset::default();
                    self.saved_charset = Charset::default();
                    self.cluster_active = false;
                    self.alternate_active = false;
                    self.return_charset = None;
                }
                Escape::Tab | Escape::Keypad(_) => {}
            },
            Unit::Csi(Csi::Modes(modes, set, _)) => {
                for mode in modes.into_iter().flatten() {
                    match mode {
                        Mode::Origin => self.cluster_active = false,
                        Mode::SaveCursor if set => self.saved_charset = self.charset,
                        Mode::SaveCursor => {
                            self.charset = self.saved_charset;
                            self.cluster_active = false;
                        }
                        Mode::Screen(mode) if set != self.alternate_active => {
                            self.alternate_active = set;
                            if mode == 1049 {
                                if set {
                                    self.return_charset = Some(self.charset);
                                } else if let Some(saved) = self.return_charset.take() {
                                    self.charset = saved;
                                }
                            }
                            self.cluster_active = false;
                            self.revisits_rows = true;
                            self.screen_changes += 1;
                        }
                        _ => {}
                    }
                }
            }
            Unit::Csi(Csi::Reset) => {
                self.saved_charset = Charset::default();
                self.cluster_active = false;
            }
            Unit::Csi(Csi::Reply(_)) => self.reply_count += 1,
            Unit::Csi(Csi::Repeat(count)) => {
                let mut spelling = [0; 256];
                let spelling =
                    copy_graphic(&self.source_graphic[..self.source_count], &mut spelling);
                if spelling.is_empty() {
                    return Ok(());
                }
                let bytes: usize = spelling
                    .iter()
                    .map(|&scalar| char::from_u32(scalar).unwrap().len_utf8())
                    .sum();
                self.repeated_bytes += bytes * count;
                if self.repeated_bytes > 1024 * 1024 {
                    return Err(RESOURCE);
                }
                for _ in 0..count {
                    for &scalar in spelling {
                        self.graphic(scalar)?;
                    }
                }
            }
            Unit::Graphic => return self.graphic(self.charset.graphic(c)),
            Unit::Control if c == 14 || c == 15 => self.charset.invoke(c == 14),
            Unit::Control => self.cluster_active = false,
            Unit::Csi(Csi::Edit(values)) => {
                let final_byte = c as u8;
                self.revisits_rows |= !matches!(final_byte, b'J' | b'K' | b'X');
                if matches!(final_byte, b'L' | b'M' | b'S' | b'T') {
                    self.row_moves = self.row_moves.saturating_add(values[0].max(1));
                }
                if final_byte != b'J' || values[0] != 3 {
                    self.cluster_active = false;
                }
            }
        }
        Ok(())
    }
}

#[derive(Clone, Copy)]
enum Mode {
    Wrap,
    Cursor,
    Paste,
    Newline,
    Origin,
    Insert,
    ApplicationCursor,
    SaveCursor,
    Mouse(u32),
    MouseCoordinates,
    Screen(u32),
}
impl Mode {
    fn spelling(self) -> &'static [u8] {
        match self {
            Self::Wrap => b"7",
            Self::Cursor => b"25",
            Self::Paste => b"2004",
            Self::Newline => b"20",
            Self::Origin => b"6",
            Self::Insert => b"4",
            Self::ApplicationCursor => b"1",
            Self::SaveCursor => b"1048",
            Self::Mouse(9) => b"9",
            Self::Mouse(1000) => b"1000",
            Self::Mouse(1002) => b"1002",
            Self::Mouse(1003) => b"1003",
            Self::Mouse(_) => unreachable!(),
            Self::MouseCoordinates => b"1006",
            Self::Screen(47) => b"47",
            Self::Screen(1047) => b"1047",
            Self::Screen(1049) => b"1049",
            Self::Screen(_) => unreachable!(),
        }
    }
}

enum Csi {
    Rendition,
    Modes([Option<Mode>; 32], bool, bool),
    Reset,
    Edit([usize; 2]),
    Reply(Reply),
    Repeat(usize),
    CursorStyle(usize),
}

const MAX_REPLIES: usize = 128;

enum Reply {
    Status,
    Position(bool),
    Attributes,
}

fn parse_csi(parameters: &[u8], final_byte: u8) -> Result<Unit, i32> {
    if final_byte == b'p' && parameters == b"!" {
        return Ok(Unit::Csi(Csi::Reset));
    }
    if final_byte == b'q' {
        let style = parameters
            .strip_suffix(b" ")
            .filter(|value| !value.is_empty())
            .and_then(number);
        return Ok(match style {
            Some(style @ 0..=6) => Unit::Csi(Csi::CursorStyle(style)),
            _ => Unit::Ignored,
        });
    }
    let reply = match (final_byte, parameters) {
        (b'n', b"5") => Some(Reply::Status),
        (b'n', b"6") => Some(Reply::Position(false)),
        (b'n', b"?6") => Some(Reply::Position(true)),
        (b'c', b"" | b"0") => Some(Reply::Attributes),
        _ => None,
    };
    if let Some(reply) = reply {
        return Ok(Unit::Csi(Csi::Reply(reply)));
    }
    if final_byte == b'm' {
        let mut groups = 0;
        for parameter in parameters.split(|&byte| byte == b';') {
            groups += 1;
            if groups > 32 || parameter.iter().filter(|&&byte| byte == b':').count() >= 16 {
                return Err(RESOURCE);
            }
        }
        let valid = parameters
            .iter()
            .all(|byte| byte.is_ascii_digit() || matches!(byte, b';' | b':'));
        return Ok(if valid {
            Unit::Csi(Csi::Rendition)
        } else {
            Unit::Ignored
        });
    }
    if matches!(final_byte, b'h' | b'l') {
        let private = parameters.starts_with(b"?");
        let mut modes = [None; 32];
        let mut any = false;
        for (index, parameter) in parameters[private as usize..]
            .split(|&byte| byte == b';')
            .enumerate()
        {
            let slot = modes.get_mut(index).ok_or(RESOURCE)?;
            if !parameter.iter().all(u8::is_ascii_digit) {
                return Ok(Unit::Ignored);
            }
            *slot = match (private, parameter) {
                (true, b"7") => Some(Mode::Wrap),
                (true, b"25") => Some(Mode::Cursor),
                (true, b"2004") => Some(Mode::Paste),
                (true, b"1") => Some(Mode::ApplicationCursor),
                (true, b"6") => Some(Mode::Origin),
                (true, b"1048") => Some(Mode::SaveCursor),
                (true, b"9") => Some(Mode::Mouse(9)),
                (true, b"1000") => Some(Mode::Mouse(1000)),
                (true, b"1002") => Some(Mode::Mouse(1002)),
                (true, b"1003") => Some(Mode::Mouse(1003)),
                (true, b"1006") => Some(Mode::MouseCoordinates),
                (true, b"47") => Some(Mode::Screen(47)),
                (true, b"1047") => Some(Mode::Screen(1047)),
                (true, b"1049") => Some(Mode::Screen(1049)),
                (false, b"4") => Some(Mode::Insert),
                (false, b"20") => Some(Mode::Newline),
                _ => None,
            };
            any |= slot.is_some();
        }
        return Ok(if any {
            Unit::Csi(Csi::Modes(modes, final_byte == b'h', private))
        } else {
            Unit::Ignored
        });
    }
    let mut values = [0usize; 2];
    let mut index = 0;
    for &byte in parameters {
        if byte.is_ascii_digit() {
            values[index] = (values[index] * 10 + (byte - b'0') as usize).min(1024);
        } else if byte == b';' && index == 0 && matches!(final_byte, b'H' | b'f' | b'r') {
            index = 1;
        } else {
            return Ok(Unit::Ignored);
        }
    }
    match final_byte {
        b'H' | b'f' | b'A' | b'B' | b'C' | b'D' | b'E' | b'F' | b'G' | 96 | b'd' | b'X' | b'@'
        | b'P' | b'L' | b'M' | b'S' | b'T' | b'r' | b'I' | b'Z' => {}
        b'g' if matches!(values[0], 0 | 3) => {}
        b's' | b'u' if parameters.is_empty() => {}
        b'K' if values[0] <= 2 => {}
        b'J' if values[0] <= 3 => {}
        b'b' => return Ok(Unit::Csi(Csi::Repeat(values[0].max(1)))),
        _ => return Ok(Unit::Ignored),
    }
    Ok(Unit::Csi(Csi::Edit(values)))
}

fn copy_graphic<'a>(source: &[u32], copy: &'a mut [u32; 256]) -> &'a [u32] {
    let destination = &mut copy[..source.len()];
    destination.copy_from_slice(source);
    destination
}
#[inline]
fn decode_utf8_byte(
    pending: &mut [u8; 4],
    count: &mut usize,
    byte: u8,
) -> Result<impl Iterator<Item = u32>, i32> {
    let mut output = [0; 4];
    if *count == 0 && byte.is_ascii() {
        output[0] = byte as u32;
        return Ok(output.into_iter().take(1));
    }
    if *count == pending.len() {
        return Err(RESOURCE);
    }
    pending[*count] = byte;
    *count += 1;
    let mut emitted = 0;
    while *count != 0 {
        let (scalar, used) = match std::str::from_utf8(&pending[..*count]) {
            Ok(value) => {
                let scalar = value.chars().next().unwrap();
                (scalar as u32, scalar.len_utf8())
            }
            Err(error) => match error.error_len() {
                Some(used) => (0xfffd, used),
                None => break,
            },
        };
        output[emitted] = scalar;
        emitted += 1;
        pending.copy_within(used..*count, 0);
        *count -= used;
    }
    Ok(output.into_iter().take(emitted))
}

fn finish_utf8(flush: bool, count: &mut usize) -> Option<u32> {
    if flush && *count != 0 {
        *count = 0;
        Some(0xfffd)
    } else {
        None
    }
}

fn suppress_initial_bom(started: &mut bool, scalar: u32) -> bool {
    !std::mem::replace(started, true) && scalar == 0xfeff
}

fn number(bytes: &[u8]) -> Option<usize> {
    let mut number = 0usize;
    for &b in bytes {
        if !b.is_ascii_digit() {
            return None;
        }
        number = number.checked_mul(10)?.checked_add((b - b'0') as usize)?;
        if number > i32::MAX as usize {
            return None;
        }
    }
    Some(number)
}
const PALETTE: [u32; 16] = [
    0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5, 0x7f7f7f,
    0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff,
];
fn color(fields: &[&[u8]], colon: bool) -> Option<u32> {
    if fields.len() == 2 && fields[0] == b"5" && !fields[1].is_empty() {
        let index = number(fields[1])?;
        if index > 255 {
            return None;
        }
        if index < 16 {
            return Some(PALETTE[index]);
        }
        if index < 232 {
            let index = index - 16;
            let levels = [0, 95, 135, 175, 215, 255];
            return Some(levels[index / 36] << 16 | levels[index / 6 % 6] << 8 | levels[index % 6]);
        }
        return Some((8 + 10 * (index as u32 - 232)) * 0x010101);
    }
    let fields = if fields.len() == 5
        && colon
        && fields[0] == b"2"
        && (fields[1].is_empty() || fields[1] == b"0")
    {
        &fields[1..]
    } else if fields.len() == 4 && fields[0] == b"2" {
        fields
    } else {
        return None;
    };
    if fields[1..].iter().any(|v| v.is_empty()) {
        return None;
    }
    let (r, g, b) = (number(fields[1])?, number(fields[2])?, number(fields[3])?);
    if r > 255 || g > 255 || b > 255 {
        return None;
    }
    Some((r as u32) << 16 | (g as u32) << 8 | b as u32)
}
fn apply_sgr(p: &[u8], style: &mut Style) -> bool {
    let mut parameters: [&[u8]; 32] = [&[]; 32];
    let mut length = 0;
    for parameter in p.split(|b| *b == b';') {
        parameters[length] = parameter;
        length += 1;
    }
    let parts = &parameters[..length];
    let mut applied = false;
    let mut index = 0;
    while index < parts.len() {
        let mut subparameters: [&[u8]; 16] = [&[]; 16];
        let mut length = 0;
        for field in parts[index].split(|b| *b == b':') {
            subparameters[length] = field;
            length += 1;
        }
        let fields = &subparameters[..length];
        let command = number(fields[0]).unwrap_or(usize::MAX);
        if command == 38 || command == 48 {
            let selected = if fields.len() > 1 {
                color(&fields[1..], true)
            } else {
                let length = if index + 1 < parts.len() {
                    match number(parts[index + 1]) {
                        Some(2) => 4,
                        Some(5) => 2,
                        _ => 1,
                    }
                } else {
                    0
                };
                let available = length.min(parts.len() - index - 1);
                let selected = if available == length {
                    color(&parts[index + 1..index + 1 + available], false)
                } else {
                    None
                };
                index += available;
                selected
            };
            if let Some(value) = selected {
                if command == 38 {
                    style.fg = 0x01000000 | value;
                } else {
                    style.bg = 0x01000000 | value;
                }
                applied = true;
            }
        } else if fields.len() == 1 {
            match command {
                0 => {
                    *style = Style::default();
                    applied = true;
                }
                30..=37 | 90..=97 => {
                    style.fg = 0x01000000
                        | PALETTE[if command >= 90 {
                            command - 82
                        } else {
                            command - 30
                        }];
                    applied = true;
                }
                40..=47 | 100..=107 => {
                    style.bg = 0x01000000
                        | PALETTE[if command >= 100 {
                            command - 92
                        } else {
                            command - 40
                        }];
                    applied = true;
                }
                39 => {
                    style.fg = 0;
                    applied = true;
                }
                49 => {
                    style.bg = 0;
                    applied = true;
                }
                _ => {
                    let flag = match command {
                        1 => 1,
                        2 => 2,
                        3 | 23 => 4,
                        4 | 24 => 8,
                        7 | 27 => 16,
                        8 | 28 => 32,
                        9 | 29 => 64,
                        22 => 3,
                        _ => 0,
                    };
                    if flag != 0 {
                        if command < 20 {
                            style.flags |= flag;
                        } else {
                            style.flags &= !flag;
                        }
                        applied = true;
                    }
                }
            }
        }
        index += 1;
    }
    applied
}
