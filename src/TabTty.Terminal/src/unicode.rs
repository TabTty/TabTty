// Direct port of TabTty.Core/Unicode17.cs using the identical pinned tables.
const DATA: &[u8] = include_bytes!("Unicode17.bin");
const EMOJI: &[u8] = include_bytes!("Emoji17.bin");
#[derive(Clone)]
pub struct Unicode {
    offsets: [usize; 9],
    lengths: [usize; 9],
}
impl Unicode {
    pub fn new() -> Self {
        let mut result = Self {
            offsets: [0; 9],
            lengths: [0; 9],
        };
        let mut offset = 0;
        for table in 0..9 {
            result.lengths[table] = read_u32(offset) as usize;
            offset += 4;
            result.offsets[table] = offset;
            offset += result.lengths[table] * 9;
        }
        assert_eq!(offset, DATA.len());
        assert_eq!(&EMOJI[..8], b"TTEM170\0");
        let count = emoji_word(0) as usize;
        let last_offset = 2 + emoji_word(1) as usize * 2 + count;
        assert_eq!(EMOJI.len(), 8 + 4 * (last_offset + 1 + emoji_word(last_offset) as usize));
        result
    }
    fn get(&self, table: usize, scalar: u32) -> u8 {
        let (mut low, mut high) = (0usize, self.lengths[table]);
        while low < high {
            let middle = low + (high - low) / 2;
            let offset = self.offsets[table] + middle * 9;
            if scalar < read_u32(offset) {
                high = middle;
            } else if scalar > read_u32(offset + 4) {
                low = middle + 1;
            } else {
                return DATA[offset + 8];
            }
        }
        0
    }
    pub fn is_mark(&self, scalar: u32) -> bool {
        scalar >= 128 && self.get(3, scalar) != 0
    }

    pub fn width(&self, cluster: &[u32], ambiguous: usize) -> usize {
        if cluster.len() == 1 && cluster[0] < 128 {
            return 1;
        }
        let mut width = 1;
        for &scalar in cluster {
            if self.get(3, scalar) == 0 && !matches!(self.get(1, scalar), 4 | 5) {
                width = width.max(match self.get(0, scalar) {
                    2 => 2,
                    1 => ambiguous,
                    _ => 1,
                });
            }
        }
        if cluster.contains(&0xfe0e) {
            return width;
        }
        let mut core = cluster;
        while let Some((&last, prefix)) = core.split_last() {
            if self.get(3, last) == 0
                || !matches!(self.get(1, last), 4 | 8)
                || last == 0x20e3
                || is_variation_selector(last)
            {
                break;
            }
            core = prefix;
        }
        if emoji_sequence(core) { 2 } else { width }
    }
    #[cfg(test)]
    pub fn count_utf16(&self, text: &[u16]) -> usize {
        // Display rows commonly contain only printable ASCII. Every such
        // code unit is a complete grapheme; controls and all other Unicode
        // retain the exact table-driven path below.
        if text.iter().all(|unit| (0x20..=0x7e).contains(unit)) {
            return text.len();
        }
        let mut state = GraphemeState::default();
        let mut count = 0;
        for c in char::decode_utf16(text.iter().copied()) {
            if state.push(self, c.unwrap_or(char::REPLACEMENT_CHARACTER) as u32) {
                count += 1;
            }
        }
        count
    }

    pub fn analyze_paste(&self, text: &[u16]) -> Result<(usize, usize), i32> {
        let mut state = GraphemeState::default();
        let (mut characters, mut lines) = (0, 1);
        for c in char::decode_utf16(text.iter().copied()) {
            let c = c.map_err(|_| 1)?;
            if c.is_control() && !matches!(c, '\t' | '\r' | '\n') {
                return Err(1);
            }
            if state.push(self, c as u32) {
                characters += 1;
                if matches!(c, '\r' | '\n') {
                    lines += 1;
                }
            }
        }
        Ok((characters, lines))
    }

    pub fn paste_chunk(
        &self,
        text: &[u16],
        mut context: u32,
        character_delay: u32,
        line_delay: u32,
    ) -> Result<[u32; 4], i32> {
        const MAX_UNITS: usize = 1024;
        let mut state = GraphemeState::decode(context);
        let mut newline = context & (1 << 14) != 0;
        let (mut length, mut completed, mut delay) = (0, 0, 0);
        let mut chars = char::decode_utf16(text.iter().copied()).peekable();
        while let Some(value) = chars.next() {
            let c = value.map_err(|_| 1)?;
            let next = match chars.peek() {
                Some(Ok(next)) => Some(*next),
                Some(Err(_)) => return Err(1),
                None => None,
            };
            let width = c.len_utf16();
            if length + width + usize::from(c == '\r' && next == Some('\n')) > MAX_UNITS {
                break;
            }
            length += width;
            if state.push(self, c as u32) {
                newline = matches!(c, '\r' | '\n');
            }
            if next.map_or(true, |next| !state.joins(self, next as u32)) {
                completed += 1;
                delay = character_delay + if newline { line_delay } else { 0 };
                state = GraphemeState::default();
                newline = false;
                if delay != 0 { break; }
            }
            if length == MAX_UNITS { break; }
        }
        context = state.encode() | (u32::from(newline) << 14);
        Ok([length as u32, completed, delay, context])
    }

