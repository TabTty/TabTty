use crate::legacy_encode::{decode_unit, supports};
use crate::windows_icu::{self, Converter};

const JIS: u32 = 50220;
const EUC_JP: u32 = 51932;
const REPLACEMENT: u32 = 0xfffd;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct DecodedScalar {
    pub scalar: u32,
    pub interval: u32,
}

#[repr(C)]
#[derive(Default)]
pub struct Decoded {
    pub length: u32,
    pub partial: u32,
    pub scalars: [DecodedScalar; 16],
}

impl Decoded {
    fn push(&mut self, scalar: u32, interval: u32) {
        let scalar = char::from_u32(scalar).map_or(REPLACEMENT, |valid| valid as u32);
        self.scalars[self.length as usize] = DecodedScalar { scalar, interval };
        self.length += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::Receiver;

    fn values(decoded: super::Decoded) -> Vec<(u32, u32)> {
        decoded.scalars[..decoded.length as usize]
            .iter()
            .map(|value| (value.scalar, value.interval))
            .collect()
    }

    #[test]
    fn malformed_and_split_units_keep_the_interval_of_the_first_byte() {
        let mut data = Receiver::new(932, true).unwrap();
        let mut stderr = Receiver::new(932, true).unwrap();
        assert_eq!(data.step(Some(0x82), false, 10).unwrap().partial, 1);
        assert_eq!(
            values(stderr.step(Some(b'A'), false, 11).unwrap()),
            vec![(65, 11)]
        );
        assert_eq!(
            values(data.step(Some(0xa0), false, 12).unwrap()),
            vec![(0x3042, 10)]
        );
        assert_eq!(data.step(Some(0x82), false, 13).unwrap().partial, 1);
        assert_eq!(
            values(data.step(Some(0x1b), false, 14).unwrap()),
            vec![(0xfffd, 13), (0x1b, 14)]
        );
        assert_eq!(
            values(data.step(Some(0x0e), true, 14).unwrap()),
            vec![(0x0e, 14)]
        );
        assert!(values(data.step(Some(0x0e), false, 15).unwrap()).is_empty());
        assert_eq!(
            values(data.step(Some(0x31), false, 15).unwrap()),
            vec![(0xff71, 15)]
        );
        assert_eq!(data.step(Some(0x82), false, 16).unwrap().partial, 1);
        assert_eq!(
            values(data.step(None, false, 17).unwrap()),
            vec![(0xfffd, 16)]
        );
    }

    #[test]
    fn jis_keeps_codec_designation_and_vt_escapes_distinct() {
        let mut receiver = Receiver::new(50220, true).unwrap();
        for byte in b"\x1b$B" {
            assert!(values(receiver.step(Some(*byte), false, 20).unwrap()).is_empty());
        }
        assert_eq!(receiver.step(Some(0x24), false, 21).unwrap().partial, 1);
        assert_eq!(
            values(receiver.step(Some(0x22), false, 22).unwrap()),
            vec![(0x3042, 21)]
        );
        assert_eq!(receiver.step(Some(0x1b), false, 23).unwrap().partial, 1);
        assert_eq!(
            values(receiver.step(Some(b'['), false, 24).unwrap()),
            vec![(0x1b, 23), (b'[' as u32, 23)]
        );
        assert_eq!(receiver.step(Some(0x24), false, 25).unwrap().partial, 1);
        assert_eq!(
            values(receiver.step(Some(0x22), false, 26).unwrap()),
            vec![(0x3042, 25)]
        );
    }
}

pub struct Receiver {
    page: u32,
    shift_kana: bool,
    kana: bool,
    unit: [u8; 3],
    count: usize,
    unit_interval: u32,
    jis: Option<Converter>,
    designation: [u8; 4],
    designation_length: usize,
    designation_interval: u32,
    jis_partial: bool,
}

impl Receiver {
    pub fn new(page: u32, shift_kana: bool) -> Result<Self, i32> {
        if !supports(page) {
            return Err(1);
        }
        Ok(Self {
            page,
            shift_kana,
            kana: false,
            unit: [0; 3],
            count: 0,
            unit_interval: 0,
            jis: None,
            designation: [0; 4],
            designation_length: 0,
            designation_interval: 0,
            jis_partial: false,
        })
    }

    pub fn step(&mut self, byte: Option<u8>, in_vt: bool, interval: u32) -> Result<Decoded, i32> {
        let mut decoded = Decoded::default();
        if self.page == JIS {
            windows_icu::in_apartment(|| self.jis_step(byte, in_vt, interval, &mut decoded))?;
        } else if let Some(byte) = byte {
            self.stateless_step(byte, in_vt, interval, &mut decoded);
        } else if self.count != 0 {
            self.count = 0;
            decoded.push(REPLACEMENT, self.unit_interval);
        }
        decoded.partial =
            (self.count != 0 || self.designation_length != 0 || self.jis_partial) as u32;
        Ok(decoded)
    }

