use super::{Model, Row, Unicode};
use std::collections::HashMap;
use std::mem::size_of;

const BUILD_ROWS: usize = 2048;
pub(super) const BUSY: i32 = 6;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct HistoryProof {
    pub first: u64,
    pub second: u64,
    pub first_power: u64,
    pub second_power: u64,
    pub length: i64,
    pub revision: i64,
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

// Each boundary is a UTF-16 offset between complete Unicode clusters.
fn clusters(text: &[u16], unicode: &Unicode, mut visit: impl FnMut(usize, usize, u32) -> bool) {
    if text.iter().all(|unit| (0x20..=0x7e).contains(unit)) {
        for index in 0..text.len() {
            if !visit(index, index + 1, 1) {
                break;
            }
        }
        return;
    }
    let mut scalars = [0; 256];
    let (mut length, mut start, mut end, mut bytes) = (0, 0, 0, 0);
    for character in char::decode_utf16(text.iter().copied()) {
        let character = character.unwrap_or(char::REPLACEMENT_CHARACTER);
        let scalar = character as u32;
        if length != 0 && !unicode.joins(&scalars[..length], scalar) {
            if !visit(start, end, bytes) {
                return;
            }
            length = 0;
            start = end;
            bytes = 0;
        }
        scalars[length] = scalar;
        length += 1;
        end += character.len_utf16();
        bytes += character.len_utf8() as u32;
    }
    if end > start {
        visit(start, end, bytes);
    }
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

type Prior = (i64, i64, i64, bool);

fn normalize(row: &mut Row, prior: Option<Prior>, unicode: &Unicode) -> Prior {
    if let Some((line, offset, cluster, wrap)) = prior {
        row.logical_id = if wrap { line } else { row.id };
        row.logical_offset = if wrap { offset } else { 0 };
        row.logical_cluster_offset = if wrap { cluster } else { 0 };
    }
    row.refresh_plain();
    after(row, unicode)
}

fn after(row: &Row, unicode: &Unicode) -> Prior {
    (
        row.logical_id,
        row.logical_offset + row.plain.len() as i64,
        row.logical_cluster_offset + unicode.count_utf16(&row.plain) as i64,
        row.soft,
    )
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
            0 => {
                clusters(text, &self.unicode, |start, end, size| {
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
            1 => {
                let total = char::decode_utf16(text.iter().copied())
                    .map(|character| {
                        character.unwrap_or(char::REPLACEMENT_CHARACTER).len_utf8() as u32
                    })
                    .sum::<u32>();
                result.bytes = total;
                result.end = to as u32;
                if total > bytes {
                    clusters(text, &self.unicode, |_, end, size| {
                        result.bytes -= size;
                        result.start = (from + end) as u32;
                        result.bytes > bytes
                    });
                }
            }
            2 => {
                clusters(text, &self.unicode, |start, end, _| {
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
            .map(|row| row.potential_units() * 8 + 64)
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
        let mut prior = self.history.back().map(|row| after(row, &self.unicode));
        archive.page.clear();
        let page = self
            .parked
            .as_mut()
            .map_or(&mut self.screen, |parked| &mut parked.screen);
        for (index, row) in page.iter_mut().enumerate() {
            prior = Some(normalize(row, prior, &self.unicode));
            archive
                .page
                .entry(row.logical_id)
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
            .map(|i| after(&self.history[i], &self.unicode));
        for row in self.history.iter_mut().skip(indexed).take(maximum) {
            let before = row.bytes();
            prior = Some(normalize(row, prior, &self.unicode));
            self.history_bytes = self.history_bytes - before + row.bytes();
            archive.append(row.logical_id, whole(row, archive.bases));
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

    pub fn history_row_view(&self, index: usize) -> Result<HistoryRowView, i32> {
        let row = self.history_row(index)?;
        Ok(HistoryRowView {
            logical_id: row.logical_id,
            offset: row.logical_offset,
            cluster_offset: row.logical_cluster_offset,
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
