use std::io::Write;

pub(crate) fn key(
    key: u32,
    modifiers: u32,
    modes: u32,
    mut output: &mut [u8],
) -> Result<usize, i32> {
    if key >= 40 || modifiers > 7 || modes > 3 {
        return Err(1);
    }
    let capacity = output.len();
    if key >= 24 {
        let index = (key - 24) as usize;
        if modes & 2 != 0 {
            output
                .write_all(&[27, b'O', b"pqrstuvwxynMjkmo"[index]])
                .map_err(|_| 3)?;
        } else {
            output
                .write_all(&[b"0123456789.\r*+-/"[index]])
                .map_err(|_| 3)?;
        }
    } else if key <= 3 || (12..=15).contains(&key) {
        let function = key >= 12;
        let code = if function {
            b'P' + (key - 12) as u8
        } else {
            b'A' + key as u8
        };
        if modifiers != 0 {
            output
                .write_all(&[27, b'[', b'1', b';', b'1' + modifiers as u8, code])
                .map_err(|_| 3)?;
        } else {
            let prefix = if modes & 1 != 0 || function {
                b'O'
            } else {
                b'['
            };
            output.write_all(&[27, prefix, code]).map_err(|_| 3)?;
        }
    } else if key == 11 {
        output.write_all(b"\x1b").map_err(|_| 3)?;
    } else if key == 10 {
        output.write_all(b"\x1b[Z").map_err(|_| 3)?;
    } else {
        let parameter = if key < 10 {
            [1, 4, 2, 3, 5, 6][(key - 4) as usize]
        } else {
            [15, 17, 18, 19, 20, 21, 23, 24][(key - 16) as usize]
        };
        if modifiers == 0 {
            write!(output, "\x1b[{}~", parameter).map_err(|_| 3)?;
        } else {
            write!(output, "\x1b[{};{}~", parameter, modifiers + 1).map_err(|_| 3)?;
        }
    }
    Ok(capacity - output.len())
}

#[repr(C)]
pub struct MouseInput {
    pub button: i32,
    pub column: i32,
    pub row: i32,
    pub generation: i32,
    pub flags: u32,
}

pub(crate) fn mouse(
    input: &MouseInput,
    tracking: u32,
    generation: i32,
    columns: i32,
    rows: i32,
    mut output: &mut [u8],
) -> Result<usize, i32> {
    if input.flags > 31 || columns < 1 || rows < 1 {
        return Err(1);
    }
    let capacity = output.len();
    let sgr = tracking & 0x80000000 != 0;
    let tracking = tracking & 0x7fff_ffff;
    let release = input.flags & 8 != 0;
    let motion = input.flags & 16 != 0;
    if tracking == 0
        || generation != input.generation
        || !(0..=5).contains(&input.button)
        || (tracking == 9 && (release || motion || input.button >= 4))
        || (motion && !matches!(tracking, 1002 | 1003))
        || (motion && tracking == 1002 && input.button == 3)
    {
        return Ok(0);
    }
    let modifiers = (input.flags & 7) * 4;
    let mut button = if input.button >= 4 {
        64 + input.button as u32 - 4
    } else {
        input.button as u32
    };
    button |= modifiers;
    if motion {
        button |= 32;
    }
    let x = input.column.saturating_add(1).clamp(1, columns);
    let y = input.row.saturating_add(1).clamp(1, rows);
    if sgr {
        write!(
            output,
            "\x1b[<{};{};{}{}",
            button,
            x,
            y,
            if release { 'm' } else { 'M' }
        )
        .map_err(|_| 3)?;
    } else {
        if release {
            button = 3 | modifiers;
        }
        output
            .write_all(&[
                27,
                b'[',
                b'M',
                (32 + button) as u8,
                (32 + x.min(223)) as u8,
                (32 + y.min(223)) as u8,
            ])
            .map_err(|_| 3)?;
    }
    Ok(capacity - output.len())
}
