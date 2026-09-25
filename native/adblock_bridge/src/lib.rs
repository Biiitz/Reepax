use std::ffi::{CStr, CString};
use std::os::raw::{c_char, c_int};
use std::panic::AssertUnwindSafe;
use std::ptr;
use adblock::engine::Engine;
use adblock::request::Request;

/// Opaque wrapper struct to protect Engine instances.
pub struct ReepaxAdblockEngine {
    engine: Engine,
}

#[no_mangle]
pub extern "C" fn adblock_create() -> *mut ReepaxAdblockEngine {
    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let engine = Engine::new_with_list_text("");
        Box::into_raw(Box::new(ReepaxAdblockEngine { engine }))
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn adblock_create_from_rules(rules_ptr: *const c_char) -> *mut ReepaxAdblockEngine {
    if rules_ptr.is_null() {
        return ptr::null_mut();
    }

    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let c_str = unsafe { CStr::from_ptr(rules_ptr) };
        let rules_str = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return ptr::null_mut(),
        };

        let engine = Engine::new_with_list_text(rules_str);
        Box::into_raw(Box::new(ReepaxAdblockEngine { engine }))
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn adblock_create_from_buffer(buf_ptr: *const u8, len: usize) -> *mut ReepaxAdblockEngine {
    if buf_ptr.is_null() || len == 0 {
        return ptr::null_mut();
    }

    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let slice = unsafe { std::slice::from_raw_parts(buf_ptr, len) };
        let mut engine = Engine::new_with_list_text("");
        if engine.deserialize(slice).is_ok() {
            Box::into_raw(Box::new(ReepaxAdblockEngine { engine }))
        } else {
            ptr::null_mut()
        }
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn adblock_serialize(engine_ptr: *mut ReepaxAdblockEngine, out_len: *mut usize) -> *mut u8 {
    if engine_ptr.is_null() || out_len.is_null() {
        return ptr::null_mut();
    }

    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let wrapper = unsafe { &*engine_ptr };
        let bytes = wrapper.engine.serialize();
        let len = bytes.len();
        unsafe { *out_len = len };

        let mut boxed_slice = bytes.into_boxed_slice();
        let ptr = boxed_slice.as_mut_ptr();
        std::mem::forget(boxed_slice);
        ptr
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn adblock_free_buffer(buf_ptr: *mut u8, len: usize) {
    if !buf_ptr.is_null() && len > 0 {
        let _ = std::panic::catch_unwind(AssertUnwindSafe(|| {
            unsafe {
                let _ = Box::from_raw(std::slice::from_raw_parts_mut(buf_ptr, len));
            }
        }));
    }
}

/// Checks network request URL against adblock engine rules.
/// Returns:
///   0: Not blocked (pass through)
///   1: Blocked
///   2: Redirect
#[no_mangle]
pub extern "C" fn adblock_check_network(
    engine_ptr: *mut ReepaxAdblockEngine,
    url_ptr: *const c_char,
    source_url_ptr: *const c_char,
    request_type_ptr: *const c_char,
) -> c_int {
    if engine_ptr.is_null() || url_ptr.is_null() {
        return 0;
    }

    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let wrapper = unsafe { &*engine_ptr };

        let url = match unsafe { CStr::from_ptr(url_ptr) }.to_str() {
            Ok(s) => s,
            Err(_) => return 0,
        };

        let source_url = if source_url_ptr.is_null() {
            ""
        } else {
            match unsafe { CStr::from_ptr(source_url_ptr) }.to_str() {
                Ok(s) => s,
                Err(_) => "",
            }
        };

        let request_type = if request_type_ptr.is_null() {
            "other"
        } else {
            match unsafe { CStr::from_ptr(request_type_ptr) }.to_str() {
                Ok(s) => s,
                Err(_) => "other",
            }
        };

        let req = match Request::new(url, source_url, request_type, "GET") {
            Ok(r) => r,
            Err(_) => return 0,
        };

        let res = wrapper.engine.check_network_request(&req);

        if res.exception.is_some() {
            return 0;
        }

        if res.redirect.is_some() {
            return 2;
        }

        if res.filter.is_some() {
            return 1;
        }

        0
    }))
    .unwrap_or(0)
}

/// Obtains cosmetic CSS hide rules and scriptlets for a specific URL.
/// Returns a JSON string: `{"css": "...", "script": "..."}` or null.
#[no_mangle]
pub extern "C" fn adblock_url_cosmetic_resources(
    engine_ptr: *mut ReepaxAdblockEngine,
    url_ptr: *const c_char,
) -> *mut c_char {
    if engine_ptr.is_null() || url_ptr.is_null() {
        return ptr::null_mut();
    }

    std::panic::catch_unwind(AssertUnwindSafe(|| {
        let wrapper = unsafe { &*engine_ptr };

        let url = match unsafe { CStr::from_ptr(url_ptr) }.to_str() {
            Ok(s) => s,
            Err(_) => return ptr::null_mut(),
        };

        let cosmetic = wrapper.engine.url_cosmetic_resources(url);

        let mut css = String::new();
        if !cosmetic.hide_selectors.is_empty() {
            let selectors: Vec<String> = cosmetic.hide_selectors.into_iter().collect();
            css = format!("{} {{ display: none !important; visibility: hidden !important; pointer-events: none !important; }}", selectors.join(", "));
        }

        let script = cosmetic.injected_script;

        let json = serde_json::json!({
            "css": css,
            "script": script,
        });

        match CString::new(json.to_string()) {
            Ok(c_str) => c_str.into_raw(),
            Err(_) => ptr::null_mut(),
        }
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn adblock_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(AssertUnwindSafe(|| {
            unsafe {
                let _ = CString::from_raw(ptr);
            }
        }));
    }
}

#[no_mangle]
pub extern "C" fn adblock_free(engine_ptr: *mut ReepaxAdblockEngine) {
    if !engine_ptr.is_null() {
        let _ = std::panic::catch_unwind(AssertUnwindSafe(|| {
            unsafe {
                let _ = Box::from_raw(engine_ptr);
            }
        }));
    }
}
