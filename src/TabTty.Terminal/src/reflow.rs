use super::*;

pub(super) struct Reflow {
    pub rows: VecDeque<Row>,
    pub cursor: usize,
    pub column: usize,
    pub cluster: Option<(i64, usize)>,
    pub position: RecordedPosition,
    pub(super) text: RecordingBuffer,
}

struct Builder<'a> {
    model: &'a Model,
    columns: usize,
    result: Reflow,
    placed: usize,
    current: Row,
    next: usize,
    reported: Vec<TabView>,
    unreported: bool,
}

impl<'a> Builder<'a> {
    fn new(model: &'a Model, columns: usize) -> Self {
        Self {
            model,
            columns,
            result: Reflow {
                rows: VecDeque::new(),
                cursor: 0,
                column: 0,
                cluster: None,
                position: model.position.clone(),
                text: RecordingBuffer::new(),
            },
            placed: 0,
            current: Row::new(0, 0),
            next: 0,
            reported: Vec::new(),
            unreported: false,
        }
    }

    fn start_line(&mut self, first: &Row, starts_retained_buffer: bool) {
        self.current = Row::new(first.id, self.columns);
        if starts_retained_buffer {
            self.current.logical_id = first.logical_id;
            self.current.logical_offset = first.logical_offset;
            self.current.logical_cluster_offset = first.logical_cluster_offset;
        }
        self.current.revision = first.revision + 1;
        self.current.excluded = first.excluded;
    }

    fn finish(&mut self, soft: bool, empty_boundary: bool) -> Result<(), i32> {
        self.current.soft = soft;
        self.current.refresh_plain();
        let next_offset = self.current.logical_offset + self.current.plain.len() as i64;
        let next_cluster_offset = self.current.logical_cluster_offset
            + self.current.span_count() as i64;
        if (!self.reported.is_empty() && !self.unreported)
            || (empty_boundary && self.current.plain.is_empty())
        {
            self.current.submitted = Some(self.current.serialize(true, None));
            self.current.submitted_revision = self.current.content_revision;
            self.current.submitted_tab_revision = self.current.tab_revision;
        } else {
            self.current.recorded_columns = std::mem::take(&mut self.reported);
        }
        let mut next = if soft {
            Row::new(next_row_id(), self.columns)
        } else {
            Row::new(0, 0)
        };
        next.logical_id = self.current.logical_id;
        next.logical_offset = next_offset;
        next.logical_cluster_offset = next_cluster_offset;
        next.excluded = self.current.excluded;
        self.result
            .rows
            .push_back(std::mem::replace(&mut self.current, next));
        self.placed += 1;
        self.next = 0;
        self.reported.clear();
        self.unreported = false;
        if self.result.rows.len() > self.model.retained {
            let mut expired = self.result.rows.pop_front().unwrap();
            if self
                .result
                .cluster
                .map_or(false, |(id, _)| id == expired.id)
            {
                self.result.cluster = None;
            }
            if self.model.logging.unwrap_or(0) & 2 != 0 {
                submit(
                    &mut expired,
                    false,
                    &mut self.result.text,
                    &mut self.result.position,
                    &self.model.unicode,
                    self.model.ambiguous,
                );
            }
        }
        Ok(())
    }

    fn record_span(&mut self, width: usize, already_reported: bool) {
        if !already_reported {
            self.unreported = true;
        } else if let Some(last) = self
            .reported
            .last_mut()
            .filter(|span| span.end == self.next as u32)
        {
            last.end += width as u32;
        } else {
            self.reported.push(TabView {
                start: self.next as u32,
                end: (self.next + width) as u32,
            });
        }
    }

    fn copy_row(&mut self, row: &Row, cursor: Option<usize>) -> Result<(), i32> {
        self.current.excluded |= row.excluded;
        let unchanged = row
            .submitted
            .as_ref()
            .map_or(false, |prior| prior == &row.serialize(true, None));
        let is_recorded = |column| {
            unchanged
                || row
                    .recorded_columns
                    .iter()
                    .any(|span| (span.start..span.end).contains(&(column as u32)))
        };
        let mut marks = row.formats.iter().peekable();
        let mut tabs = row.tabs.iter().peekable();
        let mut position = 0;
        let mut mapped = false;
        for column in 0..row.cells.len() {
            if column < position {
                continue;
            }
            let cell = row.cells[column];
            let width = cell.width.max(1) as usize;
            if self.next + width > self.columns {
                self.finish(true, false)?;
            }
            if let Some(old_column) = cursor.filter(|&old| old < column + width && !mapped) {
                self.result.cursor = self.placed;
                self.result.column = self.next + old_column.saturating_sub(column);
                mapped = true;
            }
            while let Some(mark) = marks.next_if(|mark| mark.column as usize <= column) {
                self.current.add_format(self.next, mark.revision)?;
            }
            if let Some(&(start, end)) = tabs.next_if(|tab| tab.0 <= column) {
                if start == column && self.next + end - start <= self.columns {
                    self.current.tabs.push((self.next, self.next + end - start));
                }
            }
            if row.id == self.model.cluster_row && column == self.model.cluster_column {
                self.result.cluster = Some((self.current.id, self.next));
            }
            if cell.width > 0 {
                self.current.write(
                    self.next,
                    row.text(cell),
                    width,
                    cell.style(),
                    cell.revision,
                )?;
            } else if cell.style() != Style::default() || cell.revision != 0 {
                if self.current.cells.len() <= self.next {
                    self.current
                        .cells
                        .resize(self.next + 1, CellView::blank(Style::default()));
                }
                self.current.cells[self.next] = CellView::blank(cell.style());
            }
            if let Some(target) = self.current.cells.get_mut(self.next) {
                target.revision = cell.revision;
            }
            self.record_span(width, is_recorded(column));
            self.next += width;
            position = column + width;
        }
        for mark in marks {
            let column =
                (self.next + (mark.column as usize).saturating_sub(position)).min(self.columns);
            self.current.add_format(column, mark.revision)?;
        }
        for &(start, end) in tabs {
            if start >= position && self.next + end - position <= self.columns {
                self.current
                    .tabs
                    .push((self.next + start - position, self.next + end - position));
            }
        }
        if let Some(old_column) = cursor.filter(|_| !mapped) {
            self.result.cursor = self.placed;
            self.result.column =
                (self.next + old_column.saturating_sub(position)).min(self.columns);
        }
        Ok(())
    }
}

pub(super) fn normal(
    model: &Model,
    screen: &VecDeque<Row>,
    row: usize,
    column: usize,
    wrap: bool,
    columns: usize,
) -> Result<Reflow, i32> {
    let mut build = Builder::new(model, columns);
    let active = model.history.len() + row;
    let mut first = true;
    let mut rows = model.history.iter().chain(screen).enumerate().peekable();
    while let Some((index, source)) = rows.next() {
        if first {
            build.start_line(source, index == 0);
        }
        let cursor = (index == active).then_some(column + wrap as usize);
        build.copy_row(source, cursor)?;
        first = !source.soft || rows.peek().is_none();
        if first {
            let empty_boundary = source.submitted.as_ref().map_or(false, Vec::is_empty);
            build.finish(false, empty_boundary)?;
        }
    }
    // Cursor operations can address a row that has yet to be materialized.
    if row >= screen.len() {
        build.result.cursor = build.placed.saturating_sub(1);
        build.result.column = column.min(columns - 1);
    }
    let retired = build.placed - build.result.rows.len();
    build.result.cursor = build.result.cursor.saturating_sub(retired);
    if build.result.cursor >= build.result.rows.len() {
        return Err(5);
    }
    Ok(build.result)
}
