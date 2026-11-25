use std::ffi::c_void;
use std::ptr::{null, null_mut, NonNull};

pub(crate) const EUC_JP: &[u8] = b"euc-jp-2007\0";
pub(crate) const JIS_SHIFT_KANA: &[u8] = b"ISO_2022,locale=ja,version=3\0";
pub(crate) const JIS_EIGHT_BIT_KANA: &[u8] = b"ISO_2022,locale=ja,version=4\0";

pub(crate) fn iso_name(page: u32) -> Option<&'static [u8]> {
    match page {
        28600 => Some(b"iso-8859-10\0"),
        28601 => Some(b"iso-8859-11\0"),
        28604 => Some(b"iso-8859-14\0"),
        _ => None,
    }
}

pub(crate) struct Converter {
    handle: NonNull<c_void>,
}

type ToCallback = unsafe extern "C" fn(*const c_void, *mut c_void, *const u8, i32, i32, *mut i32);
type FromCallback =
    unsafe extern "C" fn(*const c_void, *mut c_void, *const u16, i32, i32, i32, *mut i32);

#[link(name = "icu")]
extern "C" {
    fn ucnv_open(name: *const u8, error: *mut i32) -> *mut c_void;
    fn ucnv_close(converter: *mut c_void);
    fn ucnv_safeClone(
        converter: *const c_void,
        buffer: *mut c_void,
        buffer_size: *mut i32,
        error: *mut i32,
    ) -> *mut c_void;
    fn ucnv_setToUCallBack(
        converter: *mut c_void,
        action: ToCallback,
        context: *const c_void,
        old_action: *mut ToCallback,
        old_context: *mut *const c_void,
        error: *mut i32,
    );
    fn ucnv_setFromUCallBack(
        converter: *mut c_void,
        action: FromCallback,
        context: *const c_void,
        old_action: *mut FromCallback,
        old_context: *mut *const c_void,
        error: *mut i32,
    );
    fn UCNV_TO_U_CALLBACK_STOP(
        context: *const c_void,
        args: *mut c_void,
        input: *const u8,
        count: i32,
        reason: i32,
        error: *mut i32,
    );
    fn UCNV_FROM_U_CALLBACK_STOP(
        context: *const c_void,
        args: *mut c_void,
        input: *const u16,
        count: i32,
        scalar: i32,
        reason: i32,
        error: *mut i32,
    );
    fn ucnv_setFallback(converter: *mut c_void, enabled: i8);
    fn ucnv_toUChars(
        converter: *mut c_void,
        output: *mut u16,
        capacity: i32,
        input: *const u8,
        length: i32,
        error: *mut i32,
    ) -> i32;
    fn ucnv_fromUChars(
        converter: *mut c_void,
        output: *mut u8,
        capacity: i32,
        input: *const u16,
        length: i32,
        error: *mut i32,
    ) -> i32;
    fn ucnv_fromUnicode(
        converter: *mut c_void,
        target: *mut *mut u8,
        target_limit: *const u8,
        source: *mut *const u16,
        source_limit: *const u16,
        offsets: *mut i32,
        flush: i8,
        error: *mut i32,
    );
    fn ucnv_toUnicode(
        converter: *mut c_void,
        target: *mut *mut u16,
        target_limit: *const u16,
        source: *mut *const u8,
        source_limit: *const u8,
        offsets: *mut i32,
        flush: i8,
        error: *mut i32,
    );
    fn ucnv_toUCountPending(converter: *mut c_void, error: *mut i32) -> i32;
}

#[link(name = "ole32")]
extern "system" {
    fn CoInitializeEx(reserved: *mut c_void, flags: u32) -> i32;
    fn CoUninitialize();
}

pub(crate) fn with_converter<T>(
    name: &'static [u8],
    operation: impl FnOnce(&mut Converter) -> Result<T, i32>,
) -> Result<T, i32> {
    in_apartment(|| operation(&mut Converter::open(name, true)?))
}

pub(crate) fn in_apartment<T>(operation: impl FnOnce() -> Result<T, i32>) -> Result<T, i32> {
    struct Apartment(bool);
    impl Drop for Apartment {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }
    // A WPF UI thread can already have a different apartment. In that case
    // it is ready to call ICU and we leave its COM ownership alone.
    let status = unsafe { CoInitializeEx(null_mut(), 0) };
    if status < 0 && status != 0x80010106u32 as i32 {
        return Err(3);
    }
    let _apartment = Apartment(status >= 0);
    operation()
}

