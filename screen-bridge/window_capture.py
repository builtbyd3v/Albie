"""macOS: capture one app's front window at full Retina resolution, even while other windows cover it.

CoreGraphics through ctypes (the same calls mss makes), so no new dependency. mss itself grabs screen
regions at nominal (non-Retina) resolution and includes whatever is on top.
"""

import ctypes
import ctypes.util
from ctypes import POINTER, Structure, byref, c_bool, c_char_p, c_double, c_int, c_long, c_size_t, c_uint32, c_void_p

from PIL import Image

cf = ctypes.cdll.LoadLibrary(ctypes.util.find_library("CoreFoundation"))
cg = ctypes.cdll.LoadLibrary(ctypes.util.find_library("CoreGraphics"))


class CGPoint(Structure):
    _fields_ = [("x", c_double), ("y", c_double)]


class CGSize(Structure):
    _fields_ = [("width", c_double), ("height", c_double)]


class CGRect(Structure):
    _fields_ = [("origin", CGPoint), ("size", CGSize)]


def _sig(lib, name, restype, *argtypes):
    fn = getattr(lib, name)
    fn.restype, fn.argtypes = restype, list(argtypes)


_sig(cg, "CGWindowListCopyWindowInfo", c_void_p, c_uint32, c_uint32)
_sig(cg, "CGWindowListCreateImage", c_void_p, CGRect, c_uint32, c_uint32, c_uint32)
_sig(cg, "CGRectMakeWithDictionaryRepresentation", c_bool, c_void_p, POINTER(CGRect))
_sig(cg, "CGImageGetWidth", c_size_t, c_void_p)
_sig(cg, "CGImageGetHeight", c_size_t, c_void_p)
_sig(cg, "CGImageGetBytesPerRow", c_size_t, c_void_p)
_sig(cg, "CGImageGetDataProvider", c_void_p, c_void_p)
_sig(cg, "CGDataProviderCopyData", c_void_p, c_void_p)
_sig(cf, "CFArrayGetCount", c_long, c_void_p)
_sig(cf, "CFArrayGetValueAtIndex", c_void_p, c_void_p, c_long)
_sig(cf, "CFDictionaryGetValue", c_void_p, c_void_p, c_void_p)
_sig(cf, "CFStringCreateWithCString", c_void_p, c_void_p, c_char_p, c_uint32)
_sig(cf, "CFStringGetCString", c_bool, c_void_p, c_char_p, c_long, c_uint32)
_sig(cf, "CFNumberGetValue", c_bool, c_void_p, c_int, c_void_p)
_sig(cf, "CFDataGetBytePtr", c_void_p, c_void_p)
_sig(cf, "CFDataGetLength", c_long, c_void_p)
_sig(cf, "CFRelease", None, c_void_p)

UTF8 = 0x08000100
ON_SCREEN_ONLY, EXCLUDE_DESKTOP, INCLUDING_WINDOW = 1, 16, 8
IGNORE_FRAMING, OPAQUE, BEST_RESOLUTION = 1, 2, 8
NULL_RECT = CGRect(CGPoint(float("inf"), float("inf")), CGSize(0, 0))  # CGRectNull: the window's own bounds


def _key(name):
    return cf.CFStringCreateWithCString(None, name.encode(), UTF8)


OWNER, NUMBER, LAYER, BOUNDS = map(_key, ["kCGWindowOwnerName", "kCGWindowNumber", "kCGWindowLayer", "kCGWindowBounds"])


def _str(ref):
    buf = ctypes.create_string_buffer(256)
    return buf.value.decode() if ref and cf.CFStringGetCString(ref, buf, 256, UTF8) else None


def _int(ref):
    value = c_long(0)
    cf.CFNumberGetValue(ref, 4, byref(value))  # kCFNumberSInt64Type
    return value.value


def find_window(app):
    """The id of `app`'s front-most normal window (skips small helper windows), or None."""
    windows = cg.CGWindowListCopyWindowInfo(ON_SCREEN_ONLY | EXCLUDE_DESKTOP, 0)
    try:
        for i in range(cf.CFArrayGetCount(windows)):  # front to back
            info = cf.CFArrayGetValueAtIndex(windows, i)
            if _str(cf.CFDictionaryGetValue(info, OWNER)) != app or _int(cf.CFDictionaryGetValue(info, LAYER)) != 0:
                continue
            rect = CGRect()
            cg.CGRectMakeWithDictionaryRepresentation(cf.CFDictionaryGetValue(info, BOUNDS), byref(rect))
            if rect.size.width >= 400 and rect.size.height >= 300:
                return _int(cf.CFDictionaryGetValue(info, NUMBER))
    finally:
        cf.CFRelease(windows)
    return None


def grab_window(window_id):
    """The window as a PIL image at backing (Retina) resolution, or None when it's gone."""
    image = cg.CGWindowListCreateImage(NULL_RECT, INCLUDING_WINDOW, window_id, IGNORE_FRAMING | OPAQUE | BEST_RESOLUTION)
    if not image:
        return None
    try:
        width, height = cg.CGImageGetWidth(image), cg.CGImageGetHeight(image)
        if width == 0 or height == 0:
            return None
        data = cg.CGDataProviderCopyData(cg.CGImageGetDataProvider(image))
        try:
            raw = ctypes.string_at(cf.CFDataGetBytePtr(data), cf.CFDataGetLength(data))
        finally:
            cf.CFRelease(data)
        return Image.frombuffer("RGB", (width, height), raw, "raw", "BGRX", cg.CGImageGetBytesPerRow(image), 1)
    finally:
        cf.CFRelease(image)
