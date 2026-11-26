//! Windows x64 streaming ABI. The caller owns the source handle and the pinned buffer.
use crc32fast::Hasher;
use std::ffi::c_void;
use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom};
use std::os::windows::io::{FromRawHandle, RawHandle};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;

const OK: i32 = 0;
const STOP: i32 = 1;
const INVALID: i32 = -1;
const IO_ERROR: i32 = -2;
const ABORT: i32 = -3;
const PANIC: i32 = -4;

type OnChunk = extern "C" fn(*mut c_void, *const u8, usize) -> i32;

#[link(name = "kernel32")]
extern "system" {
    fn ReOpenFile(original: RawHandle, access: u32, share: u32, flags: u32) -> RawHandle;
}

fn reopen(original: RawHandle) -> Result<File, i32> {
    const GENERIC_READ: u32 = 0x8000_0000;
    const FILE_SHARE_READ: u32 = 1;
    const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
    // The session holds the original open. Reopening the same file object gives
    // each transfer an independent cursor even if a directory changes its name.
    let handle = unsafe { ReOpenFile(original, GENERIC_READ, FILE_SHARE_READ, FILE_FLAG_SEQUENTIAL_SCAN) };
    if handle as isize == -1 {
        return Err(IO_ERROR);
    }
    // ReOpenFile transferred ownership of this new handle to us.
    Ok(unsafe { File::from_raw_handle(handle) })
}

struct Buffer {
    address: *mut u8,
    capacity: usize,
}

impl Buffer {
    /// The caller guarantees exclusive, initialized, pinned storage for each
    /// individual read. The sink may access the same storage between reads.
    unsafe fn transfer<R: Read + Seek>(
        &self,
        mut source: R,
        offset: u64,
        length: u64,
        checksum: bool,
        mut deliver: impl FnMut(*const u8, usize) -> i32,
    ) -> Result<Option<u32>, i32> {
        offset.checked_add(length).ok_or(INVALID)?;
        source.seek(SeekFrom::Start(offset)).map_err(|_| IO_ERROR)?;
        let mut hasher = checksum.then(Hasher::new);
        let mut remaining = length;
        while remaining > 0 {
            let requested = remaining.min(self.capacity as u64) as usize;
            let count = {
                // End the Rust borrow before the managed sink accesses its buffer.
                let bytes = unsafe { slice::from_raw_parts_mut(self.address, requested) };
                match source.read(bytes) {
                    Ok(0) => return Err(IO_ERROR),
                    Ok(n) => {
                        if let Some(h) = hasher.as_mut() {
                            h.update(&bytes[..n]);
                        }
                        n
                    }
                    Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                    Err(_) => return Err(IO_ERROR),
                }
            };
            match deliver(self.address, count) {
                OK => {}
                STOP => return Err(STOP),
                _ => return Err(ABORT),
            }
            remaining -= count as u64;
        }
        Ok(hasher.map(Hasher::finalize))
    }
}

#[no_mangle]
pub extern "C" fn blink_core_version() -> u32 {
    2
}

/// Stream an exact byte range using caller-owned storage. A callback may
/// stop the transfer (1) or report a sink failure (other nonzero value).
/// The checksum is published only after the entire range was delivered.
///
/// # Safety
/// `original` must remain a live readable Windows file handle. `buffer` must
/// point to `capacity` initialized, writable bytes, pinned for this call and
/// accessible to the caller only during its synchronous callback. The callback
/// must not unwind or retain a reference after returning. If CRC is requested,
/// `out_crc` must point to an aligned writable u32 outside the buffer.
#[no_mangle]
pub unsafe extern "C" fn blink_stream(
    original: RawHandle,
    offset: u64,
    length: u64,
    compute_crc: u32,
    buffer: *mut u8,
    capacity: usize,
    on_chunk: Option<OnChunk>,
    context: *mut c_void,
    out_crc: *mut u32,
) -> i32 {
    let Some(callback) = on_chunk else { return INVALID };
    if original.is_null() || original as isize == -1 || buffer.is_null()
        || capacity == 0 || capacity > isize::MAX as usize || compute_crc > 1
        || (compute_crc == 1 && out_crc.is_null()) || offset.checked_add(length).is_none()
    {
        return INVALID;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        let source = reopen(original)?;
        let storage = Buffer { address: buffer, capacity };
        // The caller guarantees that pinned storage is valid for the transfer.
        unsafe { storage.transfer(source, offset, length, compute_crc == 1, |ptr, n| callback(context, ptr, n)) }
    }));
    match result {
        Ok(Ok(Some(value))) => {
            unsafe { out_crc.write(value) };
            OK
        }
        Ok(Ok(None)) => OK,
        Ok(Err(status)) => status,
        Err(_) => PANIC,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;
    use std::os::windows::io::AsRawHandle;

    fn transfer(
        input: &[u8], offset: u64, len: u64, crc: bool,
        mut sink: impl FnMut(&mut [u8]) -> i32,
    ) -> Result<Option<u32>, i32> {
        let mut block = [0u8; 1024];
        let storage = Buffer { address: block.as_mut_ptr(), capacity: block.len() };
        unsafe {
            storage.transfer(Cursor::new(input), offset, len, crc, |_, size| {
                sink(slice::from_raw_parts_mut(storage.address, size))
            })
        }
    }

    #[test]
    fn exact_range_and_empty_crc() {
        let source = b"x123456789y";
        let mut out = Vec::new();
        let crc = transfer(source, 1, 9, true, |chunk| {
            out.extend_from_slice(chunk);
            chunk.fill(0); // Sink mutation must not affect the source checksum.
            OK
        });
        assert_eq!(out, b"123456789");
        assert_eq!(crc, Ok(Some(0xcbf43926)));
        assert_eq!(transfer(source, 11, 0, true, |_| panic!()), Ok(Some(0)));
    }

    #[test]
    fn multi_block_checksum_and_disabled_checksum() {
        let source: Vec<_> = (0..8209).map(|i| (i % 251) as u8).collect();
        let range = &source[11..source.len() - 7];
        for enabled in [true, false] {
            let mut out = Vec::new();
            let crc = transfer(&source, 11, range.len() as u64, enabled, |chunk| {
                assert!(chunk.len() <= 1024);
                out.extend_from_slice(chunk);
                OK
            });
            assert_eq!(out, range);
            assert_eq!(crc, Ok(enabled.then(|| crc32fast::hash(range))));
        }
    }

    #[test]
    fn revocation_and_destination_error_stop_immediately() {
        let source = [5u8; 3072];
        for (value, expected) in [(1, STOP), (-1, ABORT)] {
            let mut calls = 0;
            let result = transfer(&source, 0, source.len() as u64, true, |_| {
                calls += 1;
                value
            });
            assert_eq!(result, Err(expected));
            assert_eq!(calls, 1);
        }
    }

    #[test]
    fn short_source_and_overflow_are_errors() {
        assert_eq!(transfer(&[1, 2], 0, 3, true, |_| OK), Err(IO_ERROR));
        assert_eq!(transfer(&[], u64::MAX, 1, true, |_| OK), Err(INVALID));
    }

    #[test]
    fn reopened_handle_preserves_original_cursor() {
        let mut original = File::open(std::env::current_exe().unwrap()).unwrap();
        original.seek(SeekFrom::Start(7)).unwrap();
        let mut second = reopen(original.as_raw_handle()).unwrap();
        let mut magic = [0; 2];
        second.read_exact(&mut magic).unwrap();
        assert_eq!(&magic, b"MZ");
        assert_eq!(original.stream_position().unwrap(), 7);
    }
}
