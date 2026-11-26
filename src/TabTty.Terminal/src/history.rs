use super::{Model, Row, RowContinuation};
use std::collections::HashMap;
use std::mem::size_of;
use std::time::{Duration, Instant};

const BUILD_ROWS: usize = 2048;
pub(super) const BUSY: i32 = 6;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct HistoryProof {
    pub first: u64,
    pub second: u64,
    pub first_power: u64,
    pub second_power: u64,
    pub length: i64,
    pub revision: i64,
}

const RANGE_EVICTED: i32 = 7;
const RANGE_CHANGED: i32 = 8;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct HistoryPoint {
    pub line: i64,
    pub offset: i64,
}

impl HistoryPoint {
    fn at(row: &Row, offset: usize) -> Self {
        Self { line: row.logical.id, offset: row.logical.utf16 + offset as i64 }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HistoryRange {
    pub start: HistoryPoint,
    pub end: HistoryPoint,
    pub proof: HistoryProof,
    pub end_boundary: u32,
}

#[repr(C)]
pub struct HistoryFragmentView {
    pub position: HistoryPoint,
    pub cluster_offset: i64,
    pub text: *const u16,
    pub length: u32,
    pub flags: u32,
}

#[repr(C)]
#[derive(Default)]
pub struct HistoryPageView {
    pub range: HistoryRange,
    pub earlier: HistoryRange,
    pub next: HistoryPoint,
    pub count: u32,
    pub flags: u32,
}

// Keep these bit assignments in sync with NativeTerminalCore.History.cs.
const FRAGMENT_LINE_END: u32 = 1 << 0;
const FRAGMENT_CONTINUES_BEFORE: u32 = 1 << 1;
const FRAGMENT_CONTINUES_AFTER: u32 = 1 << 2;
const FRAGMENT_PREFIX_MISSING: u32 = 1 << 3;
const FRAGMENT_SUFFIX_MISSING: u32 = 1 << 4;
const PAGE_HAS_RANGE: u32 = 1 << 0;
const PAGE_HAS_EARLIER: u32 = 1 << 1;
const PAGE_HAS_NEXT: u32 = 1 << 2;
const SEARCH_HAS_COVERAGE: u32 = 1 << 0;
const SEARCH_HAS_SCANNED: u32 = 1 << 1;
const SEARCH_HAS_NEXT: u32 = 1 << 2;

const PREFIX: u32 = 0;
const SUFFIX: u32 = 1;
const OVERLAP: u32 = 2;
const FRAGMENT_COST: u32 = 640;
const RESOURCE: i32 = 5;
const SEARCH_BYTES: u32 = 256 * 1024;
const SEARCH_ROWS: usize = 1024;
const MATCH_COST: u32 = 512;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct HistorySearchCheckpoint {
    pub position: HistoryPoint,
    pub end: HistoryPoint,
    pub remaining: HistoryProof,
}

#[repr(C)]
pub struct HistoryMatchView {
    pub position: HistoryPoint,
    pub context: HistoryRange,
    pub text: *const u16,
    pub length: u32,
}

#[repr(C)]
pub struct HistorySearchPageView {
    pub start: HistorySearchCheckpoint,
    pub next: HistorySearchCheckpoint,
    pub covered: HistoryRange,
    pub scanned: HistoryRange,
    pub count: u32,
    pub flags: u32,
}

#[derive(Default)]
struct SearchPart {
    text: Vec<u16>,
    boundaries: Vec<usize>,
    start: HistoryPoint,
    next: HistoryPoint,
    owned: usize,
}

impl SearchPart {
    fn push(&mut self, row: &Row, from: usize, to: usize) {
        let offset = self.text.len();
        self.text.extend_from_slice(&row.plain[from..to]);
        if self.boundaries.is_empty() { self.boundaries.push(0); }
        row.spans(|_, end| {
            if end <= from { return true; }
            if end > to { return false; }
            self.boundaries.push(offset + end - from);
            true
        });
    }

    fn position(&self, offset: usize) -> HistoryPoint {
        HistoryPoint { offset: self.start.offset + offset as i64, ..self.start }
    }
}

struct LocatedMatch { part: usize, offset: usize }

pub struct HistorySearchWindow {
    start: HistorySearchCheckpoint,
    covered: Option<HistoryRange>,
    context_prefix: Option<HistoryRange>,
    parts: Vec<SearchPart>,
    progress: HistoryPoint,
    matches: Vec<LocatedMatch>,
    query_length: usize,
}

pub type OrdinalEquals = extern "C" fn(*const u16, *const u16, u32) -> i32;

impl HistorySearchWindow {
    pub fn scan(&mut self, query: &[u16], case_sensitive: bool, limit: usize, output_budget: u32,
        milliseconds: u32, unicode_equals: OrdinalEquals) -> Result<(), i32>
    {
        if milliseconds == 0 { return Err(BUSY); }
        if query.len() != self.query_length { return Err(1); }
        let slice = Instant::now() + Duration::from_millis(u64::from(milliseconds.min(20)));
        self.matches.clear();
        self.progress = self.start.position;
        let mut result_bytes = 0;
        for (part_index, part) in self.parts.iter().enumerate() {
            for (index, &offset) in part.boundaries.iter().enumerate().take_while(|(_, at)| **at < part.owned) {
                if index % 64 == 0 && Instant::now() >= slice {
                    self.progress = part.position(offset);
                    return Ok(());
                }
                let end = offset + query.len();
                if end > part.text.len() { break; }
                if part.boundaries.binary_search(&end).is_err() { continue; }
                let candidate = &part.text[offset..end];
                let same = if case_sensitive { candidate == query }
                    else { ordinal_case_equal(candidate, query, unicode_equals) };
                if !same { continue; }
                let size = json_cost(candidate) + MATCH_COST;
                if self.matches.len() == limit || result_bytes + size > output_budget {
                    self.progress = part.position(offset);
                    return Ok(());
                }
                self.matches.push(LocatedMatch { part: part_index, offset });
                result_bytes += size;
            }
            self.progress = part.next;
            if Instant::now() >= slice { break; }
        }
        Ok(())
    }
}

fn ordinal_case_equal(candidate: &[u16], query: &[u16], unicode_equals: OrdinalEquals) -> bool {
    for (&a, &b) in candidate.iter().zip(query) {
        if a == b { continue; }
        if a > 127 || b > 127 {
            // The Windows application supplies the runtime's ordinal equality primitive.
            // Search order, boundaries and checkpoints remain with the terminal model.
            return unicode_equals(candidate.as_ptr(), query.as_ptr(), query.len() as u32) != 0;
        }
        if !(a as u8).eq_ignore_ascii_case(&(b as u8)) { return false; }
    }
    true
}

impl HistoryProof {
    const EMPTY: Self = Self {
        first: 0,
        second: 0,
        first_power: 1,
        second_power: 1,
        length: 0,
        revision: 0,
    };

    pub(super) fn append(self, suffix: Self) -> Self {
        Self {
            first: self
                .first
                .wrapping_mul(suffix.first_power)
                .wrapping_add(suffix.first),
            second: self
                .second
                .wrapping_mul(suffix.second_power)
                .wrapping_add(suffix.second),
            first_power: self.first_power.wrapping_mul(suffix.first_power),
            second_power: self.second_power.wrapping_mul(suffix.second_power),
            length: self.length + suffix.length,
            revision: self.revision.max(suffix.revision),
        }
    }

    fn text(bases: [u64; 2], text: &[u16], revision: i64) -> Self {
        let mut proof = Self::EMPTY;
        for &unit in text {
            let value = u64::from(unit) + 1;
            proof.first = proof.first.wrapping_mul(bases[0]).wrapping_add(value);
            proof.second = proof.second.wrapping_mul(bases[1]).wrapping_add(value);
            proof.first_power = proof.first_power.wrapping_mul(bases[0]);
            proof.second_power = proof.second_power.wrapping_mul(bases[1]);
        }
        proof.length = text.len() as i64;
        proof.revision = revision;
        proof
    }
}

#[repr(C)]
#[cfg(test)]
pub struct HistoryRowView {
    pub logical_id: i64,
    pub offset: i64,
    pub cluster_offset: i64,
    pub text: *const u16,
    pub length: i32,
    pub soft_wrap: u32,
}

#[repr(C)]
#[derive(Default)]
pub struct HistorySlice {
    pub start: u32,
    pub end: u32,
    pub bytes: u32,
    pub json: u32,
    pub clusters: u32,
}

// Visit the part of each stored unit within the requested UTF-16 range.
fn clusters(row: &Row, from: usize, to: usize, mut visit: impl FnMut(usize, usize, u32) -> bool) {
    row.spans(|start, end| {
        if start >= to { return false; }
        if end <= from { return true; }
        let start = start.max(from);
        let end = end.min(to);
        let bytes = char::decode_utf16(row.plain[start..end].iter().copied())
            .map(|c| c.unwrap_or(char::REPLACEMENT_CHARACTER).len_utf8() as u32)
            .sum();
        visit(start - from, end - from, bytes)
    });
}

fn json_cost(text: &[u16]) -> u32 {
    text.iter()
        .map(|&unit| match unit {
            92 => 6,
            34 => 12,
            32..=126 if !matches!(unit, 60 | 62 | 38 | 39 | 43 | 96) => 2,
            _ => 16,
        })
        .sum()
}

pub(super) struct Archive {
    capacity: usize,
    tree: Vec<HistoryProof>,
    lines: HashMap<i64, (u64, u64)>,
    head: u64,
    tail: u64,
    page: HashMap<i64, (usize, usize)>,
    bases: [u64; 2],
}

impl Archive {
    pub(super) fn required(limit: usize) -> u64 {
        let tree = 2 * limit.next_power_of_two() * size_of::<HistoryProof>();
        // Both maps reserve their maximum entry counts on creation.
        (tree + limit * 96 + 512 * 96) as u64
    }

    fn new(limit: usize, bases: [u64; 2]) -> Self {
        let capacity = limit.next_power_of_two();
        Self {
            capacity,
            tree: vec![HistoryProof::EMPTY; capacity * 2],
            lines: HashMap::with_capacity(limit),
            head: 0,
            tail: 0,
            page: HashMap::with_capacity(512),
            bases,
        }
    }

    fn count(&self) -> usize {
        (self.tail - self.head) as usize
    }

    fn append(&mut self, line: i64, proof: HistoryProof) {
        self.lines
            .entry(line)
            .and_modify(|bounds| bounds.1 = self.tail)
            .or_insert((self.tail, self.tail));
        let mut leaf = self.capacity + self.slot(self.tail);
        self.tree[leaf] = proof;
        self.tail += 1;
        while leaf > 1 {
            leaf /= 2;
            self.tree[leaf] = self.tree[leaf * 2].append(self.tree[leaf * 2 + 1]);
        }
    }

    pub(super) fn remove_front(&mut self, line: i64) {
        if self.head == self.tail {
            return;
        }
        self.head += 1;
        let bounds = self
            .lines
            .get_mut(&line)
            .expect("indexed history line is present");
        if bounds.1 < self.head {
            self.lines.remove(&line);
        } else {
            bounds.0 = self.head;
        }
    }

    fn slot(&self, index: u64) -> usize {
        index as usize & (self.capacity - 1)
    }

    fn archived_proof(&self, start: usize, end: usize) -> HistoryProof {
        let first = self.slot(self.head + start as u64);
        let count = end - start;
        let contiguous = count.min(self.capacity - first);
        let proof = self.query(first, first + contiguous);
        proof.append(self.query(0, count - contiguous))
    }

    fn query(&self, mut start: usize, mut end: usize) -> HistoryProof {
        let mut left = HistoryProof::EMPTY;
        let mut right = HistoryProof::EMPTY;
        start += self.capacity;
        end += self.capacity;
        while start < end {
            if start & 1 != 0 {
                left = left.append(self.tree[start]);
                start += 1;
            }
            if end & 1 != 0 {
                end -= 1;
                right = self.tree[end].append(right);
            }
            start /= 2;
            end /= 2;
        }
        left.append(right)
    }
}

type Prior = RowContinuation;

fn normalize(row: &mut Row, prior: Option<Prior>) -> Prior {
    if let Some(prior) = prior { row.start_after(prior); }
    row.refresh_plain();
    after(row)
}

fn after(row: &Row) -> Prior {
    row.continuation()
}

fn whole(row: &Row, bases: [u64; 2]) -> HistoryProof {
    let proof = fragment(row, bases, 0, row.plain.len());
    if row.soft {
        proof
    } else {
        proof.append(HistoryProof::text(bases, &[10], 0))
    }
}

fn fragment(row: &Row, bases: [u64; 2], from: usize, to: usize) -> HistoryProof {
    let mut revision = 0;
    let mut offset = 0;
    if from < to {
        let mut marks = row.formats.iter().peekable();
        let end = row.text_end();
        for column in 0..=end {
            while let Some(mark) = marks.next_if(|mark| mark.column as usize == column) {
                if offset >= from && offset < to {
                    revision = revision.max(mark.revision);
                }
                offset += 1;
            }
            if offset >= to || column == end {
                break;
            }
            let cell = row.cells.get(column);
            let len = match cell {
                Some(cell) if cell.width < 0 => 0,
                Some(cell) if cell.width > 0 => cell.length as usize,
                _ => 1,
            };
            if len != 0 && offset + len > from {
                revision = revision.max(cell.map_or(0, |cell| cell.revision));
            }
            offset += len;
        }
    }
    HistoryProof::text(bases, &row.plain[from..to], revision)
}

impl Model {
    pub fn history_slice(
        &self,
        index: usize,
        from: usize,
        to: usize,
        kind: u32,
        bytes: u32,
        json: u32,
    ) -> Result<HistorySlice, i32> {
        let row = self.history_row(index)?;
        let text = row.plain.get(from..to).ok_or(1)?;
        let mut result = HistorySlice {
            start: from as u32,
            end: from as u32,
            ..HistorySlice::default()
        };
        match kind {
            PREFIX => {
                clusters(row, from, to, |start, end, size| {
                    let serialized = if json == u32::MAX {
                        0
                    } else {
                        json_cost(&text[start..end])
                    };
                    if size > bytes - result.bytes || serialized > json - result.json {
                        return false;
                    }
                    result.end = (from + end) as u32;
                    result.bytes += size;
                    result.json += serialized;
                    result.clusters += 1;
                    true
                });
            }
            SUFFIX => {
                let total = char::decode_utf16(text.iter().copied())
                    .map(|character| {
                        character.unwrap_or(char::REPLACEMENT_CHARACTER).len_utf8() as u32
                    })
                    .sum::<u32>();
                result.bytes = total;
                result.end = to as u32;
                if total > bytes {
                    clusters(row, from, to, |_, end, size| {
                        result.bytes -= size;
                        result.start = (from + end) as u32;
                        result.bytes > bytes
                    });
                }
            }
            OVERLAP => {
                clusters(row, from, to, |start, end, _| {
                    if start >= bytes as usize {
                        return false;
                    }
                    result.end = (from + end) as u32;
                    true
                });
            }
            _ => return Err(1),
        }
        Ok(result)
    }

    pub fn history_required(&self) -> u64 {
        let index = if self.history_index.is_none() {
            Archive::required(self.retained)
        } else {
            0
        };
        let indexed = self.history_index.as_ref().map_or(0, Archive::count);
        let text = self
            .history
            .iter()
            .skip(indexed)
            .take(BUILD_ROWS)
            .chain(self.normal_rows())
            .filter(|row| row.dirty)
            .map(|row| row.potential_units() * 20 + 64)
            .sum::<u64>();
        self.quota + index + text
    }

    pub fn prepare_history(&mut self, bases: [u64; 2], allow_build: bool) -> Result<usize, i32> {
        let indexed = self.history_index.as_ref().map_or(0, Archive::count);
        if !allow_build && self.history.len().saturating_sub(indexed) > 512 {
            return Err(BUSY);
        }
        if self.history_required() > self.limit {
            return Err(5);
        }
        let archive = self
            .history_index
            .get_or_insert_with(|| Archive::new(self.retained, bases));
        if archive.bases != bases {
            return Err(1);
        }
        self.index_archived_rows(BUILD_ROWS);
        let archive = self.history_index.as_mut().unwrap();
        if archive.count() < self.history.len() {
            self.update_accounting();
            return Err(BUSY);
        }
        let mut prior = self.history.back().map(|row| after(row));
        archive.page.clear();
        let page = self
            .parked
            .as_mut()
            .map_or(&mut self.screen, |parked| &mut parked.screen);
        for (index, row) in page.iter_mut().enumerate() {
            prior = Some(normalize(row, prior));
            archive
                .page
                .entry(row.logical.id)
                .and_modify(|bounds| bounds.1 = index)
                .or_insert((index, index));
        }
        let count = self.history.len() + page.len();
        self.update_accounting();
        Ok(count)
    }

    pub(super) fn index_archived_rows(&mut self, maximum: usize) {
        let archive = match &mut self.history_index {
            Some(archive) => archive,
            None => return,
        };
        let indexed = archive.count();
        if indexed == self.history.len() {
            return;
        }
        let mut prior = indexed
            .checked_sub(1)
            .map(|i| after(&self.history[i]));
        for row in self.history.iter_mut().skip(indexed).take(maximum) {
            let before = row.bytes();
            prior = Some(normalize(row, prior));
            self.history_bytes = self.history_bytes - before + row.bytes();
            archive.append(row.logical.id, whole(row, archive.bases));
        }
    }

    fn normal_rows(&self) -> std::collections::vec_deque::Iter<'_, Row> {
        self.parked
            .as_ref()
            .map_or(&self.screen, |parked| &parked.screen)
            .iter()
    }

    fn history_row(&self, index: usize) -> Result<&Row, i32> {
        if index < self.history.len() {
            self.history.get(index)
        } else {
            self.parked
                .as_ref()
                .map_or(&self.screen, |parked| &parked.screen)
                .get(index - self.history.len())
        }
        .ok_or(1)
    }

    fn history_len(&self) -> usize { self.history.len() + self.normal_rows().len() }

    fn locate(&self, point: HistoryPoint, end: bool) -> Result<(usize, usize), i32> {
        let (mut first, mut last) = self.history_line(point.line)?.ok_or(RANGE_EVICTED)?;
        if point.offset < self.history_row(first)?.logical.utf16 { return Err(RANGE_EVICTED); }
        while first < last {
            let middle = first + (last - first) / 2;
            let row = self.history_row(middle)?;
            let stop = row.logical.utf16 + row.plain.len() as i64;
            if stop < point.offset || (!end && stop == point.offset) { first = middle + 1; }
            else { last = middle; }
        }
        let row = self.history_row(first)?;
        let offset = point.offset.checked_sub(row.logical.utf16).ok_or(RANGE_CHANGED)?;
        if !(0..=row.plain.len() as i64).contains(&offset) { return Err(RANGE_CHANGED); }
        Ok((first, offset as usize))
    }

    fn point_at(&self, row: usize, offset: usize) -> Result<HistoryPoint, i32> {
        Ok(HistoryPoint::at(self.history_row(row)?, offset))
    }

    fn hard_end(&self, row: usize, offset: usize) -> Result<bool, i32> {
        let entry = self.history_row(row)?;
        Ok(row + 1 < self.history_len() && !entry.soft && offset == entry.plain.len())
    }

    fn range_proof(&self, start: HistoryPoint, end: HistoryPoint, hard_end: bool) -> Result<HistoryProof, i32> {
        let (first, from) = self.locate(start, false)?;
        let (last, to) = self.locate(end, true)?;
        if hard_end && !self.hard_end(last, to)? { return Err(RANGE_CHANGED); }
        let bases = self.history_index.as_ref().ok_or(BUSY)?.bases;
        let newline = || HistoryProof::text(bases, &[10], 0);
        let mut proof = if first > last && start == end {
            HistoryProof::EMPTY
        } else if first > last || first == last && from > to {
            return Err(RANGE_CHANGED);
        } else if first == last {
            self.history_fragment(first, from, to)?
        } else {
            let row = self.history_row(first)?;
            let mut leading = self.history_fragment(first, from, row.plain.len())?;
            if !row.soft { leading = leading.append(newline()); }
            leading.append(self.history_aggregate(first + 1, last)?)
                .append(self.history_fragment(last, 0, to)?)
        };
        if hard_end { proof = proof.append(newline()); }
        Ok(proof)
    }

    fn history_range(&self, start: HistoryPoint, end: HistoryPoint) -> Result<HistoryRange, i32> {
        let (last, to) = self.locate(end, true)?;
        let hard_end = self.hard_end(last, to)?;
        Ok(HistoryRange { start, end, proof: self.range_proof(start, end, hard_end)?, end_boundary: hard_end as u32 })
    }

    fn search_range(&self, start: HistoryPoint, end: HistoryPoint, fixed_end: HistoryPoint) -> Result<HistoryRange, i32> {
        if end != fixed_end { return self.history_range(start, end); }
        Ok(HistoryRange { start, end, proof: self.range_proof(start, end, false)?, end_boundary: 0 })
    }

    pub fn verify_history_range(&self, range: &HistoryRange) -> Result<(), i32> {
        let actual = self.range_proof(range.start, range.end, range.end_boundary != 0)?;
        if actual != range.proof { return Err(RANGE_CHANGED); }
        Ok(())
    }

    fn history_tail(&self, last: usize, end: usize, limit: usize, byte_limit: u32) -> Result<HistoryRange, i32> {
        let (mut first, mut offset, mut bytes) = (last, end, 0);
        for index in (last.saturating_add(1).saturating_sub(limit)..=last).rev() {
            let to = if index == last { end } else { self.history_row(index)?.plain.len() };
            let suffix = self.history_slice(index, 0, to, SUFFIX, byte_limit - bytes, u32::MAX)?;
            if suffix.start == 0 {
                (first, offset, bytes) = (index, 0, bytes + suffix.bytes);
            } else {
                if (suffix.start as usize) < to { (first, offset) = (index, suffix.start as usize); }
                break;
            }
        }
        self.history_range(self.point_at(first, offset)?, self.point_at(last, end)?)
    }

    pub fn read_history_page(&self, requested: Option<&HistoryRange>, position: Option<HistoryPoint>, byte_limit: u32,
        output_budget: u32, fragments: &mut [std::mem::MaybeUninit<HistoryFragmentView>]) -> Result<HistoryPageView, i32>
    {
        let mut page = HistoryPageView::default();
        let count = self.history_len();
        if count == 0 {
            if requested.is_some() { return Err(RANGE_EVICTED); }
            return Ok(page);
        }
        let range = if let Some(range) = requested {
            self.verify_history_range(range)?;
            *range
        } else {
            let end = self.history_row(count - 1)?.plain.len();
            let tail = self.history_tail(count - 1, end, fragments.len(), byte_limit)?;
            if tail.start == tail.end && end != 0 { return Err(RESOURCE); }
            tail
        };
        let start = position.unwrap_or(range.start);
        let (first, offset) = self.locate(start, false)?;
        let (last, end) = self.locate(range.end, true)?;
        let mut bytes = 0;
        let mut serialized = 0;
        page.range = range;
        page.flags = PAGE_HAS_RANGE;
        let first_retained = self.history_row(0)?.logical;
        for index in first..=last {
            let row = self.history_row(index)?;
            let from = if index == first { offset } else { 0 };
            let to = if index == last { end } else { row.plain.len() };
            if page.count as usize == fragments.len() || serialized + FRAGMENT_COST > output_budget {
                if page.count == 0 { return Err(RESOURCE); }
                page.next = HistoryPoint::at(row, from);
                page.flags |= PAGE_HAS_NEXT;
                break;
            }
            let slice = self.history_slice(index, from, to, PREFIX, byte_limit - bytes, output_budget - serialized - FRAGMENT_COST)?;
            let cut = slice.end as usize;
            if cut == from && to != from && page.count == 0 { return Err(RESOURCE); }
            bytes += slice.bytes;
            serialized += slice.json + FRAGMENT_COST;
            if cut != from || to == from {
                let line_end = cut == row.plain.len() && (index < last && !row.soft || index == last && range.end_boundary != 0);
                let position = HistoryPoint::at(row, from);
                let before = self.history_slice(index, 0, from, PREFIX, u32::MAX, u32::MAX)?.clusters;
                let flags = u32::from(line_end) * FRAGMENT_LINE_END
                    | u32::from(position.offset > 0) * FRAGMENT_CONTINUES_BEFORE
                    | u32::from(cut < row.plain.len() || row.soft) * FRAGMENT_CONTINUES_AFTER
                    | u32::from(first_retained.id == row.logical.id && first_retained.utf16 > 0) * FRAGMENT_PREFIX_MISSING
                    | u32::from(index + 1 == count && row.soft) * FRAGMENT_SUFFIX_MISSING;
                fragments[page.count as usize].write(HistoryFragmentView { position, cluster_offset: row.logical.stored + i64::from(before),
                    text: row.plain[from..cut].as_ptr(), length: (cut - from) as u32, flags });
                page.count += 1;
            }
            if cut != to {
                page.next = HistoryPoint::at(row, cut);
                page.flags |= PAGE_HAS_NEXT;
                break;
            }
        }
        let (row, start_offset) = self.locate(range.start, false)?;
        if start_offset > 0 || row > 0 {
            let (earlier, end) = if start_offset == 0 { (row - 1, self.history_row(row - 1)?.plain.len()) }
                else { (row, start_offset) };
            let mut range = self.history_tail(earlier, end, fragments.len(), byte_limit)?;
            if range.start == range.end && end > 0 {
                let row = self.history_row(earlier)?;
                let mut last_cluster = 0;
                clusters(row, 0, end, |start, _, _| {
                    last_cluster = start;
                    true
                });
                range = self.history_range(self.point_at(earlier, last_cluster)?, range.end)?;
            }
            if range.start != page.range.start || range.end != page.range.end {
                page.earlier = range;
                page.flags |= PAGE_HAS_EARLIER;
            }
        }
        Ok(page)
    }

    pub fn capture_history_search(&self, checkpoint: Option<&HistorySearchCheckpoint>, query_length: usize,
        context: usize, milliseconds: u32) -> Result<HistorySearchWindow, i32>
    {
        if milliseconds == 0 { return Err(BUSY); }
        let slice_deadline = Instant::now() + Duration::from_millis(u64::from(milliseconds.min(20)));
        let last_index = self.history_len().checked_sub(1).ok_or(RANGE_EVICTED)?;
        let (start, end) = if let Some(checkpoint) = checkpoint { (checkpoint.position, checkpoint.end) }
            else { (self.point_at(0, 0)?, self.point_at(last_index, self.history_row(last_index)?.plain.len())?) };
        let remaining = self.range_proof(start, end, false)?;
        if checkpoint.map_or(false, |cursor| cursor.remaining != remaining) { return Err(RANGE_CHANGED); }
        let (first, offset) = self.locate(start, false)?;
        let (last, end_offset) = self.locate(end, true)?;
        let mut context_first = self.history_line(start.line)?.ok_or(RANGE_EVICTED)?.0;
        for _ in 0..context {
            if context_first == 0 { break; }
            context_first = self.history_line(self.history_row(context_first - 1)?.logical.id)?.ok_or(RANGE_EVICTED)?.0;
        }
        let context_start = self.point_at(context_first, 0)?;
        let context_prefix = if context_start == start { None } else { Some(self.history_range(context_start, start)?) };
        let mut parts = Vec::new();
        let (mut progress, mut covered, mut bytes) = (start, start, 0);
        let mut part: Option<SearchPart> = None;
        for index in first..=last.min(first + SEARCH_ROWS - 1) {
            if index > first && Instant::now() >= slice_deadline { break; }
            let row = self.history_row(index)?;
            let from = if index == first { offset } else { 0 };
            let to = if index == last { end_offset } else { row.plain.len() };
            let slice = self.history_slice(index, from, to, PREFIX, SEARCH_BYTES - bytes, u32::MAX)?;
            let cut = slice.end as usize;
            if cut == from && cut != to { break; }
            bytes += slice.bytes;
            let current = part.get_or_insert_with(|| SearchPart { start: HistoryPoint::at(row, from), ..SearchPart::default() });
            current.push(row, from, cut);
            covered = HistoryPoint::at(row, cut);
            progress = if cut < to { covered } else if index == last { end } else { self.point_at(index + 1, 0)? };
            if cut == to && (!row.soft || index == last) {
                let mut finished = part.take().unwrap();
                finished.owned = finished.text.len();
                finished.next = progress;
                parts.push(finished);
            }
            if cut < to || bytes == SEARCH_BYTES { break; }
        }
        if let Some(mut partial) = part {
            partial.owned = partial.text.len();
            partial.next = progress;
            let (mut index, mut from) = self.locate(covered, false)?;
            while index <= last && partial.text.len() - partial.owned < query_length - 1 {
                let row = self.history_row(index)?;
                if row.logical.id != partial.start.line { break; }
                let to = if index == last { end_offset } else { row.plain.len() };
                let units = query_length - 1 - (partial.text.len() - partial.owned);
                let cut = self.history_slice(index, from, to, OVERLAP, units as u32, u32::MAX)?.end as usize;
                partial.push(row, from, cut);
                covered = HistoryPoint::at(row, cut);
                if !row.soft { break; }
                index += 1;
                from = 0;
            }
            parts.push(partial);
        }
        let coverage = if start == covered { None } else { Some(self.search_range(start, covered, end)?) };
        Ok(HistorySearchWindow { start: HistorySearchCheckpoint { position: start, end, remaining }, covered: coverage,
            context_prefix, parts, progress: start, matches: Vec::new(), query_length })
    }

    pub fn complete_history_search(&self, window: &HistorySearchWindow, context: usize, milliseconds: u32,
        matches: &mut [std::mem::MaybeUninit<HistoryMatchView>]) -> Result<HistorySearchPageView, i32>
    {
        if milliseconds == 0 { return Err(BUSY); }
        let deadline = Instant::now() + Duration::from_millis(u64::from(milliseconds));
        let start = window.start;
        let mut page = HistorySearchPageView { start, next: start, covered: HistoryRange::default(),
            scanned: HistoryRange::default(), count: window.matches.len() as u32, flags: 0 };
        if let Some(covered) = window.covered {
            self.verify_history_range(&covered)?;
            page.covered = covered;
            page.flags |= SEARCH_HAS_COVERAGE;
        }
        if self.range_proof(start.position, start.end, false)? != start.remaining { return Err(RANGE_CHANGED); }
        if !window.matches.is_empty() {
            if let Some(prefix) = &window.context_prefix { self.verify_history_range(prefix)?; }
        }
        let (end_row, _) = self.locate(start.end, true)?;
        if matches.len() < window.matches.len() { return Err(1); }
        for (output, found) in matches.iter_mut().zip(&window.matches) {
            if Instant::now() >= deadline { return Err(BUSY); }
            let part = &window.parts[found.part];
            let position = part.position(found.offset);
            let (mut left, mut right) = self.history_line(position.line)?.ok_or(RANGE_EVICTED)?;
            for _ in 0..context {
                if left > 0 { left = self.history_line(self.history_row(left - 1)?.logical.id)?.ok_or(RANGE_EVICTED)?.0; }
                if right < end_row {
                    right = self.history_line(self.history_row(right + 1)?.logical.id)?.ok_or(RANGE_EVICTED)?.1;
                }
            }
            let beginning = self.point_at(left, 0)?;
            let range = if right >= end_row {
                self.search_range(beginning, start.end, start.end)?
            } else {
                self.history_range(beginning, self.point_at(right, self.history_row(right)?.plain.len())?)?
            };
            output.write(HistoryMatchView { position, context: range, text: part.text[found.offset..].as_ptr(),
                length: window.query_length as u32 });
        }
        if window.progress != start.position {
            page.scanned = self.search_range(start.position, window.progress, start.end)?;
            page.flags |= SEARCH_HAS_SCANNED;
        }
        if window.progress != start.end {
            if window.progress == start.position && window.matches.is_empty() { return Err(BUSY); }
            page.next = HistorySearchCheckpoint { position: window.progress, end: start.end,
                remaining: self.range_proof(window.progress, start.end, false)? };
            page.flags |= SEARCH_HAS_NEXT;
        }
        if Instant::now() >= deadline { return Err(BUSY); }
        Ok(page)
    }

    #[cfg(test)]
    pub fn history_row_view(&self, index: usize) -> Result<HistoryRowView, i32> {
        let row = self.history_row(index)?;
        Ok(HistoryRowView {
            logical_id: row.logical.id,
            offset: row.logical.utf16,
            cluster_offset: row.logical.stored,
            text: row.plain.as_ptr(),
            length: row.plain.len() as i32,
            soft_wrap: row.soft as u32,
        })
    }

    pub fn history_line(&self, line: i64) -> Result<Option<(usize, usize)>, i32> {
        let archive = self.history_index.as_ref().ok_or(BUSY)?;
        let mut bounds = archive.lines.get(&line).map(|&(first, last)| {
            (
                (first - archive.head) as usize,
                (last - archive.head) as usize,
            )
        });
        if let Some(&(first, last)) = archive.page.get(&line) {
            let count = self.history.len();
            bounds = Some((bounds.map_or(first + count, |old| old.0), last + count));
        }
        Ok(bounds)
    }

    pub fn history_aggregate(&self, mut start: usize, end: usize) -> Result<HistoryProof, i32> {
        let archive = self.history_index.as_ref().ok_or(BUSY)?;
        let mut result = HistoryProof::EMPTY;
        if start > end || end > self.history.len() + self.normal_rows().len() {
            return Err(1);
        }
        if start < self.history.len() {
            let archived_end = end.min(self.history.len());
            result = archive.archived_proof(start, archived_end);
            start = archived_end;
        }
        for index in start..end {
            result = result.append(whole(self.history_row(index)?, archive.bases));
        }
        Ok(result)
    }

    pub fn history_fragment(
        &self,
        index: usize,
        from: usize,
        to: usize,
    ) -> Result<HistoryProof, i32> {
        let archive = self.history_index.as_ref().ok_or(BUSY)?;
        let row = self.history_row(index)?;
        if from > to || to > row.plain.len() {
            return Err(1);
        }
        Ok(fragment(row, archive.bases, from, to))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn observation_can_catch_up_when_output_evicts_a_full_build_slice() {
        let mut model = Model::new(80, 24, 5000, 1);
        for _ in 0..5000 {
            model.feed(0, b"initial\r\n", 0).unwrap();
        }
        let bases = [257, 263];
        assert_eq!(model.prepare_history(bases, true), Err(BUSY));
        for _ in 0..2400 {
            model.feed(0, b"streaming\r\n", 0).unwrap();
        }
        assert_eq!(model.prepare_history(bases, true), Ok(5000));
        for _ in 0..2400 {
            model.feed(0, b"continued\r\n", 0).unwrap();
        }
        assert_eq!(model.prepare_history(bases, false), Ok(5000));
    }
}
