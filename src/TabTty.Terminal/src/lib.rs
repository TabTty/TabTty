//! Native UTF-8 terminal engine with transitional managed handoff.
mod input;
mod legacy_encode;
#[cfg(windows)]
mod legacy_receive;
mod model;
mod unicode;
#[cfg(windows)]
mod windows_icu;
use model::Model;
use std::ffi::c_void;
use std::mem::align_of;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;
use std::sync::atomic::{AtomicU64, Ordering};
static LIVE_BYTES: AtomicU64 = AtomicU64::new(0);
static PEAK_BYTES: AtomicU64 = AtomicU64::new(0);
static LIVE_MODELS: AtomicU64 = AtomicU64::new(0);
fn account(before: u64, after: u64) {
    let live = if after >= before {
        LIVE_BYTES.fetch_add(after - before, Ordering::Relaxed) + after - before
    } else {
        LIVE_BYTES.fetch_sub(before - after, Ordering::Relaxed) - (before - after)
    };
    let mut peak = PEAK_BYTES.load(Ordering::Relaxed);
    while live > peak {
        match PEAK_BYTES.compare_exchange_weak(peak, live, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => break,
            Err(current) => peak = current,
        }
    }
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct CellView {
    pub offset: u32,
    pub length: u16,
    pub width: i16,
    pub foreground: u32,
    pub background: u32,
    pub flags: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct RowView {
    pub id: i64,
    pub logical_id: i64,
    pub logical_offset: i64,
    pub display_revision: i64,
    pub cells: *const CellView,
    pub text: *const u16,
    pub cell_text: *const u16,
    pub cell_count: u32,
    pub text_length: u32,
    pub cell_text_length: u32,
    pub soft_wrap: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct SnapshotView {
    pub rows: *const RowView,
    pub row_count: u32,
    pub cursor_row: i32,
    pub cursor_column: i32,
    pub columns: i32,
    pub screen_rows: i32,
    pub screen_start: i32,
    pub flags: u32,
    pub cursor_shape: u32,
    pub mouse_tracking: u32,
    pub mouse_generation: i32,
}
#[repr(C)]
pub struct ScreenState {
    pub layout_epoch: i64,
    pub revision: i32,
    pub columns: i32,
    pub rows: i32,
    pub cursor_row: i32,
    pub cursor_column: i32,
    pub flags: u32,
    pub cursor_shape: u32,
    pub mouse_tracking: u32,
    pub mouse_generation: i32,
}
#[repr(C)]
pub struct ScreenRowView {
    pub id: i64,
    pub revision: i64,
    pub cells: *const CellView,
    pub arena: *const u16,
    pub formats: *const FormatView,
    pub cell_count: u32,
    pub format_count: u32,
    pub first: u32,
    pub end: u32,
}
#[repr(C)]
pub struct Effects {
    pub text: *const u8,
    pub text_length: u32,
    pub reserved0: u32,
    pub terminal: *const u8,
    pub terminal_length: u32,
    pub reply_length: u32,
    pub replies: *const u8,
    pub mouse_tracking: u32,
    pub mouse_generation: i32,
    pub layout_epoch: i64,
}
#[repr(C)]
pub struct Stats {
    pub feed_bytes: u64,
    pub feed_batches: u64,
    pub snapshot_exports: u64,
    pub unsupported: u64,
    pub owned_bytes: u64,
    pub peak_owned_bytes: u64,
    pub rows: u64,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct TabView {
    pub start: u32,
    pub end: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct FormatView {
    pub column: u32,
    pub scalar: u32,
}
#[repr(C)]
pub struct ExportRow {
    pub view: RowView,
    pub tabs: *const TabView,
    pub submitted: *const u16,
    pub content_revision: i64,
    pub logical_cluster_offset: i64,
    pub submitted_revision: i64,
    pub tab_count: u32,
    pub submitted_length: u32,
    pub formats: *const FormatView,
    pub format_count: u32,
    pub recorded: *const TabView,
    pub recorded_count: u32,
    pub excluded: u32,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct SourceView {
    pub pending: *const u8,
    pub parameters: *const u8,
    pub last_graphic: *const u32,
    pub pending_count: u32,
    pub parameter_count: u32,
    pub last_graphic_count: u32,
    pub flags: u32,
    pub escape: u32,
    pub reserved: u32,
    pub pending_interval: u32,
    pub escape_interval: u32,
    pub previous_interval: u32,
}
impl SourceView {
    const fn empty() -> Self {
        Self {
            pending: ptr::null(),
            parameters: ptr::null(),
            last_graphic: ptr::null(),
            pending_count: 0,
            parameter_count: 0,
            last_graphic_count: 0,
            flags: 0,
            escape: 0,
            reserved: 0,
            pending_interval: 0,
            escape_interval: 0,
            previous_interval: 0,
        }
    }
}
#[repr(C)]
pub struct ExportState {
    pub snapshot: SnapshotView,
    pub rows: *const ExportRow,
    pub sources: *const SourceView,
    pub cluster: *const u32,
    pub recorded_cluster: *const u32,
    pub cluster_row: i64,
    pub cluster_count: u32,
    pub cluster_column: u32,
    pub cluster_width: u32,
    pub cursor_row: u32,
    pub style_foreground: u32,
    pub style_background: u32,
    pub style_flags: u32,
    pub cluster_foreground: u32,
    pub cluster_background: u32,
    pub cluster_flags: u32,
    pub mode_flags: u32,
    pub recorded_column: u32,
    pub recorded_known: u32,
    pub recorded_count: u32,
    pub row_count: u32,
    pub source_count: u32,
    pub reserved0: u32,
    pub reserved1: u32,
    pub editing: *const EditingView,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct EditingView {
    pub margin_top: u32,
    pub margin_bottom: u32,
    pub position_row: i32,
    pub position_column: u32,
    pub saved_row: u32,
    pub saved_column: u32,
    pub saved_fg: u32,
    pub saved_bg: u32,
    pub saved_style: u32,
    pub saved_flags: u32,
    pub tab_stops: [u64; 16],
    pub parked: PageView,
    pub alternate: PageView,
    pub normal_return: CursorView,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct PageView {
    pub first: u32,
    pub count: u32,
    pub row: u32,
    pub column: u32,
    pub columns: u32,
    pub rows: u32,
    pub wrap: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct CursorView {
    pub row: u32,
    pub column: u32,
    pub fg: u32,
    pub bg: u32,
    pub style: u32,
    pub flags: u32,
    pub active: u32,
}
fn valid<T>(p: *const T) -> bool {
    !p.is_null() && (p as usize) % align_of::<T>() == 0
}
unsafe fn model<'a>(handle: *mut c_void) -> Result<&'a mut Model, i32> {
    if !valid(handle as *const Model) {
        Err(1)
    } else {
        Ok(&mut *(handle as *mut Model))
    }
}
fn run<F: FnOnce() -> Result<(), i32>>(f: F) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => 0,
        Ok(Err(e)) => e,
        Err(_) => 3,
    }
}
#[no_mangle]
pub extern "C" fn tc_abi_version() -> u32 {
    7
}

#[no_mangle]
pub unsafe extern "C" fn tc_encode_key(
    key: u32,
    modifiers: u32,
    modes: u32,
    output: *mut u8,
    written: *mut u32,
) -> i32 {
    if output.is_null() || !valid(written) {
        return 1;
    }
    run(|| {
        written.write(
            input::key(key, modifiers, modes, slice::from_raw_parts_mut(output, 32))? as u32,
        );
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tc_encode_mouse(
    input: *const input::MouseInput,
    tracking: u32,
    generation: i32,
    columns: i32,
    rows: i32,
    output: *mut u8,
    written: *mut u32,
) -> i32 {
    if !valid(input) || output.is_null() || !valid(written) {
        return 1;
    }
    run(|| {
        written.write(input::mouse(
            &*input,
            tracking,
            generation,
            columns,
            rows,
            slice::from_raw_parts_mut(output, 32),
        )? as u32);
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tc_encode_utf8(
    input: *const u16,
    length: u32,
    output: *mut u8,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    const MAX_BYTES: usize = 1024 * 1024;
    if !valid(required) || (length != 0 && !valid(input)) {
        return 1;
    }
    run(|| {
        if length as usize > MAX_BYTES {
            return Err(2);
        }
        let input = if length == 0 {
            &[]
        } else {
            slice::from_raw_parts(input, length as usize)
        };
        let mut total = 0usize;
        for scalar in char::decode_utf16(input.iter().copied()) {
            let scalar = scalar.map_err(|_| 1)?;
            total += scalar.len_utf8();
            if total > MAX_BYTES {
                return Err(2);
            }
        }
        required.write(total as u32);
        if output.is_null() {
            return Ok(());
        }
        if (capacity as usize) < total {
            return Err(2);
        }
        let destination = slice::from_raw_parts_mut(output, total);
        let mut offset = 0;
        for scalar in char::decode_utf16(input.iter().copied()) {
            let scalar = scalar.map_err(|_| 1)?;
            let end = offset + scalar.len_utf8();
            scalar.encode_utf8(&mut destination[offset..end]);
            offset = end;
        }
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tc_encode_legacy(
    page: u32,
    input: *const u16,
    length: u32,
    flags: u32,
    output: *mut u8,
    capacity: u32,
    required: *mut u32,
) -> i32 {
    if !valid(required) || (length != 0 && !valid(input)) {
        return 1;
    }
    run(|| {
        if length > 1024 * 1024 {
            return Err(2);
        }
        let input = if length == 0 {
            &[]
        } else {
            slice::from_raw_parts(input, length as usize)
        };
        let plan = legacy_encode::encode(page, input, flags, None)?;
        if !output.is_null() {
            if (capacity as usize) < plan {
                return Err(2);
            }
            let destination = slice::from_raw_parts_mut(output, plan);
            let applied = legacy_encode::encode(page, input, flags, Some(destination))?;
            if applied != plan {
                return Err(1);
            }
        }
        required.write(plan as u32);
        Ok(())
    })
}

#[cfg(windows)]
#[no_mangle]
pub unsafe extern "C" fn tc_legacy_open(page: u32, shift_kana: u32, out: *mut *mut c_void) -> i32 {
    if shift_kana > 1 || !valid(out) {
        return 1;
    }
    out.write(ptr::null_mut());
    run(|| {
        let receiver = legacy_receive::Receiver::new(page, shift_kana != 0)?;
        out.write(Box::into_raw(Box::new(receiver)) as *mut c_void);
        Ok(())
    })
}

#[cfg(windows)]
#[no_mangle]
pub unsafe extern "C" fn tc_legacy_step(
    handle: *mut c_void,
    byte: i32,
    in_vt: u32,
    interval: u32,
    out: *mut legacy_receive::Decoded,
) -> i32 {
    let receiver = handle as *mut legacy_receive::Receiver;
    if !valid(receiver) || !(-1..=255).contains(&byte) || in_vt > 1 || !valid(out) {
        return 1;
    }
    run(|| {
        out.write((*receiver).step(u8::try_from(byte).ok(), in_vt != 0, interval)?);
        Ok(())
    })
}

#[cfg(windows)]
#[no_mangle]
pub unsafe extern "C" fn tc_legacy_destroy(handle: *mut c_void) {
    if valid(handle as *const legacy_receive::Receiver) {
        let receiver = Box::from_raw(handle as *mut legacy_receive::Receiver);
        let _ = windows_icu::in_apartment(|| {
            drop(receiver);
            Ok(())
        });
    }
}
#[no_mangle]
pub extern "C" fn tc_live_bytes() -> u64 {
    LIVE_BYTES.load(Ordering::Relaxed)
}
#[no_mangle]
pub extern "C" fn tc_peak_bytes() -> u64 {
    PEAK_BYTES.load(Ordering::Relaxed)
}
#[no_mangle]
pub extern "C" fn tc_live_models() -> u64 {
    LIVE_MODELS.load(Ordering::Relaxed)
}
#[no_mangle]
pub unsafe extern "C" fn tc_create_required(
    columns: i32,
    rows: i32,
    retained: i32,
    ambiguous: i32,
    out: *mut u64,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    out.write(0);
    run(|| {
        if !(2..=1024).contains(&columns)
            || !(100..=100000).contains(&retained)
            || !(1..=512.min(retained)).contains(&rows)
            || !(1..=2).contains(&ambiguous)
        {
            return Err(1);
        }
        out.write(Model::creation_required(
            columns as usize,
            rows as usize,
            retained as usize,
        ));
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_create(
    columns: i32,
    rows: i32,
    retained: i32,
    ambiguous: i32,
    allowance: u64,
    out: *mut *mut c_void,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    out.write(ptr::null_mut());
    run(|| {
        if !(2..=1024).contains(&columns)
            || !(100..=100000).contains(&retained)
            || !(1..=512.min(retained)).contains(&rows)
            || !(1..=2).contains(&ambiguous)
        {
            return Err(1);
        }
        if Model::creation_required(columns as usize, rows as usize, retained as usize) > allowance
        {
            return Err(2);
        }
        let mut terminal = Box::new(Model::new(
            columns as usize,
            rows as usize,
            retained as usize,
            ambiguous as usize,
        ));
        terminal.set_limit(allowance)?;
        out.write(Box::into_raw(terminal) as *mut c_void);
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_destroy(handle: *mut c_void) {
    if !handle.is_null() {
        drop(Box::from_raw(handle as *mut Model));
    }
}
#[no_mangle]
pub unsafe extern "C" fn tc_set_limit(handle: *mut c_void, allowance: u64) -> i32 {
    run(|| model(handle)?.set_limit(allowance))
}
#[no_mangle]
pub unsafe extern "C" fn tc_preflight(
    handle: *mut c_void,
    operation: u32,
    source: u32,
    data: *const u8,
    length: u32,
    flags: u32,
    out: *mut u64,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    out.write(0);
    run(|| {
        if length != 0 && data.is_null() {
            return Err(1);
        }
        if length > 1024 * 1024 {
            return Err(2);
        }
        let payload = if length == 0 {
            &[]
        } else {
            slice::from_raw_parts(data, length as usize)
        };
        out.write(model(handle)?.preflight(operation, source as usize, payload, flags)?);
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_feed(
    handle: *mut c_void,
    source: u32,
    data: *const u8,
    length: u32,
    flags: u32,
    out: *mut Effects,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        if source > 2 || (length != 0 && data.is_null()) {
            return Err(1);
        }
        if length > 1024 * 1024 {
            return Err(2);
        }
        let bytes = if length == 0 {
            &[]
        } else {
            slice::from_raw_parts(data, length as usize)
        };
        let terminal = model(handle)?;
        terminal.admit(0, source as usize, bytes, flags)?;
        terminal.feed(source as usize, bytes, flags)?;
        out.write(terminal.effects());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_complete(handle: *mut c_void, flags: u32, out: *mut Effects) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        let t = model(handle)?;
        t.admit(1, 0, &[], flags)?;
        t.complete(flags)?;
        out.write(t.effects());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_flush_text(handle: *mut c_void, flags: u32, out: *mut Effects) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        let t = model(handle)?;
        t.admit(2, 0, &[], flags)?;
        t.flush(flags)?;
        out.write(t.effects());
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tc_begin_recording(handle: *mut c_void, flags: u32, interval: u32) -> i32 {
    run(|| model(handle)?.begin_recording(flags, interval))
}

#[no_mangle]
pub unsafe extern "C" fn tc_cutoff_recording(
    handle: *mut c_void,
    flags: u32,
    interval: u32,
    abort: u32,
    out: *mut Effects,
) -> i32 {
    if abort > 1 || !valid(out) {
        return 1;
    }
    run(|| {
        let terminal = model(handle)?;
        terminal.cutoff_recording(flags, interval, abort != 0)?;
        out.write(terminal.effects());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_resize_required(
    handle: *mut c_void,
    columns: i32,
    rows: i32,
    out: *mut u64,
) -> i32 {
    if !valid(out) || !(2..=1024).contains(&columns) || !(1..=512).contains(&rows) {
        return 1;
    }
    run(|| {
        out.write(model(handle)?.resize_required(columns as usize, rows as usize));
        Ok(())
    })
}

#[no_mangle]
pub unsafe extern "C" fn tc_resize_prepare(
    handle: *mut c_void,
    columns: i32,
    rows: i32,
    out: *mut *mut c_void,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    out.write(ptr::null_mut());
    run(|| {
        if !(2..=1024).contains(&columns) || !(1..=512).contains(&rows) {
            return Err(1);
        }
        let prepared = model(handle)?.prepare_resize(columns as usize, rows as usize)?;
        out.write(Box::into_raw(Box::new(Some(prepared))) as *mut c_void);
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_resize_apply(
    handle: *mut c_void,
    resize: *mut c_void,
    out: *mut Effects,
) -> i32 {
    let plan = resize as *mut Option<model::PreparedResize>;
    if !valid(out) || !valid(plan) {
        return 1;
    }
    run(|| {
        let t = model(handle)?;
        t.apply_resize((*plan).take().ok_or(1)?)?;
        out.write(t.effects());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_resize_drop(resize: *mut c_void) {
    let plan = resize as *mut Option<model::PreparedResize>;
    if valid(plan) {
        drop(Box::from_raw(plan));
    }
}
#[no_mangle]
pub unsafe extern "C" fn tc_screen_state(handle: *mut c_void, out: *mut ScreenState) -> i32 {
    if !valid(out) {
        return 1;
    }
    run(|| {
        out.write(model(handle)?.screen_state());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_screen_row(
    handle: *mut c_void,
    row: u32,
    first: u32,
    end: u32,
    out: *mut ScreenRowView,
) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        if let Some(view) = model(handle)?.screen_row(row, first, end)? {
            out.write(view);
        }
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_snapshot_copy_required(handle: *mut c_void, out: *mut u64) -> i32 {
    if !valid(out) {
        return 1;
    }
    run(|| {
        out.write(model(handle)?.snapshot_copy_required());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_snapshot(handle: *mut c_void, out: *mut SnapshotView) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        let t = model(handle)?;
        t.admit(3, 0, &[], 0)?;
        out.write(t.snapshot()?);
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_export(handle: *mut c_void, out: *mut ExportState) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        let t = model(handle)?;
        t.admit(4, 0, &[], 0)?;
        out.write(t.export()?);
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_stats(handle: *mut c_void, out: *mut Stats) -> i32 {
    if !valid(out) {
        return 1;
    }
    ptr::write_bytes(out, 0, 1);
    run(|| {
        out.write(model(handle)?.stats());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn tc_owned_bytes(handle: *mut c_void) -> u64 {
    if !valid(handle as *const Model) {
        0
    } else {
        (&*(handle as *const Model)).owned_bytes()
    }
}
#[no_mangle]
pub unsafe extern "C" fn tc_quota_bytes(handle: *mut c_void) -> u64 {
    if !valid(handle as *const Model) {
        0
    } else {
        (&*(handle as *const Model)).quota_bytes()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::mem::size_of;
    fn effects(t: &Model) -> (Vec<u8>, Vec<u8>) {
        let e = t.effects();
        unsafe {
            (
                slice::from_raw_parts(e.text, e.text_length as usize).to_vec(),
                slice::from_raw_parts(e.terminal, e.terminal_length as usize).to_vec(),
            )
        }
    }
    fn row_text(t: &mut Model, index: usize) -> String {
        let v = t.snapshot().unwrap();
        unsafe {
            let r = &*v.rows.add(index);
            String::from_utf16(slice::from_raw_parts(r.text, r.text_length as usize)).unwrap()
        }
    }
    #[test]
    fn abi() {
        assert_eq!(size_of::<CellView>(), 20);
        assert_eq!(size_of::<RowView>(), 72);
        assert_eq!(size_of::<SnapshotView>(), 48);
        assert_eq!(size_of::<Effects>(), 56);
        assert_eq!(size_of::<ScreenState>(), 48);
        assert_eq!(size_of::<ScreenRowView>(), 56);
        assert_eq!(size_of::<Stats>(), 56);
    }
    #[test]
    fn overwrite_preserves_tail_and_logs_final_text() {
        let mut t = Model::new(120, 40, 5000, 1);
        t.feed(0, b"progress 10%\rprogress 100%\r\n", 3).unwrap();
        let (text, terminal) = effects(&t);
        assert_eq!(text, b"progress 100%\r\n");
        assert_eq!(terminal, b"progress 10%\rprogress 100%\r\n");
        assert_eq!(row_text(&mut t, 0), "progress 100%");
        t.feed(0, b"abcdef\rXY\n", 3).unwrap();
        assert_eq!(effects(&t).0, b"XYcdef\n");
    }
    #[test]
    fn tabs_sgr_unicode_and_fragmentation() {
        let bytes = "A\t界e\u{301}\u{1f600}\x1b[31m!\x1b[0m\r\n".as_bytes();
        let mut t = Model::new(120, 40, 5000, 1);
        let mut text = Vec::new();
        let mut operations = Vec::new();
        for byte in bytes {
            t.feed(0, &[*byte], 3).unwrap();
            let (a, b) = effects(&t);
            text.extend(a);
            operations.extend(b);
        }
        assert_eq!(
            String::from_utf8(text).unwrap(),
            "A\t界e\u{301}\u{1f600}!\r\n"
        );
        assert_eq!(operations, bytes);
        assert_eq!(row_text(&mut t, 0), "A       界e\u{301}\u{1f600}!");
    }
    #[test]
    fn independent_decoders_and_truncated_tail() {
        let mut t = Model::new(120, 40, 5000, 1);
        t.feed(0, &[0xe7], 1).unwrap();
        assert!(effects(&t).1.is_empty());
        t.feed(1, b"x", 1).unwrap();
        assert_eq!(effects(&t).1, b"x");
        t.feed(0, &[0x95, 0x8c], 1).unwrap();
        assert_eq!(effects(&t).1, "界".as_bytes());
        t.feed(0, &[0xe2, 0x82], 1).unwrap();
        t.complete(1).unwrap();
        assert_eq!(effects(&t).1, "\u{fffd}".as_bytes());
    }
    #[test]
    fn interleaved_repeat_control_strings_and_cursor_style() {
        let mut terminal = Model::new(24, 4, 100, 1);
        terminal.feed(0, "e\u{301}".as_bytes(), 3).unwrap();
        terminal.feed(1, "界".as_bytes(), 3).unwrap();
        let operation = b"\x1b[2b\x1bPdiscard\x07also discard\x1b\\\x1b[\x076 q\x07";
        terminal.preflight(0, 0, operation, 3).unwrap();
        terminal.feed(0, operation, 3).unwrap();
        assert_eq!(effects(&terminal).1, "e\u{301}e\u{301}\x1b[6 q".as_bytes());
        assert_eq!(terminal.effects().reserved0 >> 8, 2);
        let snapshot = terminal.snapshot().unwrap();
        assert_eq!(snapshot.cursor_shape, 2);
        assert_eq!(snapshot.flags & 2, 0);
        assert_eq!(row_text(&mut terminal, 0), "e\u{301}界e\u{301}e\u{301}");
        terminal.feed(1, b"\x1b[1b", 3).unwrap();
        assert_eq!(row_text(&mut terminal, 0), "e\u{301}界e\u{301}e\u{301}界");
    }

    #[test]
    fn retention_wrap_and_repeat_flush() {
        let mut t = Model::new(8, 4, 100, 1);
        for _ in 0..160 {
            t.feed(0, b"abcdefghijkl\r\n", 2).unwrap();
        }
        let snap = t.snapshot().unwrap();
        assert_eq!(snap.row_count, 100);
        assert!(snap.screen_start > 0);
        t.flush(2).unwrap();
        t.flush(2).unwrap();
        assert!(effects(&t).0.is_empty());
    }
    #[test]
    fn alternate_screen_keeps_normal_output_and_records_its_own_text() {
        let mut t = Model::new(120, 40, 5000, 1);
        let input = b"normal\x1b[?1049hALT\r\n\x1b[?1049h!\x1b[?1049l";
        t.preflight(0, 0, input, 3).unwrap();
        t.feed(0, input, 3).unwrap();
        assert_eq!(effects(&t).0, b"normalALT\r\n!");
        assert_eq!(row_text(&mut t, 0), "normal");
        t.feed(0, b"\x1b[?47h", 3).unwrap();
        assert_eq!(t.snapshot().unwrap().flags & 4, 4);
        assert_eq!(row_text(&mut t, 0), "ALT");
        t.feed(0, b"\x1b[?1047l\x1b[?47h", 3).unwrap();
        assert_eq!(t.snapshot().unwrap().row_count, 0);
        t.resize(100, 40).unwrap();
        t.feed(0, b"\x1b[?47l", 3).unwrap();
        assert_eq!(row_text(&mut t, 0), "normal");
    }
    #[test]
    fn disabling_autowrap_cancels_pending_wrap() {
        let mut t = Model::new(4, 4, 100, 1);
        t.feed(0, b"abcd\x1b[?7lX", 3).unwrap();
        assert_eq!(row_text(&mut t, 0), "abcX");
        assert_eq!(t.snapshot().unwrap().row_count, 1);
    }
    #[test]
    fn export_preserves_empty_screen_and_all_abi_layouts() {
        assert_eq!(size_of::<TabView>(), 8);
        assert_eq!(size_of::<ExportRow>(), 152);
        assert_eq!(size_of::<SourceView>(), 64);
        assert_eq!(size_of::<ExportState>(), 168);
        assert_eq!(size_of::<EditingView>(), 256);
        let mut t = Model::new(16, 4, 100, 1);
        let e = t.export().unwrap();
        assert_eq!(e.snapshot.row_count, 0);
        assert_eq!(e.row_count, 1);
        assert_eq!(e.source_count, 3);
        unsafe {
            assert_eq!((*e.rows).submitted_length, u32::MAX);
        }
    }
    #[test]
    fn export_preserves_stale_submitted_text_and_partial_sources() {
        let mut t = Model::new(16, 4, 100, 1);
        t.feed(0, b"A\r\n\x1b[1;1HB", 3).unwrap();
        t.feed(0, &[0xe7], 3).unwrap();
        t.feed(1, b"\x1b[3", 3).unwrap();
        let e = t.export().unwrap();
        unsafe {
            let row = &*e.rows;
            assert_eq!(
                String::from_utf16(slice::from_raw_parts(
                    row.view.text,
                    row.view.text_length as usize
                ))
                .unwrap(),
                "B"
            );
            assert_eq!(
                slice::from_raw_parts(row.submitted, row.submitted_length as usize),
                &[65]
            );
            assert_ne!(row.content_revision, row.submitted_revision);
            let data = &*e.sources;
            assert_eq!(data.pending_count, 1);
            assert_eq!(*data.pending, 0xe7);
            let stderr = &*e.sources.add(1);
            assert_eq!(stderr.escape, 2);
            assert_eq!(
                slice::from_raw_parts(stderr.parameters, stderr.parameter_count as usize),
                b"3"
            );
            assert_eq!(
                slice::from_raw_parts(data.last_graphic, data.last_graphic_count as usize),
                &[66]
            );
        }
    }
    #[test]
    fn unsupported_preflight_and_admission_leave_native_state_unchanged() {
        let mut t = Model::new(16, 4, 100, 1);
        t.feed(0, b"ABC", 3).unwrap();
        let before = t.stats();
        assert_eq!(
            t.preflight(0, 0, b"accepted-prefix\x1b[6n", 3 | 128 << 8),
            Err(4)
        );
        assert_eq!(t.stats().feed_bytes, before.feed_bytes);
        assert_eq!(row_text(&mut t, 0), "ABC");
        let required = t.preflight(0, 0, b"D", 3).unwrap();
        t.set_limit(t.quota_bytes()).unwrap();
        assert_eq!(t.admit(0, 0, b"D", 3), Err(2));
        assert_eq!(row_text(&mut t, 0), "ABC");
        t.set_limit(required.max(t.quota_bytes())).unwrap();
        t.admit(0, 0, b"D", 3).unwrap();
        t.feed(0, b"D", 3).unwrap();
        assert_eq!(row_text(&mut t, 0), "ABCD");
    }
    #[test]
    fn preflight_rejects_oversized_csi_before_any_prefix_mutation() {
        let mut t = Model::new(16, 4, 100, 1);
        t.feed(0, b"before", 1).unwrap();
        let mut input = b"after\x1b[".to_vec();
        input.extend_from_slice(&vec![b';'; 40]);
        input.push(b'm');
        assert_eq!(t.preflight(0, 0, &input, 1), Err(2));
        assert_eq!(row_text(&mut t, 0), "before");
    }
    #[test]
    fn preflight_rejects_cluster_overflow_without_losing_retained_output() {
        let mut t = Model::new(16, 4, 100, 1);
        let cluster = format!("a{}", "\u{301}".repeat(255));
        t.feed(0, cluster.as_bytes(), 3).unwrap();
        let before = t.stats().feed_bytes;
        for source in 0..2 {
            assert_eq!(t.preflight(0, source, "\u{301}".as_bytes(), 3), Err(2));
            assert_eq!(t.stats().feed_bytes, before);
            assert_eq!(row_text(&mut t, 0), cluster);
        }
        let e = t.export().unwrap();
        assert_eq!(e.cluster_count, 256);
        unsafe {
            assert_eq!((*e.sources).last_graphic_count, 256);
        }
    }
    #[test]
    fn preflight_rejects_fragmented_escape_overflow_without_consuming_it() {
        let mut t = Model::new(16, 4, 100, 1);
        t.feed(0, b"before\x1b[", 1).unwrap();
        t.feed(0, &vec![b'0'; 4096], 1).unwrap();
        assert_eq!(t.preflight(0, 0, b"0m", 1), Err(2));
        assert_eq!(row_text(&mut t, 0), "before");
        let e = t.export().unwrap();
        unsafe {
            assert_eq!((*e.sources).parameter_count, 4096);
        }
    }
    #[test]
    fn oversized_text_effect_hands_off_before_lf_or_flush() {
        let mut t = Model::new(120, 40, 5000, 1);
        let payload = [b'x'; 1024];
        for _ in 0..128 {
            t.feed(0, &payload, 2).unwrap();
        }
        let before = t.stats().feed_bytes;
        assert_eq!(t.preflight(0, 0, b"\n", 2), Err(2));
        assert_eq!(t.preflight(2, 0, &[], 2), Err(2));
        assert_eq!(t.stats().feed_bytes, before);
        assert!(t.snapshot().unwrap().row_count > 1000);
        assert!(t.export().unwrap().row_count > 1000);
    }
    #[test]
    fn submitted_tab_history_has_no_effect_and_revisit_keeps_exact_text() {
        let mut t = Model::new(120, 40, 5000, 1);
        for _ in 0..3000 {
            t.feed(0, b"A\tB\r\n", 2).unwrap();
        }
        t.preflight(0, 0, b"next\r\n", 2).unwrap();
        t.preflight(2, 0, &[], 2).unwrap();
        t.flush(2).unwrap();
        assert!(effects(&t).0.is_empty());

        let mut t = Model::new(32, 4, 100, 1);
        t.feed(0, b"A\tB\r\n", 2).unwrap();
        t.feed(0, b"ZZ", 2).unwrap();
        t.flush(2).unwrap();
        assert_eq!(effects(&t).0, b"ZZ");
        t.feed(0, b"\x1b[1;1H\n", 2).unwrap();
        assert!(effects(&t).0.is_empty());
        t.feed(0, b"\x1b[1;10H\t\r\n", 2).unwrap();
        assert_eq!(effects(&t).0, b"A       B       \r\n");
    }
    #[test]
    fn shell_title_and_bracketed_paste_remain_native_when_fragmented() {
        let mut t = Model::new(120, 40, 5000, 1);
        let input = "\x1b]0;作業中\x07\x1b[?2004huser$ echo ok\r\n";
        let mut text = Vec::new();
        let mut terminal = Vec::new();
        for byte in input.as_bytes() {
            t.preflight(0, 0, &[*byte], 3).unwrap();
            t.feed(0, &[*byte], 3).unwrap();
            let (a, b) = effects(&t);
            text.extend(a);
            terminal.extend(b);
        }
        assert_eq!(text, b"user$ echo ok\r\n");
        assert_eq!(terminal, b"\x1b[?2004huser$ echo ok\r\n");
        assert_eq!(t.effects().reserved0 & 16, 16);
        assert_eq!(t.export().unwrap().mode_flags & 16, 16);
        t.feed(0, b"\x1b[?2004l", 3).unwrap();
        assert_eq!(t.effects().reserved0 & 16, 0);
    }
    #[test]
    fn osc_escape_reprocesses_csi_and_exports_partial_count() {
        let mut t = Model::new(32, 4, 100, 1);
        let input = "A\x1b]0;界\r\n\x1b[31mB";
        t.preflight(0, 0, input.as_bytes(), 3).unwrap();
        t.feed(0, input.as_bytes(), 3).unwrap();
        assert_eq!(effects(&t).1, b"A\x1b[31mB");
        assert_eq!(row_text(&mut t, 0), "AB");
        t.feed(0, "\x1b]0;界\x1b".as_bytes(), 3).unwrap();
        let e = t.export().unwrap();
        unsafe {
            assert_eq!((*e.sources).escape, 4);
            assert_eq!((*e.sources).flags & 8, 8);
            assert_eq!((*e.sources).reserved, 5);
        }
        assert_eq!(t.preflight(0, 0, b"[6n", 3 | 128 << 8), Err(4));
        t.feed(0, b"\\C\r\n", 3).unwrap();
        assert_eq!(effects(&t).1, b"C\r\n");
        assert_eq!(effects(&t).0, b"ABC\r\n");
    }
    #[test]
    fn osc_size_limit_hands_off_without_consuming_overflow() {
        let mut t = Model::new(32, 4, 100, 1);
        t.feed(0, b"before\x1b]", 0).unwrap();
        t.feed(0, &vec![b'x'; 1024 * 1024], 0).unwrap();
        assert_eq!(t.preflight(0, 0, b"x", 0), Err(2));
        assert_eq!(row_text(&mut t, 0), "before");
        let e = t.export().unwrap();
        unsafe {
            assert_eq!((*e.sources).escape, 3);
            assert_eq!((*e.sources).reserved, 1024 * 1024);
        }
        t.preflight(0, 0, b"\x18after", 0).unwrap();
        t.feed(0, b"\x18after", 0).unwrap();
        assert_eq!(row_text(&mut t, 0), "beforeafter");
    }
    #[test]
    fn export_retains_logical_cluster_prefix_after_retirement() {
        let mut t = Model::new(4, 1, 100, 1);
        let text = "e\u{301}ABC".repeat(130);
        t.feed(0, text.as_bytes(), 0).unwrap();
        let e = t.export().unwrap();
        unsafe {
            let first = &*e.rows;
            assert!(first.view.logical_offset > 0);
            assert!(first.logical_cluster_offset > 0);
            assert!(first.logical_cluster_offset < first.view.logical_offset);
        }
    }

    #[test]
    fn independent_models_have_disjoint_negative_row_ids() {
        let mut first = Model::new(16, 4, 100, 1);
        let mut second = Model::new(16, 4, 100, 1);
        first.feed(0, b"ABC", 0).unwrap();
        second.feed(0, b"ABC", 0).unwrap();
        let a = first.snapshot().unwrap();
        let b = second.snapshot().unwrap();
        unsafe {
            assert!((*a.rows).id < 0);
            assert!((*b.rows).id < 0);
            assert_ne!((*a.rows).id, (*b.rows).id);
            assert_ne!((*a.rows).logical_id, (*b.rows).logical_id);
        }
    }
    #[test]
    fn text_log_position_wraps_when_vs15_shrinks_a_cluster() {
        let mut t = Model::new(16, 4, 100, 1);
        let text = "AAAAAA\u{2764}\u{fe0f}\u{fe0e}\tZ\r\n";
        t.feed(0, text.as_bytes(), 3).unwrap();
        assert_eq!(effects(&t).0, text.as_bytes());
        assert_eq!(effects(&t).1, text.as_bytes());
    }
    #[test]
    fn scrolled_blank_keeps_erase_background_and_late_tab_is_recorded() {
        let mut t = Model::new(8, 2, 100, 1);
        t.feed(0, b"\x1b[44ma\r\nb\r\n", 3).unwrap();
        let snap = t.snapshot().unwrap();
        unsafe {
            let row = &*snap.rows.add(snap.row_count as usize - 1);
            assert_eq!(row.cell_count, 8);
            assert_eq!((*row.cells).background, 0x010000ee);
            assert_eq!((*row.cells).width, 0);
        }
        let mut t = Model::new(16, 4, 100, 1);
        t.feed(0, b"Q\r\n", 3).unwrap();
        t.feed(0, b"\x1b[1;2H\t\r\n", 3).unwrap();
        assert_eq!(effects(&t).0, b"Q\t\r\n");
    }
}
