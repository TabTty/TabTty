// Direct port of TabTty.Core/Unicode17.cs using the identical pinned tables.
const DATA: &[u8] = include_bytes!("Unicode17.bin");
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
    pub fn joins(&self, cluster: &[u32], next: u32) -> bool {
        let previous_scalar = cluster[cluster.len() - 1];
        if previous_scalar < 128 && next < 128 {
            return previous_scalar == 13 && next == 10;
        }
        let previous = self.get(1, previous_scalar);
        let following = self.get(1, next);
        if previous == 1 && following == 2 {
            return true;
        }
        if (1..=3).contains(&previous) || (1..=3).contains(&following) {
            return false;
        }
        if previous == 9 && matches!(following, 9 | 10 | 12 | 13) {
            return true;
        }
        if matches!(previous, 10 | 12) && matches!(following, 10 | 11) {
            return true;
        }
        if matches!(previous, 11 | 13) && following == 11 {
            return true;
        }
        if matches!(following, 4 | 5 | 8) || previous == 7 {
            return true;
        }
        if self.get(2, next) == 1 {
            let mut saw_linker = false;
            for &scalar in cluster.iter().rev() {
                let property = self.get(2, scalar);
                if property == 3 {
                    saw_linker = true;
                } else if property != 2 {
                    if property == 1 && saw_linker {
                        return true;
                    }
                    break;
                }
            }
        }
        if previous == 5 && self.get(6, next) != 0 {
            let mut index = cluster.len() as isize - 2;
            while index >= 0 && self.get(1, cluster[index as usize]) == 4 {
                index -= 1;
            }
            if index >= 0 && self.get(6, cluster[index as usize]) != 0 {
                return true;
            }
        }
        if previous == 6 && following == 6 {
            return cluster
                .iter()
                .rev()
                .take_while(|&&s| self.get(1, s) == 6)
                .count()
                & 1
                != 0;
        }
        false
    }
    pub fn width(&self, cluster: &[u32], ambiguous: usize) -> usize {
        if cluster.len() == 1 && cluster[0] < 128 {
            return 1;
        }
        let mut width = 1;
        let (mut emoji, mut text, mut modifier_base) = (false, false, false);
        for (index, &scalar) in cluster.iter().enumerate() {
            if self.get(3, scalar) == 0 && !matches!(self.get(1, scalar), 4 | 5) {
                width = width.max(match self.get(0, scalar) {
                    2 => 2,
                    1 => ambiguous,
                    _ => 1,
                });
            }
            if self.get(5, scalar) != 0 {
                emoji = true;
            }
            if scalar == 0xfe0e {
                text = true;
            }
            if scalar == 0xfe0f && index != 0 && self.get(4, cluster[index - 1]) != 0 {
                emoji = true;
            }
            if modifier_base && self.get(8, scalar) != 0 {
                emoji = true;
            }
            if self.get(7, scalar) != 0 {
                modifier_base = true;
            }
            if scalar == 0x20e3 && matches!(cluster[0], 35 | 42 | 48..=57) {
                emoji = true;
            }
        }
        if emoji && !text {
            2
        } else {
            width
        }
    }
    pub fn count_utf16(&self, text: &[u16]) -> usize {
        // Display rows commonly contain only printable ASCII. Every such
        // code unit is a complete grapheme; controls and all other Unicode
        // retain the exact table-driven path below.
        if text.iter().all(|unit| (0x20..=0x7e).contains(unit)) {
            return text.len();
        }
        let mut cluster = [0u32; 256];
        let mut length = 0;
        let mut count = 0;
        for c in char::decode_utf16(text.iter().copied()) {
            let scalar = c.unwrap_or(char::REPLACEMENT_CHARACTER) as u32;
            if length == 0 || !self.joins(&cluster[..length], scalar) {
                count += 1;
                length = 0;
            }
            assert!(
                length < cluster.len(),
                "Terminal text cluster exceeds 256 Unicode scalars."
            );
            cluster[length] = scalar;
            length += 1;
        }
        count
    }
}
fn read_u32(offset: usize) -> u32 {
    u32::from_le_bytes([
        DATA[offset],
        DATA[offset + 1],
        DATA[offset + 2],
        DATA[offset + 3],
    ])
}
