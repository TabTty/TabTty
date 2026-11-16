use crate::unicode::Unicode;
use crate::{
    account, CellView, Effects, ExportRow, ExportState, RowView, SnapshotView, SourceView, Stats,
    TabView, LIVE_MODELS,
};
use std::collections::VecDeque;
use std::mem::size_of;
use std::ptr;
use std::sync::atomic::{AtomicI64, Ordering};
#[cfg(test)]
#[path = "admission_tests.rs"]
mod admission_tests;
// Native identities are process-unique and disjoint from C#'s positive row IDs.
static NEXT_ROW_ID: AtomicI64 = AtomicI64::new(-1);
fn next_row_id() -> i64 {
    NEXT_ROW_ID
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |id| id.checked_sub(1))
        .expect("Native terminal row identity space exhausted.")
}
const NULL: u16 = u16::MAX;
pub const UNSUPPORTED: i32 = 2;
pub const RESOURCE: i32 = 3;
#[derive(Clone, Copy, Default, PartialEq, Eq)]
struct Style {
    fg: u32,
    bg: u32,
    flags: u32,
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
        self.soft = false;
        self.dirty = true;
    }
    fn bytes(&self) -> usize {
        self.cells.capacity() * size_of::<CellView>()
            + self.arena.capacity() * 2
            + self.plain.capacity() * 2
            + self.submitted.as_ref().map_or(0, |s| s.capacity() * 2)
            + self.tabs.capacity() * size_of::<(usize, usize)>()
    }
    fn pending_units(&self) -> u64 {
        if self.submitted.is_some()
            && self.submitted_revision == self.content_revision
            && self.submitted_tab_revision == self.tab_revision
        {
            0
        } else {
            self.potential_units()
        }
    }
    fn potential_units(&self) -> u64 {
        (self.arena.len() + self.cells.len() + self.tabs.last().map_or(0, |t| t.1)) as u64
    }
    fn invalidate(&mut self) {
        self.revision += 1;
        self.content_revision += 1;
        self.dirty = true;
    }
    fn text(&self, c: CellView) -> &[u16] {
        if c.length == NULL {
            &[]
        } else {
            &self.arena[c.offset as usize..c.offset as usize + c.length as usize]
        }
    }
    fn clear_cells(&mut self, mut start: usize, mut end: usize) {
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
        for c in &mut self.cells[start..end] {
            *c = CellView::blank(Style::default());
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
        self.clear_cells(column, column + width);
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
    fn erase(&mut self, mut start: usize, mut end: usize, style: Style) {
        if start < self.cells.len() && self.cells[start].width == -1 {
            start -= 1;
        }
        if end < self.cells.len() && self.cells[end].width == -1 {
            end += 1;
        }
        self.clear_cells(start, end);
        if style != Style::default() {
            if self.cells.len() < end {
                self.cells.resize(end, CellView::blank(Style::default()));
            }
            for c in &mut self.cells[start..end] {
                *c = CellView::blank(style);
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
    ) {
        if insert {
            if self.cells.get(start).map_or(false, |cell| cell.width == -1) {
                self.clear_cells(start, start + 1);
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
            self.clear_cells(start, start + count);
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
        let mut end = self.cells.len();
        while end > 0 && self.cells[end - 1].width == 0 {
            end -= 1;
        }
        if preserve_tabs {
            end = end.max(self.tabs.last().map_or(0, |t| t.1));
        }
        let mut output = Vec::with_capacity(end);
        let mut column = 0;
        while column < end {
            if preserve_tabs {
                if let (Some(prefix), Some(&(_, to))) =
                    (prefix, self.tabs.iter().find(|t| t.0 == column))
                {
                    if to - column == 8 - (prefix + column) % 8 {
                        output.push(9);
                        column = to;
                        continue;
                    }
                }
            }
            if column >= self.cells.len() || self.cells[column].width == 0 {
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
}
#[derive(Default)]
struct Source {
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
}
#[derive(Default)]
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
    unicode: Unicode,
    columns: usize,
    screen_rows: usize,
    retained: usize,
    ambiguous: usize,
    history: VecDeque<Row>,
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
    cluster_row: i64,
    cluster_column: usize,
    cluster_width: usize,
    cluster_style: Style,
    sources: [Source; 3],
    logging: Option<u32>,
    finished: bool,
    failed: bool,
    auto_wrap: bool,
    line_feed_returns: bool,
    bracketed_paste: bool,
    cursor_visible: bool,
    cursor_blink: bool,
    cursor_shape: u32,
    bells: u32,
    terminal_log: Vec<u8>,
    text_log: Vec<u8>,
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
    prepared: PreparedOperation,
    export_rows: Vec<ExportRow>,
    export_tabs: Vec<TabView>,
    export_sources: [SourceView; 3],
}
impl Model {
    pub fn new(columns: usize, rows: usize, retained: usize, ambiguous: usize) -> Self {
        let mut screen = VecDeque::with_capacity(rows);
        screen.push_back(Row::new(next_row_id(), columns));
        let mut result = Self {
            unicode: Unicode::new(),
            columns,
            screen_rows: rows,
            retained,
            ambiguous,
            history: VecDeque::with_capacity(retained),
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
            finished: false,
            failed: false,
            auto_wrap: true,
            line_feed_returns: false,
            bracketed_paste: false,
            cursor_visible: true,
            cursor_blink: true,
            cursor_shape: 0,
            bells: 0,
            terminal_log: Vec::with_capacity(8192),
            text_log: Vec::with_capacity(8192),
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
            prepared: PreparedOperation::default(),
            export_rows: Vec::new(),
            export_tabs: Vec::new(),
            export_sources: [SourceView::empty(); 3],
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
            + self.cluster.capacity() * 4
            + self.position.cluster.capacity() * 4
            + self
                .sources
                .iter()
                .map(|s| s.parameters.capacity() + s.last_graphic.capacity() * 4)
                .sum::<usize>()
            + self.terminal_log.capacity()
            + self.text_log.capacity()
            + self.replies.capacity()
            + self.view_rows.capacity() * size_of::<RowView>()
            + self.export_rows.capacity() * size_of::<ExportRow>()
            + self.export_tabs.capacity() * size_of::<TabView>()) as u64
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
            text: self.text_log.as_ptr(),
            text_length: self.text_log.len() as u32,
            reserved0: ((self.bracketed_paste as u32) << 4) | (self.bells << 8),
            terminal: self.terminal_log.as_ptr(),
            terminal_length: self.terminal_log.len() as u32,
            reply_length: self.replies.len() as u32,
            replies: self.replies.as_ptr(),
        }
    }
    fn start_call(&mut self, flags: u32) -> Result<(), i32> {
        self.invalidate_prepared();
        self.text_log.clear();
        self.terminal_log.clear();
        self.replies.clear();
        self.bells = 0;
        if self.failed {
            return Err(UNSUPPORTED);
        }
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
            let mut source = std::mem::take(&mut self.sources[index]);
            result = self.decode(&mut source, &[], true);
            self.sources[index] = source;
            if result.is_err() {
                break;
            }
        }
        self.finished = true;
        self.finish_result(result)
    }
    pub fn flush(&mut self, flags: u32) -> Result<(), i32> {
        self.start_call(flags)?;
        if flags & 2 != 0 {
            for index in 0..self.history.len() {
                self.submit_history(index, false);
            }
            for index in 0..self.screen.len() {
                self.submit_screen(index, false);
            }
        }
        self.update_accounting();
        Ok(())
    }
    pub fn resize(&mut self, columns: usize, rows: usize) -> Result<(), i32> {
        self.invalidate_prepared();
        if columns == self.columns && rows == self.screen_rows {
            return Ok(());
        }
        if self.feed_bytes != 0 {
            self.unsupported += 1;
            return Err(UNSUPPORTED);
        }
        self.columns = columns;
        self.screen_rows = rows.min(self.retained);
        self.update_accounting();
        Ok(())
    }
    fn decode(&mut self, source: &mut Source, payload: &[u8], flush: bool) -> Result<(), i32> {
        for &byte in payload {
            for scalar in decode_utf8_byte(&mut source.pending, &mut source.pending_count, byte)? {
                self.emit(source, scalar)?;
            }
        }
        if let Some(scalar) = finish_utf8(flush, &mut source.pending_count) {
            self.emit(source, scalar)?;
        }
        Ok(())
    }
    fn emit(&mut self, source: &mut Source, c: u32) -> Result<(), i32> {
        if suppress_initial_bom(&mut source.started, c) {
            return Ok(());
        }
        self.apply(source, c)
    }
    fn operation_scalar(&mut self, c: u32) {
        if self.logging.unwrap_or(0) & 1 != 0 {
            append_utf8(&mut self.terminal_log, c);
        }
    }
    fn operation_bytes(&mut self, bytes: &[u8]) {
        if self.logging.unwrap_or(0) & 1 != 0 {
            self.terminal_log.extend_from_slice(bytes);
        }
    }
    fn apply(&mut self, source: &mut Source, c: u32) -> Result<(), i32> {
        if is_control(c) {
            source.last_was_graphic = false;
        }
        let crlf = source.previous_cr;
        source.previous_cr = c == 13;
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
            Unit::Index(left) => {
                self.operation_bytes(&[27, c as u8]);
                self.move_down(left);
                return Ok(());
            }
            Unit::Csi(Csi::Repeat(count)) => {
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
            Unit::Csi(command) => return self.csi(&source.parameters, c as u8, command),
            Unit::Control | Unit::Graphic => {}
        }
        match c {
            13 => {
                self.operation_scalar(c);
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
                self.cluster_row = 0;
                self.ensure_cursor();
                let next = ((self.column / 8 + 1) * 8).min(self.columns - 1);
                self.screen[self.cursor_row].add_tab(self.column, next);
                self.column = next;
                self.wrap_pending = false;
                return Ok(());
            }
            10 | 11 | 12 => {
                self.operation_scalar(c);
                if c == 10 && self.logging.unwrap_or(0) & 2 != 0 {
                    self.record_line_feed(crlf);
                }
                self.move_down(self.line_feed_returns);
                return Ok(());
            }
            _ => {}
        }
        self.print(source, c)
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
        if self.cluster_row != 0 && self.unicode.joins(&self.cluster, c) {
            if self.cluster.len() == 256 {
                return Err(RESOURCE);
            }
            self.cluster.push(c);
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
        let mut text = [0u16; 2];
        let value = char::from_u32(c).unwrap().encode_utf16(&mut text);
        self.screen[self.cursor_row].write(self.column, value, width, self.style)?;
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
        let prev_clusters = if soft {
            self.unicode
                .count_utf16(&self.screen[self.cursor_row].plain)
        } else {
            0
        };
        if self.cursor_row == self.screen_rows - 1 {
            self.scroll_region(0, self.screen_rows - 1, 1, false);
        } else if self.cursor_row < self.screen_rows - 1 {
            self.cursor_row += 1;
            self.ensure_cursor();
        }
        if soft {
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
            if !down && top == 0 && bottom == self.screen_rows - 1 {
                self.history_bytes += old.bytes();
                self.history_pending_units += old.pending_units();
                self.history_cell_capacity += old.cells.capacity() as u64;
                self.history.push_back(old);
            }
            let mut row = self.new_row();
            row.erase(0, self.columns, self.style.erase());
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
        while self.history.len() + self.screen.len() > self.retained {
            if self.history.is_empty() {
                break;
            }
            let mut old = self.history.pop_front().unwrap();
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
        let mut changed = false;
        if first == 0 {
            let mut start = self.history.len();
            while start > 0 && self.history[start - 1].soft {
                start -= 1;
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
                self.text_log.push(13);
                self.position.append(13, &self.unicode, self.ambiguous);
            }
            self.text_log.push(10);
            self.position.append(10, &self.unicode, self.ambiguous);
        }
    }
    fn csi(&mut self, p: &[u8], final_byte: u8, command: Csi) -> Result<(), i32> {
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
                        self.cursor_row + 1,
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
            if !apply_sgr(p, &mut self.style) {
                return Ok(());
            }
        } else if let Csi::Mode(mode, set) = command {
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
            }
        } else if let Csi::Edit(v) = command {
            let count = v[0].max(1);
            match final_byte {
                b'H' | b'f' => {
                    self.cursor_row = v[0].max(1).min(self.screen_rows) - 1;
                    self.column = v[1].max(1).min(self.columns) - 1;
                }
                b'A' => self.cursor_row = self.cursor_row.saturating_sub(count),
                b'B' => self.cursor_row = (self.cursor_row + count).min(self.screen_rows - 1),
                b'C' => self.column = (self.column + count).min(self.columns - 1),
                b'D' => self.column = self.column.saturating_sub(count),
                b'E' => {
                    self.cursor_row = (self.cursor_row + count).min(self.screen_rows - 1);
                    self.column = 0;
                }
                b'F' => {
                    self.cursor_row = self.cursor_row.saturating_sub(count);
                    self.column = 0;
                }
                b'G' | 96 => self.column = count.min(self.columns) - 1,
                b'd' => self.cursor_row = count.min(self.screen_rows) - 1,
                b'K' if v[0] <= 2 => {
                    self.cluster_row = 0;
                    self.ensure_cursor();
                    let (start, end) = match v[0] {
                        0 => (self.column, self.columns),
                        1 => (0, self.column + 1),
                        _ => (0, self.columns),
                    };
                    self.screen[self.cursor_row].erase(start, end, self.style.erase());
                    self.operation_csi(p, final_byte);
                    return Ok(());
                }
                b'X' => {
                    self.ensure_cursor();
                    self.screen[self.cursor_row].erase(
                        self.column,
                        self.column + count.min(self.columns - self.column),
                        self.style.erase(),
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
                    );
                }
                b'L' | b'M' => {
                    self.scroll_region(
                        self.cursor_row,
                        self.screen_rows - 1,
                        count,
                        final_byte == b'L',
                    );
                }
                b'S' | b'T' => {
                    self.scroll_region(0, self.screen_rows - 1, count, final_byte == b'T');
                }
                b'J' if v[0] == 2 => {
                    self.cluster_row = 0;
                    while self.screen.len() < self.screen_rows {
                        let row = self.new_row();
                        self.screen.push_back(row);
                    }
                    for row in &mut self.screen {
                        row.erase(0, self.columns, self.style.erase());
                    }
                    self.operation_csi(p, final_byte);
                    return Ok(());
                }
                b'J' if v[0] == 3 => {
                    self.history.clear();
                    self.history_bytes = 0;
                    self.history_pending_units = 0;
                    self.history_cell_capacity = 0;
                    self.operation_csi(p, final_byte);
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
                            );
                        } else if (v[0] == 0 && index > self.cursor_row)
                            || (v[0] == 1 && index < self.cursor_row)
                        {
                            row.erase(0, self.columns, self.style.erase());
                        }
                    }
                }
                _ => return Err(UNSUPPORTED),
            }
            self.cluster_row = 0;
            self.wrap_pending = false;
        }
        self.operation_csi(p, final_byte);
        Ok(())
    }
    fn operation_csi(&mut self, p: &[u8], f: u8) {
        self.operation_bytes(&[27, 91]);
        self.operation_bytes(p);
        self.operation_bytes(&[f]);
    }
    pub fn snapshot_copy_required(&self) -> u64 {
        // Per row: snapshot slot and string headers, compact and expanded cell
        // slots, and UTF-16 units in the arena, row text and expanded strings.
        256 + self
            .history
            .iter()
            .chain(self.screen.iter())
            .map(|row| 384 + row.cells.len() as u64 * 154 + row.arena.len() as u64 * 6)
            .sum::<u64>()
    }

    pub fn snapshot(&mut self) -> Result<SnapshotView, i32> {
        self.invalidate_prepared();
        self.snapshots += 1;
        self.view_rows.clear();
        let mut previous = None;
        self.history_bytes = 0;
        for row in &mut self.history {
            row.refresh_plain();
            normalize_row(row, &mut previous);
            self.view_rows.push(view(row));
            self.history_bytes += row.bytes();
        }
        for row in &mut self.screen {
            row.refresh_plain();
            normalize_row(row, &mut previous);
            self.view_rows.push(view(row));
        }
        let empty = self.history.is_empty()
            && self.screen.len() == 1
            && self.screen[0].cells.is_empty()
            && self.screen[0].tabs.is_empty();
        self.update_accounting();
        Ok(SnapshotView {
            rows: self.view_rows.as_ptr(),
            row_count: if empty {
                0
            } else {
                self.view_rows.len() as u32
            },
            cursor_row: (self.history.len() + self.cursor_row) as i32,
            cursor_column: self.column as i32,
            columns: self.columns as i32,
            screen_rows: self.screen_rows as i32,
            screen_start: self.history.len() as i32,
            flags: (if self.cursor_visible { 1 } else { 0 })
                | (if self.cursor_blink { 2 } else { 0 }),
            cursor_shape: self.cursor_shape,
        })
    }
    pub fn export(&mut self) -> Result<ExportState, i32> {
        let snapshot = self.snapshot()?;
        self.export_rows.clear();
        self.export_tabs.clear();
        let tab_count = self
            .history
            .iter()
            .chain(self.screen.iter())
            .map(|r| r.tabs.len())
            .sum::<usize>();
        self.export_tabs.reserve(tab_count);
        for row in self.history.iter().chain(self.screen.iter()) {
            self.export_tabs
                .extend(row.tabs.iter().map(|&(start, end)| TabView {
                    start: start as u32,
                    end: end as u32,
                }));
        }
        let mut tab_index = 0;
        let mut previous: Option<(i64, i64, usize, bool)> = None;
        for row in self.history.iter_mut().chain(self.screen.iter_mut()) {
            if let Some((_, offset, count, soft)) = previous {
                row.logical_cluster_offset = if soft { offset + count as i64 } else { 0 };
            }
            let count = self.unicode.count_utf16(&row.plain);
            previous = Some((row.logical_id, row.logical_cluster_offset, count, row.soft));
            self.export_rows.push(ExportRow {
                view: view(row),
                tabs: unsafe { self.export_tabs.as_ptr().add(tab_index) },
                submitted: row.submitted.as_ref().map_or(ptr::null(), |s| s.as_ptr()),
                content_revision: row.content_revision,
                logical_cluster_offset: row.logical_cluster_offset,
                submitted_revision: row.submitted_revision,
                tab_count: row.tabs.len() as u32,
                submitted_length: row.submitted.as_ref().map_or(u32::MAX, |s| s.len() as u32),
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
                    | ((source.osc as u32) << 3),
                escape: source.escape as u32,
                reserved: source.string_bytes as u32,
            };
        }
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
                | ((self.bracketed_paste as u32) << 4),
            recorded_column: self.position.column as u32,
            recorded_known: (!self.position.initialized || self.position.known) as u32,
            recorded_count: self.position.cluster.len() as u32,
            row_count: self.export_rows.len() as u32,
            source_count: 3,
            reserved0: 0,
            reserved1: 0,
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
        if operation > 4 || source > 2 || flags & 0xfc != 0 || pending_replies > MAX_REPLIES {
            return Err(1);
        }
        let flags = flags & 3;
        if operation <= 2 {
            if self.logging.map_or(false, |prior| prior != flags) {
                return Err(UNSUPPORTED);
            }
        }
        let mut revisits_rows = false;
        let mut row_moves = 0;
        let mut repeated_bytes = 0;
        if operation == 0 {
            if self.finished || payload.len() > 1024 * 1024 {
                return Err(UNSUPPORTED);
            }
            let mut probe = Probe::from_source(&self.sources[source], self);
            probe.decode(payload, false)?;
            if probe.reply_count + pending_replies > MAX_REPLIES {
                return Err(4);
            }
            revisits_rows = probe.revisits_rows;
            row_moves = probe.row_moves.min(self.retained) as u64;
            repeated_bytes = probe.repeated_bytes as u64;
        } else if operation == 1 {
            let mut probe = Probe::from_source(&self.sources[0], self);
            probe.decode(&[], true)?;
            probe.replace_source(&self.sources[1]);
            probe.decode(&[], true)?;
        }
        let owned = self.quota;
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
                (pending + bytes) * (line_feeds + 1) * 3
            } else {
                0
            };
            let terminal_effect = if flags & 1 != 0 { bytes * 3 + 8192 } else { 0 };
            // Use the managed engine's incremental log writer for large batches.
            // No native mutation has occurred when this handoff is requested.
            if text_effect + terminal_effect > 256 * 1024 {
                return Err(UNSUPPORTED);
            }
            let row_growth = if operation <= 1 {
                (bytes + self.screen_rows as u64 + row_moves + 1)
                    * (self.columns as u64 * 80 + size_of::<Row>() as u64 + 128)
                    * 2
            } else {
                0
            };
            owned * 5 + row_growth + bytes * 8192 + (text_effect + terminal_effect) * 4 + 128 * 1024
        } else {
            let rows = (self.history.len() + self.screen.len()) as u64;
            let text = self
                .history
                .iter()
                .chain(self.screen.iter())
                .filter(|r| r.dirty)
                .map(|r| (r.arena.len() + r.cells.len()) as u64)
                .sum::<u64>();
            let snapshot = owned + text * 4 + rows * size_of::<RowView>() as u64 * 2 + 128 * 1024;
            if operation == 4 {
                let tabs = self
                    .history
                    .iter()
                    .chain(self.screen.iter())
                    .map(|r| r.tabs.len() as u64)
                    .sum::<u64>();
                snapshot
                    + rows * size_of::<ExportRow>() as u64 * 2
                    + tabs * size_of::<TabView>() as u64 * 2
            } else {
                snapshot
            }
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
    output: &mut Vec<u8>,
    pos: &mut RecordedPosition,
    u: &Unicode,
    ambiguous: usize,
) -> bool {
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
    let serialized = if row.tabs.is_empty() {
        None
    } else {
        Some(row.serialize(true, pos.column()))
    };
    for c in char::decode_utf16(serialized.as_ref().unwrap_or(&revision).iter().copied()) {
        let scalar = c.unwrap_or(char::REPLACEMENT_CHARACTER) as u32;
        append_utf8(output, scalar);
        pos.append(scalar, u, ambiguous);
    }
    if !revision.is_empty() || boundary {
        row.submitted = Some(revision);
        row.submitted_revision = row.content_revision;
        row.submitted_tab_revision = row.tab_revision;
    }
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
    Index(bool),
    Csi(Csi),
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
        14 | 15 => return Err(UNSUPPORTED),
        8..=13 => return Ok(Unit::Control),
        _ if is_control(scalar) => return Ok(Unit::Ignored),
        _ => {}
    }
    match *escape {
        0 if scalar == 0xfeff => return Err(UNSUPPORTED),
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
                68 | 69 => return Ok(Unit::Index(scalar == 69)),
                _ => return Err(UNSUPPORTED),
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
    source_graphic: [u32; 256],
    source_count: usize,
    source_was_graphic: bool,
    cluster: [u32; 256],
    cluster_count: usize,
    cluster_active: bool,
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
            source_graphic: [0; 256],
            source_count: source.last_graphic.len(),
            source_was_graphic: source.last_was_graphic,
            cluster: [0; 256],
            cluster_count: model.cluster.len(),
            cluster_active: model.cluster_row != 0,
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
    fn decode(&mut self, payload: &[u8], flush: bool) -> Result<(), i32> {
        let mut offset = 0;
        while offset < payload.len() {
            if self.pending_count == 0
                && self.escape == 0
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
            Unit::Ignored
            | Unit::Bell
            | Unit::Csi(Csi::Rendition | Csi::Mode(..) | Csi::CursorStyle(_)) => {}
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
            Unit::Graphic => return self.graphic(c),
            Unit::Control | Unit::Index(_) => self.cluster_active = false,
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

enum Mode {
    Wrap,
    Cursor,
    Paste,
    Newline,
}

enum Csi {
    Rendition,
    Mode(Mode, bool),
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
        let mode = match parameters {
            b"?7" => Mode::Wrap,
            b"?25" => Mode::Cursor,
            b"?2004" => Mode::Paste,
            b"20" => Mode::Newline,
            _ => return Err(UNSUPPORTED),
        };
        return Ok(Unit::Csi(Csi::Mode(mode, final_byte == b'h')));
    }
    let mut values = [0usize; 2];
    let mut index = 0;
    for &byte in parameters {
        if byte.is_ascii_digit() {
            values[index] = (values[index] * 10 + (byte - b'0') as usize).min(1024);
        } else if byte == b';' && index == 0 && matches!(final_byte, b'H' | b'f') {
            index = 1;
        } else {
            return Err(UNSUPPORTED);
        }
    }
    match final_byte {
        b'H' | b'f' | b'A' | b'B' | b'C' | b'D' | b'E' | b'F' | b'G' | 96 | b'd' | b'X' | b'@'
        | b'P' | b'L' | b'M' | b'S' | b'T' => {}
        b'K' if values[0] <= 2 => {}
        b'J' if values[0] <= 3 => {}
        b'b' => return Ok(Unit::Csi(Csi::Repeat(values[0].max(1)))),
        _ => return Err(UNSUPPORTED),
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

fn append_utf8(output: &mut Vec<u8>, scalar: u32) {
    let mut bytes = [0u8; 4];
    output.extend_from_slice(
        char::from_u32(scalar)
            .unwrap_or(char::REPLACEMENT_CHARACTER)
            .encode_utf8(&mut bytes)
            .as_bytes(),
    );
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
