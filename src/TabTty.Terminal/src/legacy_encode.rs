#[cfg(windows)]
use crate::windows_icu;

const LIMIT: usize = 1024 * 1024;

pub(crate) fn encode(
    page: u32,
    input: &[u16],
    flags: u32,
    mut destination: Option<&mut [u8]>,
) -> Result<usize, i32> {
    #[cfg(windows)]
    if let Some(name) = icu_name(page, flags) {
        return windows_icu::with_converter(name, |converter| {
            // ICU can ignore some Unicode format characters. Validate every
            // scalar exactly before accepting any byte from the full conversion.
            for scalar in char::decode_utf16(input.iter().copied()) {
                let mut original = [0; 2];
                let original = scalar.map_err(|_| 1)?.encode_utf16(&mut original);
                let mut bytes = [0; 16];
                let count = converter.encode(original, &mut bytes)?;
                let mut decoded = [0; 2];
                if count == 0
                    || (page == 51932 && !euc_unit(&bytes[..count]))
                    || converter.decode(&bytes[..count], &mut decoded)? != original.len()
                    || &decoded[..original.len()] != original
                {
                    return Err(1);
                }
            }
            let length = converter.encoded_length(input)?;
            if length > LIMIT {
                return Err(2);
            }
            if let Some(bytes) = destination {
                if bytes.len() < length {
                    return Err(2);
                }
                if converter.encode(input, bytes)? != length {
                    return Err(3);
                }
            }
            Ok(length)
        });
    }
    #[cfg(not(windows))]
    let _ = flags;
    if !windows_page(page) {
        return Err(1);
    }
    let mut length = 0;
    for scalar in char::decode_utf16(input.iter().copied()) {
        let mut encoded = [0; 2];
        let count = windows::encode(page, scalar.map_err(|_| 1)?, &mut encoded)?;
        let end = length + count;
        if end > LIMIT {
            return Err(2);
        }
        if let Some(bytes) = &mut destination {
            bytes
                .get_mut(length..end)
                .ok_or(2)?
                .copy_from_slice(&encoded[..count]);
        }
        length = end;
    }
    Ok(length)
}

fn windows_page(page: u32) -> bool {
    matches!(
        page, 437 | 737 | 775 | 850 | 852 | 855 | 857 | 860..=866 | 869 | 874 |
        932 | 936 | 949 | 950 | 1250..=1258 | 20866 | 28591..=28599 | 28603 | 28605
    )
}

#[cfg(windows)]
pub(crate) fn supports(page: u32) -> bool {
    windows_page(page) || icu_name(page, 0).is_some()
}

#[cfg(windows)]
fn icu_name(page: u32, flags: u32) -> Option<&'static [u8]> {
    match page {
        51932 => Some(windows_icu::EUC_JP),
        50220 if flags & 8 != 0 => Some(windows_icu::JIS_SHIFT_KANA),
        50220 => Some(windows_icu::JIS_EIGHT_BIT_KANA),
        _ => windows_icu::iso_name(page),
    }
}

#[cfg(windows)]
fn euc_unit(input: &[u8]) -> bool {
    matches!(
        input,
        [0..=0x7f] | [0x8e, 0xa1..=0xdf] | [0xa1..=0xfe, 0xa1..=0xfe]
    )
}

pub(crate) fn decode_unit(page: u32, input: &[u8]) -> Option<u16> {
    if windows_page(page) {
        return windows::decode(page, input);
    }
    #[cfg(windows)]
    if let Some(name) = if page == 51932 {
        if !euc_unit(input) {
            return None;
        }
        Some(windows_icu::EUC_JP)
    } else {
        windows_icu::iso_name(page)
    } {
        return windows_icu::with_converter(name, |converter| {
            let mut decoded = [0; 2];
            if converter.decode(input, &mut decoded)? == 1 {
                Ok(decoded[0])
            } else {
                Err(1)
            }
        })
        .ok();
    }
    None
}