    fn stateless_step(&mut self, byte: u8, in_vt: bool, interval: u32, decoded: &mut Decoded) {
        if self.count != 0 {
            if self.accepts_continuation(byte) {
                self.unit[self.count] = byte;
                self.count += 1;
                if self.page != EUC_JP || self.unit[0] != 0x8f || self.count != 2 {
                    let scalar = decode_unit(self.page, &self.unit[..self.count]);
                    self.count = 0;
                    decoded.push(scalar.map_or(REPLACEMENT, u32::from), self.unit_interval);
                }
                return;
            }
            self.count = 0;
            decoded.push(REPLACEMENT, self.unit_interval);
        }
        if in_vt {
            decoded.push(byte.into(), interval);
        } else if self.page == 932 && self.shift_kana && matches!(byte, 0x0e | 0x0f) {
            self.kana = byte == 0x0e;
        } else if self.starts_unit(byte) {
            self.unit[0] = byte;
            self.count = 1;
            self.unit_interval = interval;
        } else {
            let byte = if self.kana && (0x21..=0x5f).contains(&byte) {
                byte + 0x80
            } else {
                byte
            };
            let scalar = if byte < 0x20 || byte == 0x7f {
                byte.into()
            } else {
                decode_unit(self.page, &[byte]).map_or(REPLACEMENT, u32::from)
            };
            decoded.push(scalar, interval);
        }
    }

    fn starts_unit(&self, byte: u8) -> bool {
        match self.page {
            932 => matches!(byte, 0x81..=0x9f | 0xe0..=0xfc),
            936 | 949 | 950 => (0x81..=0xfe).contains(&byte),
            EUC_JP => matches!(byte, 0x8e | 0x8f | 0xa1..=0xfe),
            _ => false,
        }
    }

    fn accepts_continuation(&self, byte: u8) -> bool {
        match self.page {
            932 => matches!(byte, 0x40..=0x7e | 0x80..=0xfc),
            936 => matches!(byte, 0x40..=0x7e | 0x80..=0xfe),
            949 => matches!(byte, 0x41..=0x5a | 0x61..=0x7a | 0x81..=0xfe),
            950 => matches!(byte, 0x40..=0x7e | 0xa1..=0xfe),
            EUC_JP if self.unit[0] == 0x8e => (0xa1..=0xdf).contains(&byte),
            EUC_JP => (0xa1..=0xfe).contains(&byte),
            _ => false,
        }
    }

    fn jis_step(
        &mut self,
        byte: Option<u8>,
        in_vt: bool,
        interval: u32,
        decoded: &mut Decoded,
    ) -> Result<(), i32> {
        let byte = match byte {
            Some(byte) => byte,
            None => {
                self.designation_length = 0;
                if self.jis.is_some() {
                    self.emit_jis(&[], true, false, interval, decoded)?;
                }
                return Ok(());
            }
        };
        if self.designation_length == 0 {
            if in_vt {
                decoded.push(byte.into(), interval);
            } else if byte == 0x1b {
                if self.jis_partial {
                    self.emit_jis(&[0], false, true, interval, decoded)?;
                }
                self.designation[0] = byte;
                self.designation_length = 1;
                self.designation_interval = interval;
            } else {
                self.emit_jis(&[byte], false, false, interval, decoded)?;
            }
            return Ok(());
        }
        if byte < 0x20 {
            match byte {
                0x1b => {
                    self.designation_length = 1;
                    self.designation_interval = interval;
                }
                0x18 | 0x1a => {
                    self.designation_length = 0;
                    decoded.push(byte.into(), interval);
                }
                _ => decoded.push(byte.into(), interval),
            }
            return Ok(());
        }
        self.designation[self.designation_length] = byte;
        self.designation_length += 1;
        let is_charset_escape = matches!(self.designation[1], b'$' | b'(' | b'&');
        if self.designation_length == 2 && is_charset_escape
            || self.designation_length == 3 && self.designation[1] == b'$' && byte == b'('
        {
            return Ok(());
        }
        let designation = self.designation;
        let length = std::mem::take(&mut self.designation_length);
        if is_charset_escape && length > 2 && !(designation[1] == b'(' && byte == b'0') {
            self.emit_jis(
                &designation[..length],
                false,
                false,
                self.designation_interval,
                decoded,
            )?;
        } else {
            for &byte in &designation[..length] {
                decoded.push(byte.into(), self.designation_interval);
            }
        }
        Ok(())
    }

    fn emit_jis(
        &mut self,
        bytes: &[u8],
        flush: bool,
        suppress_nul: bool,
        interval: u32,
        decoded: &mut Decoded,
    ) -> Result<(), i32> {
        if self.jis.is_none() {
            let name = if self.shift_kana {
                windows_icu::JIS_SHIFT_KANA
            } else {
                windows_icu::JIS_EIGHT_BIT_KANA
            };
            self.jis = Some(Converter::open(name, false)?);
        }
        let mut output = [0; 16];
        let (written, pending) = self
            .jis
            .as_mut()
            .unwrap()
            .receive(bytes, &mut output, flush)?;
        let mut source_interval = if self.jis_partial {
            self.unit_interval
        } else {
            interval
        };
        for character in char::decode_utf16(output[..written].iter().copied()) {
            let scalar = character.unwrap_or(char::REPLACEMENT_CHARACTER) as u32;
            if scalar != 0 || !suppress_nul {
                decoded.push(scalar, source_interval);
            }
            source_interval = interval;
        }
        if pending && (!self.jis_partial || written != 0) {
            self.unit_interval = interval;
        }
        self.jis_partial = pending;
        Ok(())
    }
}