impl Converter {
    pub(crate) fn clone_state(&self) -> Result<Self, i32> {
        let mut error = 0;
        let handle =
            unsafe { ucnv_safeClone(self.handle.as_ptr(), null_mut(), null_mut(), &mut error) };
        let cloned = Self {
            handle: NonNull::new(handle).ok_or(3)?,
        };
        if error > 0 {
            Err(3)
        } else {
            Ok(cloned)
        }
    }

    pub(crate) fn open(name: &'static [u8], strict_receive: bool) -> Result<Self, i32> {
        let mut error = 0;
        let handle = unsafe { ucnv_open(name.as_ptr(), &mut error) };
        let converter = Self {
            handle: NonNull::new(handle).ok_or(3)?,
        };
        if error > 0 {
            return Err(3);
        }
        error = 0;
        unsafe {
            ucnv_setFallback(handle, 0);
            if strict_receive {
                ucnv_setToUCallBack(
                    handle,
                    UCNV_TO_U_CALLBACK_STOP,
                    null(),
                    null_mut(),
                    null_mut(),
                    &mut error,
                );
            }
            ucnv_setFromUCallBack(
                handle,
                UCNV_FROM_U_CALLBACK_STOP,
                null(),
                null_mut(),
                null_mut(),
                &mut error,
            );
        }
        if error > 0 {
            Err(3)
        } else {
            Ok(converter)
        }
    }

    pub(crate) fn receive(
        &mut self,
        bytes: &[u8],
        output: &mut [u16],
        flush: bool,
    ) -> Result<(usize, bool), i32> {
        let mut error = 0;
        let mut source = bytes.as_ptr();
        let mut target = output.as_mut_ptr();
        unsafe {
            ucnv_toUnicode(
                self.handle.as_ptr(),
                &mut target,
                output.as_ptr().add(output.len()),
                &mut source,
                bytes.as_ptr().add(bytes.len()),
                null_mut(),
                flush as i8,
                &mut error,
            );
            if error > 0 || source != bytes.as_ptr().add(bytes.len()) {
                return Err(3);
            }
            let pending = ucnv_toUCountPending(self.handle.as_ptr(), &mut error);
            if error > 0 {
                return Err(3);
            }
            Ok((target.offset_from(output.as_ptr()) as usize, pending != 0))
        }
    }

    pub(crate) fn decode(&mut self, input: &[u8], output: &mut [u16]) -> Result<usize, i32> {
        let mut error = 0;
        let count = unsafe {
            ucnv_toUChars(
                self.handle.as_ptr(),
                output.as_mut_ptr(),
                output.len() as i32,
                input.as_ptr(),
                input.len() as i32,
                &mut error,
            )
        };
        if error > 0 {
            Err(1)
        } else {
            Ok(count as usize)
        }
    }

    pub(crate) fn encode(&mut self, input: &[u16], output: &mut [u8]) -> Result<usize, i32> {
        self.encode_to(input, output.as_mut_ptr(), output.len() as i32, false)
    }

    pub(crate) fn send(
        &mut self,
        input: &[u16],
        output: &mut [u8],
        flush: bool,
    ) -> Result<usize, i32> {
        let mut error = 0;
        let mut source = input.as_ptr();
        let mut target = output.as_mut_ptr();
        unsafe {
            let source_limit = input.as_ptr().add(input.len());
            ucnv_fromUnicode(
                self.handle.as_ptr(),
                &mut target,
                output.as_ptr().add(output.len()),
                &mut source,
                source_limit,
                null_mut(),
                flush as i8,
                &mut error,
            );
            if error > 0 || source != source_limit {
                return Err(3);
            }
            Ok(target.offset_from(output.as_ptr()) as usize)
        }
    }

    pub(crate) fn encoded_length(&mut self, input: &[u16]) -> Result<usize, i32> {
        self.encode_to(input, null_mut(), 0, true)
    }

    fn encode_to(
        &mut self,
        input: &[u16],
        output: *mut u8,
        capacity: i32,
        preflight: bool,
    ) -> Result<usize, i32> {
        let mut error = 0;
        let count = unsafe {
            ucnv_fromUChars(
                self.handle.as_ptr(),
                output,
                capacity,
                input.as_ptr(),
                input.len() as i32,
                &mut error,
            )
        };
        if error > 0 && !(preflight && error == 15) {
            Err(1)
        } else {
            Ok(count as usize)
        }
    }
}

impl Drop for Converter {
    fn drop(&mut self) {
        if in_apartment(|| {
            unsafe { ucnv_close(self.handle.as_ptr()) };
            Ok(())
        })
        .is_err()
        {
            // A failed apartment initialization must not leak the owned converter.
            unsafe { ucnv_close(self.handle.as_ptr()) };
        }
    }
}