#[cfg(windows)]
mod windows {
    #[link(name = "kernel32")]
    extern "system" {
        fn MultiByteToWideChar(
            page: u32,
            flags: u32,
            input: *const u8,
            bytes: i32,
            output: *mut u16,
            units: i32,
        ) -> i32;
        fn WideCharToMultiByte(
            page: u32,
            flags: u32,
            input: *const u16,
            units: i32,
            output: *mut u8,
            bytes: i32,
            default: *const u8,
            substituted: *mut i32,
        ) -> i32;
    }

    pub(super) fn decode(page: u32, input: &[u8]) -> Option<u16> {
        let mut output = [0; 2];
        let length = unsafe {
            MultiByteToWideChar(
                page,
                8,
                input.as_ptr(),
                input.len() as i32,
                output.as_mut_ptr(),
                2,
            )
        };
        (length == 1).then(|| output[0])
    }

    pub(super) fn encode(page: u32, scalar: char, output: &mut [u8; 2]) -> Result<usize, i32> {
        let mut input = [0; 2];
        let mut substituted = 0;
        let input = scalar.encode_utf16(&mut input);
        if input.len() != 1 {
            return Err(1);
        }
        let length = unsafe {
            WideCharToMultiByte(
                page,
                0x400,
                input.as_ptr(),
                1,
                output.as_mut_ptr(),
                2,
                std::ptr::null(),
                &mut substituted,
            )
        };
        if length <= 0
            || substituted != 0
            || decode(page, &output[..length as usize]) != Some(input[0])
        {
            Err(1)
        } else {
            Ok(length as usize)
        }
    }
}

#[cfg(not(windows))]
mod windows {
    pub(super) fn decode(_: u32, _: &[u8]) -> Option<u16> {
        None
    }
    pub(super) fn encode(_: u32, _: char, _: &mut [u8; 2]) -> Result<usize, i32> {
        Err(1)
    }
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    fn bytes(page: u32, text: &str, flags: u32) -> Result<Vec<u8>, i32> {
        let input: Vec<_> = text.encode_utf16().collect();
        let length = encode(page, &input, flags, None)?;
        let mut output = vec![0; length];
        let written = encode(page, &input, flags, Some(&mut output))?;
        assert_eq!(written, length);
        Ok(output)
    }

    #[test]
    fn windows_mappings_leave_undefined_units_undefined() {
        for byte in [0xa0, 0xfd, 0xfe, 0xff] {
            assert_eq!(decode_unit(932, &[byte]), None);
        }
        assert_eq!(decode_unit(857, &[0xd5]), None);
        assert_eq!(decode_unit(950, &[0xa2, 0xa4]), Some(0x2550));
        assert_eq!(bytes(932, "\u{301c}", 0), Err(1));
    }

    #[test]
    fn icu_owns_iso_euc_and_jis_mapping() {
        assert_eq!(bytes(28600, "Į", 0).unwrap(), [0xc7]);
        assert_eq!(bytes(28601, "ก", 0).unwrap(), [0xa1]);
        assert_eq!(bytes(28604, "Ḃ", 0).unwrap(), [0xa1]);
        assert_eq!(decode_unit(28601, &[0xdb]), None);
        assert_eq!(
            bytes(51932, "～①纊", 0).unwrap(),
            [0xa1, 0xc1, 0xad, 0xa1, 0xf9, 0xa1]
        );
        assert_eq!(bytes(51932, "¥", 0), Err(1));
        assert_eq!(decode_unit(51932, &[0x8f, 0xa1, 0xa1]), None);
        assert_eq!(bytes(50220, "～", 0).unwrap(), b"\x1b$B!A\x1b(B");
        assert_eq!(bytes(50220, "ｶ", 0).unwrap(), [0xb6]);
        assert_eq!(bytes(50220, "ｶ", 8).unwrap(), [0x0e, 0x36, 0x0f]);
    }
}