    pub fn next_grapheme(&self, text: &[u16]) -> Result<usize, i32> {
        let mut state = GraphemeState::default();
        let mut length = 0;
        for scalar in char::decode_utf16(text.iter().copied()) {
            let c = scalar.map_err(|_| 1)?;
            if state.push(self, c as u32) && length != 0 {
                break;
            }
            length += c.len_utf16();
        }
        Ok(length)
    }
}

// Unicode 17 extended grapheme context. These fields summarize suffixes of the
// complete stream and do not depend on how many scalars the terminal retains.
#[derive(Clone, Copy, Default)]
pub struct GraphemeState {
    previous: Option<u8>,
    odd_ri: bool,
    pictograph_extend: bool,
    pictograph_zwj: bool,
    indic: u8,
}

impl GraphemeState {
    fn encode(self) -> u32 {
        self.previous.map_or(0, |p| p as u32 + 1)
            | ((self.odd_ri as u32) << 8)
            | ((self.pictograph_extend as u32) << 9)
            | ((self.pictograph_zwj as u32) << 10)
            | ((self.indic as u32) << 11)
    }

    fn decode(bits: u32) -> Self {
        Self {
            previous: (bits as u8).checked_sub(1),
            odd_ri: bits & 0x100 != 0,
            pictograph_extend: bits & 0x200 != 0,
            pictograph_zwj: bits & 0x400 != 0,
            indic: ((bits >> 11) & 3) as u8,
        }
    }

    fn joins(&self, u: &Unicode, next: u32) -> bool {
        let previous = match self.previous {
            Some(previous) => previous,
            None => return false,
        };
        if (0x20..=0x7e).contains(&next) {
            return previous == 7;
        }
        let following = u.get(1, next);
        if previous == 1 && following == 2 {
            return true;
        }
        if matches!(previous, 1..=3) || matches!(following, 1..=3) {
            return false;
        }
        (previous == 9 && matches!(following, 9 | 10 | 12 | 13))
            || (matches!(previous, 10 | 12) && matches!(following, 10 | 11))
            || (matches!(previous, 11 | 13) && following == 11)
            || matches!(following, 4 | 5 | 8)
            || previous == 7
            || (self.indic == 2 && u.get(2, next) == 1)
            || (self.pictograph_zwj && u.get(6, next) != 0)
            || (following == 6 && self.odd_ri)
    }

    // Returns true at the start of a grapheme, including the first scalar.
    pub fn push(&mut self, u: &Unicode, scalar: u32) -> bool {
        if (0x20..=0x7e).contains(&scalar) && !matches!(self.previous, Some(7)) {
            *self = Self { previous: Some(0), ..Self::default() };
            return true;
        }
        let boundary = !self.joins(u, scalar);
        if boundary {
            *self = Self::default();
        }
        let property = u.get(1, scalar);
        self.previous = Some(property);
        self.odd_ri = property == 6 && !self.odd_ri;
        self.pictograph_zwj = property == 5 && self.pictograph_extend;
        self.pictograph_extend =
            u.get(6, scalar) != 0 || (property == 4 && self.pictograph_extend);
        self.indic = match u.get(2, scalar) {
            1 => 1,
            3 if self.indic != 0 => 2,
            2 => self.indic,
            _ => 0,
        };
        boundary
    }
}
fn emoji_word(offset: usize) -> u32 {
    let offset = 8 + offset * 4;
    u32::from_le_bytes(EMOJI[offset..offset + 4].try_into().unwrap())
}

fn is_variation_selector(scalar: u32) -> bool {
    (0..emoji_word(1) as usize).any(|i| {
        let first = emoji_word(2 + i * 2);
        let last = emoji_word(3 + i * 2);
        (first..=last).contains(&scalar)
    })
}

fn emoji_sequence(cluster: &[u32]) -> bool {
    let count = emoji_word(0) as usize;
    let offsets = 2 + emoji_word(1) as usize * 2;
    let data = offsets + count + 1;
    let (mut low, mut high) = (0, count);
    while low < high {
        let middle = low + (high - low) / 2;
        let start = emoji_word(offsets + middle) as usize;
        let end = emoji_word(offsets + middle + 1) as usize;
        let spelling = (start..end).map(|i| emoji_word(data + i));
        match spelling.cmp(cluster.iter().copied()) {
            std::cmp::Ordering::Less => low = middle + 1,
            std::cmp::Ordering::Greater => high = middle,
            std::cmp::Ordering::Equal => return true,
        }
    }
    false
}
fn read_u32(offset: usize) -> u32 {
    u32::from_le_bytes([
        DATA[offset],
        DATA[offset + 1],
        DATA[offset + 2],
        DATA[offset + 3],
    ])
}
