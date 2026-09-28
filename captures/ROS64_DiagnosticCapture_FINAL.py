#!/usr/bin/env python3
"""
ROS64 Diagnostic Capture FINAL
==============================

READ-ONLY. No WriteProcessMemory / no game-memory mutation.

This is the final diagnostic for the unresolved problem:

    BattleGroundAvatar (gameplay entity)
        -> actual rendered character / ModelSkeletal
        -> SpaceNode
        -> world XYZ
        -> camera projection

Design goal:
    Remove the failure modes of v1-v4/v5 instead of adding another camera
    scoring heuristic.

Known/reconciled constants used here:
    EM_STATIC_PTR             = 0x031F53D8 (RVA)
    EM_CONTROLLED_ENT         = 0x10F8
    ENT_OB_TYPE               = 0x08
    ENT_ENTITY_ID             = 0x18
    ENT_X/Y/Z                 = 0x1C / 0x20 / 0x24
    PY_INSTANCE_DICT          = 0x1B0 (verified on PBGA)
    PY_DICT_USED/MASK/TABLE   = 0x18 / 0x20 / 0x28
    PY_DICT_ENTRY_KEY/VALUE   = 0x08 / 0x10; stride 0x18
    PY_TYPE_NAME              = 0x18
    PY_STRING_VALUE           = 0x20
    CAMERA_VTABLE_RVA         = 0x01D84DC8
    MODEL_SKELETAL_VTABLE_RVA = 0x01C1ACE0
    SPACE_NODE_VTABLE_RVA     = 0x01C137C8
    MODEL +0x38               = SpaceNode
    SPACE +0x48/+0x4C/+0x50   = XYZ
    MODEL +0x30               = metadata pointer
    CAMERA basis/pos/focal    = offsets from current source

No automatic "enemy = model #N" conclusion is made from cursor distance.
The evidence hierarchy is:

    A. Exact pointer relationship in memory (strongest)
    B. Stable motion/translation relationship over time
    C. Spatial proximity / screen proximity (diagnostic only)

Motion test:
    For each BGA/model pair, compare synchronized samples.
    Let B_t be BGA XYZ and M_t be ModelSkeletal SpaceNode XYZ.
    Compute offset_t = M_t - B_t.
    If the model belongs to the entity, offset_t should remain stable while
    the entity moves. We therefore report:
        offset_rms
        velocity_rmse
        mean velocity cosine
        position R^2 after removing constant translation
        total BGA/model motion
    No arbitrary pass/fail threshold is used for identity. The numbers are
    evidence for the next analysis step.

Camera test:
    Every discovered camera candidate is recorded. A candidate is "structurally
    sane" only when all three basis vectors are finite and approximately unit,
    pairwise orthogonal, focal is finite/in-range, and position is finite.
    A camera is never selected merely because it projects many candidates.
    Camera fields are read as one contiguous block to avoid the v4 problem of
    mixing independently-read/torn fields.

F9:
    F9 is only a timestamp marker. Cursor position is recorded but is NOT used
    to choose an enemy or camera.
    The background tracker continuously polls known BGA/model/camera pointers,
    so each F9 includes a time history rather than a single stale snapshot.

Controls:
    F9  = save final synchronized capture
    F10 = quit

Usage:
    python ROS64_DiagnosticCapture_FINAL.py
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import csv
import math
import os
import struct
import sys
import threading
import time
from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

# ---------------------------------------------------------------------------
# Win32 constants
# ---------------------------------------------------------------------------

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TH32CS_SNAPPROCESS = 0x00000002

MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_READONLY = 0x02
PAGE_READWRITE = 0x04
PAGE_WRITECOPY = 0x08
PAGE_EXECUTE_READ = 0x20
PAGE_EXECUTE_READWRITE = 0x40
PAGE_EXECUTE_WRITECOPY = 0x80
PAGE_GUARD = 0x100

READABLE_PROTECTIONS = {
    PAGE_READONLY,
    PAGE_READWRITE,
    PAGE_WRITECOPY,
    PAGE_EXECUTE_READ,
    PAGE_EXECUTE_READWRITE,
    PAGE_EXECUTE_WRITECOPY,
}

VK_F9 = 0x78
VK_F10 = 0x79
PTR_MAX = 0x0000800000000000

# Scan limits deliberately match the diagnostic runs already performed.
CHUNK_SIZE = 16 * 1024 * 1024
MAX_SCAN_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_DICT_SLOTS = 0x10000
MAX_PY_STRING = 96

# ---------------------------------------------------------------------------
# Reconciled current-build constants
# ---------------------------------------------------------------------------

EM_STATIC_PTR = 0x031F53D8
EM_CONTROLLED_ENT = 0x10F8

ENT_OB_TYPE = 0x08
ENT_ENTITY_ID = 0x18
ENT_X = 0x1C
ENT_Y = 0x20
ENT_Z = 0x24

PY_TYPE_NAME = 0x18
PY_INSTANCE_DICT = 0x1B0
PY_DICT_USED = 0x18
PY_DICT_MASK = 0x20
PY_DICT_TABLE = 0x28
PY_DICT_ENTRY_KEY = 0x08
PY_DICT_ENTRY_VALUE = 0x10
PY_DICT_ENTRY_STRIDE = 0x18
PY_STRING_VALUE = 0x20

CONTROLLED_NODE_VALUE = 0x20
NODE_PARENT = 0x08

CAMERA_VTABLE_RVA = 0x01D84DC8
MODEL_SKELETAL_VTABLE_RVA = 0x01C1ACE0
SPACE_NODE_VTABLE_RVA = 0x01C137C8

MODEL_SPACENODE_PTR = 0x38
MODEL_META_PTR = 0x30

SN_X = 0x48
SN_Y = 0x4C
SN_Z = 0x50
SN_CACHE = 0xC8

CAM_FWD = 0x08
CAM_UP = 0x18
CAM_RIGHT = 0x28
CAM_POS = 0x38
CAM_YFOCAL = 0x9C
CAM_ORTHO = 0x348
CAM_XFOCAL = 0x360
CAM_BLOCK_END = 0x368

# Diagnostic-only alternative object-field observed in entity evidence.
# It is NOT treated as proven; it is tested and reported.
BGA_DICT_CANDIDATE_048 = 0x48

TRACE_INTERVAL_SEC = 0.20
RESCAN_INTERVAL_SEC = 30.0
HISTORY_SECONDS = 12.0
F9_HISTORY_SECONDS = 10.0

# ---------------------------------------------------------------------------
# Win32 declarations
# ---------------------------------------------------------------------------

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


class MODULEINFO(ctypes.Structure):
    _fields_ = [
        ("lpBaseOfDll", ctypes.c_void_p),
        ("SizeOfImage", wintypes.DWORD),
        ("EntryPoint", ctypes.c_void_p),
    ]


kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL
kernel32.ReadProcessMemory.argtypes = [
    wintypes.HANDLE,
    ctypes.c_void_p,
    ctypes.c_void_p,
    ctypes.c_size_t,
    ctypes.POINTER(ctypes.c_size_t),
]
kernel32.ReadProcessMemory.restype = wintypes.BOOL
kernel32.VirtualQueryEx.argtypes = [
    wintypes.HANDLE,
    ctypes.c_void_p,
    ctypes.POINTER(MEMORY_BASIC_INFORMATION),
    ctypes.c_size_t,
]
kernel32.VirtualQueryEx.restype = ctypes.c_size_t
kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.Process32FirstW.restype = wintypes.BOOL
kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
kernel32.Process32NextW.restype = wintypes.BOOL

psapi.EnumProcessModules.argtypes = [
    wintypes.HANDLE,
    ctypes.POINTER(wintypes.HMODULE),
    wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD),
]
psapi.EnumProcessModules.restype = wintypes.BOOL
psapi.GetModuleInformation.argtypes = [
    wintypes.HANDLE,
    wintypes.HMODULE,
    ctypes.POINTER(MODULEINFO),
    wintypes.DWORD,
]
psapi.GetModuleInformation.restype = wintypes.BOOL

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [EnumWindowsProc, wintypes.LPARAM]
user32.EnumWindows.restype = wintypes.BOOL
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetClientRect.restype = wintypes.BOOL
user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user32.ClientToScreen.restype = wintypes.BOOL
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.GetCursorPos.restype = wintypes.BOOL
user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
user32.GetAsyncKeyState.restype = wintypes.SHORT

# ---------------------------------------------------------------------------
# Data types
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Vec3:
    x: float
    y: float
    z: float

    def as_tuple(self):
        return (self.x, self.y, self.z)


@dataclass
class CameraSnapshot:
    ptr: int
    pos: Vec3
    fwd: Vec3
    up: Vec3
    right: Vec3
    y_focal: float
    x_focal: float
    ortho_f32: float | None
    ortho_u32: int | None
    basis_len_err: float
    basis_ortho_err: float
    handedness: float
    sane: bool
    reason: str


@dataclass
class BgaSnapshot:
    ptr: int
    entity_id: int
    ob_type: int
    xyz: Vec3
    dict_048: int
    dict_1b0: int
    aoi_slot: int


@dataclass
class ModelSnapshot:
    ptr: int
    meta_ptr: int
    space_ptr: int
    xyz: Vec3
    cache_xyz: Vec3 | None


@dataclass
class CameraEval:
    camera_ptr: int
    local_distance: float | None
    local_screen_x: float | None
    local_screen_y: float | None
    local_depth: float | None
    bga_projected: int
    bga_onscreen: int
    model_projected: int
    model_onscreen: int
    center_nearest_bga_px: float | None
    center_nearest_model_px: float | None


@dataclass
class TraceSample:
    t: float
    wall_iso: str
    bga: dict[int, Vec3]
    models: dict[int, Vec3]
    cameras: dict[int, CameraSnapshot]
    local_player_ptr: int
    local_player_xyz: Vec3 | None


# ---------------------------------------------------------------------------
# Numeric helpers
# ---------------------------------------------------------------------------


def finite(*values) -> bool:
    return all(math.isfinite(float(v)) for v in values)


def plausible_ptr(p: int | None) -> bool:
    return p is not None and 0x10000 <= int(p) < PTR_MAX and (int(p) & 7) == 0


def vec_len(v: Vec3) -> float:
    return math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z)


def vec_sub(a: Vec3, b: Vec3) -> Vec3:
    return Vec3(a.x - b.x, a.y - b.y, a.z - b.z)


def vec_add(a: Vec3, b: Vec3) -> Vec3:
    return Vec3(a.x + b.x, a.y + b.y, a.z + b.z)


def vec_scale(a: Vec3, s: float) -> Vec3:
    return Vec3(a.x * s, a.y * s, a.z * s)


def vec_dot(a: Vec3, b: Vec3) -> float:
    return a.x * b.x + a.y * b.y + a.z * b.z


def vec_cross(a: Vec3, b: Vec3) -> Vec3:
    return Vec3(
        a.y * b.z - a.z * b.y,
        a.z * b.x - a.x * b.z,
        a.x * b.y - a.y * b.x,
    )


def euclid(a: Vec3, b: Vec3) -> float:
    return vec_len(vec_sub(a, b))


def hexp(v: int | None) -> str:
    return f"0x{int(v or 0):016X}"


def fmt_vec(v: Vec3 | None) -> str:
    if v is None:
        return "NONE"
    return f"({v.x:.3f},{v.y:.3f},{v.z:.3f})"


def fmt_num(v: float | None, digits: int = 6) -> str:
    return "NA" if v is None else f"{v:.{digits}f}"

# ---------------------------------------------------------------------------
# Process reader
# ---------------------------------------------------------------------------


class ProcessReader:
    def __init__(self, pid: int):
        self.pid = pid
        self.handle = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.handle:
            raise RuntimeError(f"OpenProcess failed: {ctypes.get_last_error()}")

    def close(self):
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def read(self, address: int, size: int) -> bytes | None:
        if size <= 0 or not address:
            return b""
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t(0)
        ok = kernel32.ReadProcessMemory(
            self.handle,
            ctypes.c_void_p(address),
            buf,
            size,
            ctypes.byref(got),
        )
        if not ok or got.value != size:
            return None
        return buf.raw[:size]

    def u64(self, address: int) -> int | None:
        b = self.read(address, 8)
        return struct.unpack_from("<Q", b)[0] if b else None

    def u32(self, address: int) -> int | None:
        b = self.read(address, 4)
        return struct.unpack_from("<I", b)[0] if b else None

    def f32(self, address: int) -> float | None:
        b = self.read(address, 4)
        return struct.unpack_from("<f", b)[0] if b else None

    def f32x3(self, address: int) -> Vec3 | None:
        b = self.read(address, 12)
        if not b:
            return None
        x, y, z = struct.unpack_from("<fff", b)
        return Vec3(x, y, z)

    def read_string(self, char_ptr: int, max_len: int = MAX_PY_STRING) -> str:
        if not plausible_ptr(char_ptr):
            return ""
        b = self.read(char_ptr, max_len)
        if not b:
            return ""
        end = b.find(b"\x00")
        if end < 0:
            end = len(b)
        try:
            return b[:end].decode("ascii", errors="replace")
        except Exception:
            return ""

    def region_iter(self):
        address = 0
        limit = PTR_MAX
        mbi_size = ctypes.sizeof(MEMORY_BASIC_INFORMATION)
        while address < limit:
            mbi = MEMORY_BASIC_INFORMATION()
            q = kernel32.VirtualQueryEx(
                self.handle,
                ctypes.c_void_p(address),
                ctypes.byref(mbi),
                mbi_size,
            )
            if not q:
                break
            base = int(mbi.BaseAddress or 0)
            size = int(mbi.RegionSize or 0)
            if size <= 0:
                break
            yield base, size, int(mbi.State), int(mbi.Protect)
            nxt = base + size
            if nxt <= address:
                break
            address = nxt

    def scan_vtables(self, needles: dict[int, str]):
        """Single process-wide scan for all known object-vtable signatures."""
        targets = {struct.pack("<Q", int(addr)): name for addr, name in needles.items()}
        hits = defaultdict(set)
        scanned = 0
        regions = 0
        stopped_at_cap = False

        for base, size, state, protect in self.region_iter():
            if scanned >= MAX_SCAN_TOTAL_BYTES:
                stopped_at_cap = True
                break
            if state != MEM_COMMIT or protect & PAGE_GUARD or protect & PAGE_NOACCESS:
                continue
            protect_base = protect & 0xFF
            if protect_base not in READABLE_PROTECTIONS:
                continue
            regions += 1

            region_end = base + size
            cursor = base
            while cursor < region_end and scanned < MAX_SCAN_TOTAL_BYTES:
                n = min(CHUNK_SIZE, region_end - cursor, MAX_SCAN_TOTAL_BYTES - scanned)
                data = self.read(cursor, n)
                scanned += n
                if data:
                    for packed, kind in targets.items():
                        pos = data.find(packed)
                        while pos >= 0:
                            addr = cursor + pos
                            if addr % 8 == 0:
                                hits[kind].add(addr)
                            pos = data.find(packed, pos + 1)
                cursor += n

        return hits, scanned, regions, stopped_at_cap

# ---------------------------------------------------------------------------
# Process/window lookup
# ---------------------------------------------------------------------------


def find_pid() -> int | None:
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap:
        return None
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(e)
        if not kernel32.Process32FirstW(snap, ctypes.byref(e)):
            return None
        while True:
            if e.szExeFile.lower() == "ros64.exe":
                return int(e.th32ProcessID)
            if not kernel32.Process32NextW(snap, ctypes.byref(e)):
                break
        return None
    finally:
        kernel32.CloseHandle(snap)


def find_window(pid: int) -> tuple[int, str]:
    found = []

    @EnumWindowsProc
    def cb(hwnd, _lparam):
        p = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if int(p.value) != pid or not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(max(1, length + 1))
        user32.GetWindowTextW(hwnd, buf, len(buf))
        found.append((int(hwnd), buf.value))
        return False

    user32.EnumWindows(cb, 0)
    return found[0] if found else (0, "")


def module_info(reader: ProcessReader):
    arr = (wintypes.HMODULE * 64)()
    needed = wintypes.DWORD(0)
    if not psapi.EnumProcessModules(reader.handle, arr, ctypes.sizeof(arr), ctypes.byref(needed)):
        raise RuntimeError("EnumProcessModules failed")
    count = needed.value // ctypes.sizeof(wintypes.HMODULE)
    if count <= 0:
        raise RuntimeError("No modules returned")
    hmod = arr[0]
    info = MODULEINFO()
    if not psapi.GetModuleInformation(
        reader.handle, hmod, ctypes.byref(info), ctypes.sizeof(info)
    ):
        raise RuntimeError("GetModuleInformation failed")
    base = int(info.lpBaseOfDll or 0)
    size = int(info.SizeOfImage)
    return base, size


def window_metrics(hwnd: int):
    rect = wintypes.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(rect)):
        return {"width": 0, "height": 0, "origin_x": 0, "origin_y": 0, "cursor_client": None}
    pt = wintypes.POINT(0, 0)
    if not user32.ClientToScreen(hwnd, ctypes.byref(pt)):
        pt = wintypes.POINT(0, 0)
    cp = wintypes.POINT()
    cursor_client = None
    if user32.GetCursorPos(ctypes.byref(cp)):
        cursor_client = (float(cp.x - pt.x), float(cp.y - pt.y))
    return {
        "width": int(rect.right - rect.left),
        "height": int(rect.bottom - rect.top),
        "origin_x": int(pt.x),
        "origin_y": int(pt.y),
        "cursor_client": cursor_client,
    }

# ---------------------------------------------------------------------------
# Python object/dict helpers
# ---------------------------------------------------------------------------


def type_name(reader: ProcessReader, obj_ptr: int) -> str:
    if not plausible_ptr(obj_ptr):
        return ""
    typ = reader.u64(obj_ptr + ENT_OB_TYPE)
    if not plausible_ptr(typ):
        return ""
    name_obj = reader.u64(typ + PY_TYPE_NAME)
    if not plausible_ptr(name_obj):
        return ""
    chars = reader.u64(name_obj + PY_STRING_VALUE)
    return reader.read_string(chars or 0)


def dict_header(reader: ProcessReader, ptr: int):
    if not plausible_ptr(ptr):
        return None
    used = reader.u64(ptr + PY_DICT_USED)
    mask = reader.u64(ptr + PY_DICT_MASK)
    table = reader.u64(ptr + PY_DICT_TABLE)
    if used is None or mask is None or table is None:
        return None
    if used > MAX_DICT_SLOTS or mask > MAX_DICT_SLOTS - 1:
        return None
    slots = int(mask) + 1
    if used > slots or slots <= 0 or not plausible_ptr(table):
        return None
    return int(used), int(mask), int(table)


def read_dict_entries(reader: ProcessReader, dict_ptr: int):
    h = dict_header(reader, dict_ptr)
    if not h:
        return []
    used, mask, table = h
    total = (mask + 1) * PY_DICT_ENTRY_STRIDE
    if total <= 0 or total > MAX_DICT_SLOTS * PY_DICT_ENTRY_STRIDE:
        return []
    raw = reader.read(table, total)
    if not raw:
        # Page-independent fallback, same proven behavior as EntityLoop.
        raw = bytearray(total)
        any_ok = False
        for off in range(0, total, 0x1000):
            part = reader.read(table + off, min(0x1000, total - off))
            if part:
                raw[off:off + len(part)] = part
                any_ok = True
        if not any_ok:
            return []
        raw = bytes(raw)

    out = []
    for slot in range(mask + 1):
        off = slot * PY_DICT_ENTRY_STRIDE
        try:
            key = struct.unpack_from("<Q", raw, off + PY_DICT_ENTRY_KEY)[0]
            value = struct.unpack_from("<Q", raw, off + PY_DICT_ENTRY_VALUE)[0]
        except struct.error:
            break
        if plausible_ptr(value):
            out.append((slot, key, value))
    return out


def find_dict_key(reader: ProcessReader, dict_ptr: int, key_name: str):
    for slot, key_obj, value in read_dict_entries(reader, dict_ptr):
        chars = reader.u64(key_obj + PY_STRING_VALUE) if plausible_ptr(key_obj) else None
        if reader.read_string(chars or 0) == key_name:
            return slot, value, key_obj
    return None


def dict_pointer_hits(reader: ProcessReader, dict_ptr: int, target_ptrs: set[int]):
    hits = []
    for slot, key_obj, value in read_dict_entries(reader, dict_ptr):
        if value in target_ptrs:
            hits.append((slot, "value", value))
        if key_obj in target_ptrs:
            hits.append((slot, "key", key_obj))
    return hits

# ---------------------------------------------------------------------------
# Local player / BGA enumeration
# ---------------------------------------------------------------------------


def resolve_local_player(reader: ProcessReader, module_base: int):
    em_anchor = module_base + EM_STATIC_PTR
    em_ptr = reader.u64(em_anchor)
    if not plausible_ptr(em_ptr):
        return 0, None

    container = reader.u64(em_ptr + EM_CONTROLLED_ENT)
    if not plausible_ptr(container):
        return int(em_ptr), None

    root = reader.u64(container + NODE_PARENT)
    if not plausible_ptr(root) or root == container:
        return int(em_ptr), None

    player_ptr = reader.u64(root + CONTROLLED_NODE_VALUE)
    if not plausible_ptr(player_ptr):
        return int(em_ptr), None

    if type_name(reader, player_ptr) != "PlayerBattleGroundAvatar":
        return int(em_ptr), None

    xyz = reader.f32x3(player_ptr + ENT_X)
    return int(em_ptr), (int(player_ptr), xyz)


def resolve_aoi(reader: ProcessReader, player_ptr: int):
    if not plausible_ptr(player_ptr):
        return None

    # Proven production path first.
    primary = reader.u64(player_ptr + PY_INSTANCE_DICT)
    candidate_dicts = [(PY_INSTANCE_DICT, primary)]

    # Evidence-only alternate; do not assume it is the instance dict.
    alt = reader.u64(player_ptr + BGA_DICT_CANDIDATE_048)
    candidate_dicts.append((BGA_DICT_CANDIDATE_048, alt))

    for field_off, player_dict in candidate_dicts:
        header = dict_header(reader, player_dict or 0)
        if not header:
            continue

        # Fast path proven repeatedly for the PBGA instance dict: mask=0x7FF, slot 770.
        # We additionally inspect the candidate AOI table and require at least one
        # actual BattleGroundAvatar value before accepting the pointer.
        used, mask, table = header
        if mask == 0x7FF:
            slot = 770
            entry = table + slot * PY_DICT_ENTRY_STRIDE
            value = reader.u64(entry + PY_DICT_ENTRY_VALUE)
            if plausible_ptr(value) and dict_header(reader, value):
                bga_seen = 0
                for _, _, candidate_value in read_dict_entries(reader, value):
                    if type_name(reader, candidate_value) == "BattleGroundAvatar":
                        bga_seen += 1
                        if bga_seen >= 1:
                            return {
                                "field": field_off,
                                "player_dict": player_dict,
                                "used": used,
                                "mask": mask,
                                "table": table,
                                "aoi_dict": int(value),
                                "aoi_slot": slot,
                                "resolution": "verified_slot_770_plus_BGA_content",
                            }

        found = find_dict_key(reader, player_dict, "avatarInAoI")
        if found:
            slot, value, key_obj = found
            if dict_header(reader, value):
                return {
                    "field": field_off,
                    "player_dict": player_dict,
                    "used": used,
                    "mask": mask,
                    "table": table,
                    "aoi_dict": int(value),
                    "aoi_slot": slot,
                    "aoi_key_obj": key_obj,
                    "resolution": "key_scan",
                }

    return None


def enumerate_bga(reader: ProcessReader, module_base: int):
    em_ptr, local = resolve_local_player(reader, module_base)
    if not local:
        return em_ptr, 0, None, [], None

    local_ptr, local_xyz = local
    aoi = resolve_aoi(reader, local_ptr)
    if not aoi:
        return em_ptr, local_ptr, local_xyz, []

    results = []
    seen = set()
    for slot, key_obj, value_obj in read_dict_entries(reader, aoi["aoi_dict"]):
        if value_obj in seen or value_obj == local_ptr:
            continue
        seen.add(value_obj)
        typ = reader.u64(value_obj + ENT_OB_TYPE)
        if not plausible_ptr(typ):
            continue
        if type_name(reader, value_obj) != "BattleGroundAvatar":
            continue
        entity_id = reader.u32(value_obj + ENT_ENTITY_ID)
        xyz = reader.f32x3(value_obj + ENT_X)
        if entity_id is None or xyz is None or not finite(*xyz.as_tuple()):
            continue
        if any(abs(v) >= 50000 for v in xyz.as_tuple()):
            continue
        dict_048 = reader.u64(value_obj + BGA_DICT_CANDIDATE_048) or 0
        dict_1b0 = reader.u64(value_obj + PY_INSTANCE_DICT) or 0
        results.append(BgaSnapshot(
            ptr=value_obj,
            entity_id=int(entity_id),
            ob_type=int(typ),
            xyz=xyz,
            dict_048=int(dict_048),
            dict_1b0=int(dict_1b0),
            aoi_slot=int(slot),
        ))

    return em_ptr, local_ptr, local_xyz, results, aoi

# ---------------------------------------------------------------------------
# Camera reading / validation
# ---------------------------------------------------------------------------


def camera_from_block(reader: ProcessReader, ptr: int):
    raw = reader.read(ptr + CAM_FWD, CAM_BLOCK_END - CAM_FWD)
    if not raw or len(raw) != CAM_BLOCK_END - CAM_FWD:
        return None, "block_read_failed"

    def f(off):
        rel = off - CAM_FWD
        return struct.unpack_from("<f", raw, rel)[0]

    fwd = Vec3(f(CAM_FWD), f(CAM_FWD + 4), f(CAM_FWD + 8))
    up = Vec3(f(CAM_UP), f(CAM_UP + 4), f(CAM_UP + 8))
    right = Vec3(f(CAM_RIGHT), f(CAM_RIGHT + 4), f(CAM_RIGHT + 8))
    pos = Vec3(f(CAM_POS), f(CAM_POS + 4), f(CAM_POS + 8))
    yf = f(CAM_YFOCAL)
    xf = f(CAM_XFOCAL)
    ortho_rel = CAM_ORTHO - CAM_FWD
    ortho_f32 = struct.unpack_from("<f", raw, ortho_rel)[0]
    ortho_u32 = struct.unpack_from("<I", raw, ortho_rel)[0]

    lf = [vec_len(fwd), vec_len(up), vec_len(right)]
    len_err = max(abs(v - 1.0) for v in lf)
    d12 = abs(vec_dot(fwd, up))
    d13 = abs(vec_dot(fwd, right))
    d23 = abs(vec_dot(up, right))
    ortho_err = max(d12, d13, d23)
    hand = vec_dot(vec_cross(up, right), fwd)

    finite_all = finite(*fwd.as_tuple(), *up.as_tuple(), *right.as_tuple(), *pos.as_tuple(), yf, xf)
    sane = (
        finite_all
        and 0.95 <= lf[0] <= 1.05
        and 0.95 <= lf[1] <= 1.05
        and 0.95 <= lf[2] <= 1.05
        and ortho_err <= 0.05
        and 0.3 <= yf <= 10.0
        and 0.3 <= xf <= 10.0
        and all(abs(v) < 50000 for v in pos.as_tuple())
    )

    reason_parts = []
    if not finite_all:
        reason_parts.append("nonfinite")
    if max(abs(v - 1.0) for v in lf) > 0.05:
        reason_parts.append("basis_length")
    if ortho_err > 0.05:
        reason_parts.append("basis_orthogonality")
    if not (0.3 <= yf <= 10.0 and 0.3 <= xf <= 10.0):
        reason_parts.append("focal")
    if any(abs(v) >= 50000 for v in pos.as_tuple()):
        reason_parts.append("position")
    reason = "OK" if sane else ",".join(reason_parts)

    return CameraSnapshot(
        ptr=ptr,
        pos=pos,
        fwd=fwd,
        up=up,
        right=right,
        y_focal=yf,
        x_focal=xf,
        ortho_f32=ortho_f32,
        ortho_u32=ortho_u32,
        basis_len_err=len_err,
        basis_ortho_err=ortho_err,
        handedness=hand,
        sane=sane,
        reason=reason,
    ), raw


def camera_read_double(reader: ProcessReader, ptr: int):
    c1, raw1 = camera_from_block(reader, ptr)
    if c1 is not None and c1.sane:
        return c1, raw1, None
    c2, raw2 = camera_from_block(reader, ptr)
    if c2 is not None and c2.sane:
        return c2, raw2, "first_read_rejected_second_read_sane"
    return c1 or c2, raw2 or raw1, "both_reads_rejected"

# ---------------------------------------------------------------------------
# Model discovery / direct pointer correlation
# ---------------------------------------------------------------------------


def collect_models(reader: ProcessReader, model_ptrs: set[int], module_base: int):
    space_vtable = module_base + SPACE_NODE_VTABLE_RVA
    models = []
    for ptr in sorted(model_ptrs):
        sp = reader.u64(ptr + MODEL_SPACENODE_PTR)
        if not plausible_ptr(sp):
            continue
        actual_space_vtable = reader.u64(sp)
        if actual_space_vtable != space_vtable:
            continue
        xyz = reader.f32x3(sp + SN_X)
        if xyz is None or not finite(*xyz.as_tuple()) or any(abs(v) >= 50000 for v in xyz.as_tuple()):
            continue
        cache = reader.f32x3(sp + SN_CACHE)
        if cache is not None and not finite(*cache.as_tuple()):
            cache = None
        meta = reader.u64(ptr + MODEL_META_PTR) or 0
        models.append(ModelSnapshot(
            ptr=ptr,
            meta_ptr=int(meta),
            space_ptr=sp,
            xyz=xyz,
            cache_xyz=cache,
        ))
    return models


def find_exact_qwords(data: bytes, values: dict[int, str]):
    result = []
    for value, label in values.items():
        needle = struct.pack("<Q", value)
        start = data.find(needle)
        while start >= 0:
            if start % 8 == 0:
                result.append((start, label, value))
            start = data.find(needle, start + 1)
    return result


def find_u32(data: bytes, value: int):
    needle = struct.pack("<I", value & 0xFFFFFFFF)
    result = []
    start = data.find(needle)
    while start >= 0:
        if start % 4 == 0:
            result.append(start)
        start = data.find(needle, start + 1)
    return result


def pointer_correlation(reader: ProcessReader, bgas: list[BgaSnapshot], models: list[ModelSnapshot]):
    """Search exact object-pointer relationships without any spatial threshold."""
    links = []
    model_by_ptr = {m.ptr: m for m in models}
    model_ptr_to_space = {m.ptr: m.space_ptr for m in models}
    target_ptrs = {}
    for m in models:
        target_ptrs[m.ptr] = "MODEL_PTR"
        if m.space_ptr:
            target_ptrs[m.space_ptr] = "SPACE_PTR"
        if m.meta_ptr:
            target_ptrs[m.meta_ptr] = "MODEL_META_PTR"

    # Read every object only once per F9. Previous versions repeatedly reread
    # every model for every BGA, which made F9 appear hung on large candidate sets.
    bga_raw = {b.ptr: (reader.read(b.ptr, 0x300) or b"") for b in bgas}
    model_raw = {m.ptr: (reader.read(m.ptr, 0x240) or b"") for m in models}
    meta_raw = {
        m.meta_ptr: (reader.read(m.meta_ptr, 0x240) or b"")
        for m in models
        if plausible_ptr(m.meta_ptr)
    }

    for b in bgas:
        raw = bga_raw[b.ptr]
        if raw:
            for off, label, value in find_exact_qwords(raw, target_ptrs):
                links.append({
                    "direction": "BGA->MODEL_GRAPH",
                    "bga_ptr": b.ptr,
                    "entity_id": b.entity_id,
                    "model_ptr": value if value in model_by_ptr else 0,
                    "matched_ptr": value,
                    "matched_kind": label,
                    "offset": off,
                })

        # Test both offsets separately and only report a hit if the candidate
        # pointer is itself a structurally valid Python dictionary.
        for field_name, dptr in (("+0x1B0", b.dict_1b0), ("+0x48", b.dict_048)):
            if not dict_header(reader, dptr):
                continue
            for slot, side, value in dict_pointer_hits(reader, dptr, set(target_ptrs)):
                links.append({
                    "direction": f"BGA_DICT{field_name}",
                    "bga_ptr": b.ptr,
                    "entity_id": b.entity_id,
                    "model_ptr": value if value in model_by_ptr else 0,
                    "matched_ptr": value,
                    "matched_kind": f"{side}_{target_ptrs[value]}",
                    "offset": slot,
                })

        for m in models:
            raw_m = model_raw[m.ptr]
            if raw_m:
                for off in find_u64_offsets(raw_m, b.ptr):
                    links.append({
                        "direction": "MODEL->BGA_PTR",
                        "bga_ptr": b.ptr,
                        "entity_id": b.entity_id,
                        "model_ptr": m.ptr,
                        "matched_ptr": b.ptr,
                        "matched_kind": "BGA_POINTER",
                        "offset": off,
                    })
                for off in find_u32(raw_m, b.entity_id):
                    links.append({
                        "direction": "MODEL->BGA_ID",
                        "bga_ptr": b.ptr,
                        "entity_id": b.entity_id,
                        "model_ptr": m.ptr,
                        "matched_ptr": 0,
                        "matched_kind": "BGA_ID_U32",
                        "offset": off,
                    })
            if plausible_ptr(m.meta_ptr):
                raw_meta = meta_raw.get(m.meta_ptr, b"")
                if raw_meta:
                    for off in find_u64_offsets(raw_meta, b.ptr):
                        links.append({
                            "direction": "META->BGA_PTR",
                            "bga_ptr": b.ptr,
                            "entity_id": b.entity_id,
                            "model_ptr": m.ptr,
                            "matched_ptr": b.ptr,
                            "matched_kind": "BGA_POINTER",
                            "offset": off,
                        })
                    for off in find_u32(raw_meta, b.entity_id):
                        links.append({
                            "direction": "META->BGA_ID",
                            "bga_ptr": b.ptr,
                            "entity_id": b.entity_id,
                            "model_ptr": m.ptr,
                            "matched_ptr": 0,
                            "matched_kind": "BGA_ID_U32",
                            "offset": off,
                        })

    unique = {}
    for x in links:
        unique[tuple(sorted(x.items()))] = x
    return list(unique.values())


def find_u64_offsets(data: bytes, value: int):
    needle = struct.pack("<Q", value)
    out = []
    p = data.find(needle)
    while p >= 0:
        if p % 8 == 0:
            out.append(p)
        p = data.find(needle, p + 1)
    return out

# ---------------------------------------------------------------------------
# Projection
# ---------------------------------------------------------------------------


def project(world: Vec3, cam: CameraSnapshot, width: int, height: int):
    if not cam.sane or width <= 0 or height <= 0:
        return None, None
    d = vec_sub(world, cam.pos)
    vx = vec_dot(d, cam.right)
    vy = vec_dot(d, cam.up)
    vz = vec_dot(d, cam.fwd)
    if not finite(vx, vy, vz) or vz <= 0.01:
        return None, vz
    ar = cam.y_focal / cam.x_focal
    sx = width * 0.5 * (1.0 + ar * vx / vz)
    sy = height * 0.5 * (1.0 - cam.y_focal * vy / vz)
    if not finite(sx, sy):
        return None, vz
    return (sx, sy), vz


def screen_dist_to(a, b):
    if a is None or b is None:
        return None
    return math.hypot(a[0] - b[0], a[1] - b[1])

# ---------------------------------------------------------------------------
# Time-series tracking
# ---------------------------------------------------------------------------


class Tracker:
    def __init__(self, reader, module_base):
        self.reader = reader
        self.module_base = module_base
        self.lock = threading.RLock()
        self.bgas: dict[int, BgaSnapshot] = {}
        self.models: dict[int, ModelSnapshot] = {}
        self.cameras: dict[int, CameraSnapshot] = {}
        self.local_player_ptr = 0
        self.local_player_xyz: Vec3 | None = None
        self.em_ptr = 0
        self.aoi_info = None
        self.scan_stats = {}
        self.history = deque()
        self.stop = threading.Event()
        self.scan_requested = threading.Event()
        self.last_rescan = 0.0
        self.last_error = ""
        self.thread = threading.Thread(target=self._run, name="ROS64-FinalTracker", daemon=True)

    def start(self):
        self.thread.start()

    def stop_and_join(self):
        self.stop.set()
        self.thread.join(timeout=2.0)

    def request_rescan(self):
        self.scan_requested.set()

    def _full_discovery(self):
        needles = {
            self.module_base + CAMERA_VTABLE_RVA: "camera",
            self.module_base + MODEL_SKELETAL_VTABLE_RVA: "model",
            self.module_base + SPACE_NODE_VTABLE_RVA: "space",
        }
        hits, scanned, regions, cap = self.reader.scan_vtables(needles)
        camera_ptrs = sorted(hits.get("camera", set()))
        model_ptrs = sorted(hits.get("model", set()))

        cameras = {}
        camera_rejections = defaultdict(int)
        for ptr in camera_ptrs:
            cam, raw, _ = camera_read_double(self.reader, ptr)
            if cam is None:
                continue
            cameras[ptr] = cam
            if not cam.sane:
                camera_rejections[cam.reason] += 1

        models = collect_models(self.reader, set(model_ptrs), self.module_base)
        em_ptr, local_ptr, local_xyz, bgas, aoi = enumerate_bga(self.reader, self.module_base)

        with self.lock:
            self.cameras = cameras
            self.models = {m.ptr: m for m in models}
            self.bgas = {b.ptr: b for b in bgas}
            self.local_player_ptr = local_ptr
            self.local_player_xyz = local_xyz
            self.em_ptr = em_ptr
            self.aoi_info = aoi
            self.scan_stats = {
                "scanned_bytes": scanned,
                "regions": regions,
                "scan_capped": cap,
                "camera_vtable_hits": len(camera_ptrs),
                "camera_candidates": len(cameras),
                "camera_sane": sum(1 for c in cameras.values() if c.sane),
                "camera_rejections": dict(camera_rejections),
                "model_vtable_hits": len(model_ptrs),
                "model_valid": len(models),
                "bga_valid": len(bgas),
                "aoi": aoi,
                "last_scan_iso": datetime.now().isoformat(timespec="milliseconds"),
            }
            self.last_rescan = time.time()
            self.scan_stats["pointer_links"] = None

        print(
            f"[DISCOVERY] cameras={len(cameras)} sane={sum(1 for c in cameras.values() if c.sane)} "
            f"models={len(models)} BGAs={len(bgas)} scanned={scanned:,}"
        )
        if aoi:
            print(
                f"[DISCOVERY][AOI] player=0x{local_ptr:X} dictField={hex(aoi['field'])} "
                f"playerDict=0x{aoi['player_dict']:X} aoiDict=0x{aoi['aoi_dict']:X} "
                f"slot={aoi['aoi_slot']} resolution={aoi['resolution']}"
            )
        elif local_ptr:
            print(f"[DISCOVERY][AOI] player=0x{local_ptr:X} but avatarInAoI not resolved")

    def _poll(self):
        now = time.time()
        wall = datetime.now().isoformat(timespec="milliseconds")
        with self.lock:
            bga_ptrs = list(self.bgas.keys())
            model_items = list(self.models.items())
            camera_ptrs = list(self.cameras.keys())
            local_ptr = self.local_player_ptr

        bga_pos = {}
        for ptr in bga_ptrs:
            xyz = self.reader.f32x3(ptr + ENT_X)
            if xyz is not None and finite(*xyz.as_tuple()) and all(abs(v) < 50000 for v in xyz.as_tuple()):
                bga_pos[ptr] = xyz

        model_pos = {}
        for ptr, model in model_items:
            xyz = self.reader.f32x3(model.space_ptr + SN_X)
            if xyz is not None and finite(*xyz.as_tuple()) and all(abs(v) < 50000 for v in xyz.as_tuple()):
                model_pos[ptr] = xyz

        camera_values = {}
        for ptr in camera_ptrs:
            cam, _, _ = camera_read_double(self.reader, ptr)
            if cam is not None and cam.sane:
                camera_values[ptr] = cam

        local_xyz = None
        if plausible_ptr(local_ptr):
            local_xyz = self.reader.f32x3(local_ptr + ENT_X)
            if local_xyz is not None and not finite(*local_xyz.as_tuple()):
                local_xyz = None

        sample = TraceSample(
            t=now,
            wall_iso=wall,
            bga=bga_pos,
            models=model_pos,
            cameras=camera_values,
            local_player_ptr=local_ptr,
            local_player_xyz=local_xyz,
        )

        with self.lock:
            self.history.append(sample)
            cutoff = now - HISTORY_SECONDS
            while self.history and self.history[0].t < cutoff:
                self.history.popleft()
            if local_xyz is not None:
                self.local_player_xyz = local_xyz
            for ptr, xyz in bga_pos.items():
                if ptr in self.bgas:
                    old = self.bgas[ptr]
                    self.bgas[ptr] = BgaSnapshot(old.ptr, old.entity_id, old.ob_type, xyz, old.dict_048, old.dict_1b0, old.aoi_slot)
            for ptr, xyz in model_pos.items():
                if ptr in self.models:
                    old = self.models[ptr]
                    self.models[ptr] = ModelSnapshot(old.ptr, old.meta_ptr, old.space_ptr, xyz, old.cache_xyz)
            for ptr, cam in camera_values.items():
                self.cameras[ptr] = cam

    def _run(self):
        next_poll = 0.0
        while not self.stop.is_set():
            now = time.time()
            try:
                if now >= next_poll:
                    self._poll()
                    next_poll = now + TRACE_INTERVAL_SEC
                if self.scan_requested.is_set() or now - self.last_rescan >= RESCAN_INTERVAL_SEC:
                    self.scan_requested.clear()
                    self._full_discovery()
                time.sleep(0.01)
            except Exception as exc:
                self.last_error = repr(exc)
                time.sleep(0.25)

    def snapshot(self):
        with self.lock:
            return {
                "history": list(self.history),
                "bgas": list(self.bgas.values()),
                "models": list(self.models.values()),
                "cameras": dict(self.cameras),
                "local_player_ptr": self.local_player_ptr,
                "local_player_xyz": self.local_player_xyz,
                "em_ptr": self.em_ptr,
                "aoi_info": self.aoi_info,
                "scan_stats": dict(self.scan_stats),
                "last_error": self.last_error,
            }

# ---------------------------------------------------------------------------
# Motion correlation
# ---------------------------------------------------------------------------


def motion_metrics(history: list[TraceSample], bga_ptr: int, model_ptr: int):
    rows = []
    for s in history:
        if bga_ptr in s.bga and model_ptr in s.models:
            rows.append((s.t, s.bga[bga_ptr], s.models[model_ptr]))

    if len(rows) < 3:
        return {
            "samples": len(rows),
            "bga_motion": 0.0,
            "model_motion": 0.0,
            "offset_rms": None,
            "velocity_rmse": None,
            "velocity_cos_mean": None,
            "position_r2": None,
            "offset_x": None,
            "offset_y": None,
            "offset_z": None,
        }

    offsets = [vec_sub(m, b) for _, b, m in rows]
    mean_off = vec_scale(
        Vec3(
            sum(o.x for o in offsets) / len(offsets),
            sum(o.y for o in offsets) / len(offsets),
            sum(o.z for o in offsets) / len(offsets),
        ),
        1.0,
    )
    off_sq = [vec_len(vec_sub(o, mean_off)) ** 2 for o in offsets]
    offset_rms = math.sqrt(sum(off_sq) / len(off_sq))

    v_b = []
    v_m = []
    for i in range(1, len(rows)):
        dt = max(rows[i][0] - rows[i - 1][0], 1e-6)
        db = vec_scale(vec_sub(rows[i][1], rows[i - 1][1]), 1.0 / dt)
        dm = vec_scale(vec_sub(rows[i][2], rows[i - 1][2]), 1.0 / dt)
        v_b.append(db)
        v_m.append(dm)

    vr_sq = [vec_len(vec_sub(m, b)) ** 2 for b, m in zip(v_b, v_m)]
    velocity_rmse = math.sqrt(sum(vr_sq) / len(vr_sq)) if vr_sq else None

    cos = []
    for b, m in zip(v_b, v_m):
        lb = vec_len(b)
        lm = vec_len(m)
        if lb > 1e-3 and lm > 1e-3:
            c = max(-1.0, min(1.0, vec_dot(b, m) / (lb * lm)))
            cos.append(c)
    velocity_cos = sum(cos) / len(cos) if cos else None

    mean_b = Vec3(
        sum(b.x for _, b, _ in rows) / len(rows),
        sum(b.y for _, b, _ in rows) / len(rows),
        sum(b.z for _, b, _ in rows) / len(rows),
    )
    baseline = sum(vec_len(vec_sub(b, mean_b)) ** 2 for _, b, _ in rows)
    residual = sum(vec_len(vec_sub(vec_sub(m, b), mean_off)) ** 2 for _, b, m in rows)
    position_r2 = None if baseline < 1e-6 else 1.0 - (residual / baseline)

    bga_motion = sum(vec_len(vec_sub(rows[i][1], rows[i - 1][1])) for i in range(1, len(rows)))
    model_motion = sum(vec_len(vec_sub(rows[i][2], rows[i - 1][2])) for i in range(1, len(rows)))

    return {
        "samples": len(rows),
        "bga_motion": bga_motion,
        "model_motion": model_motion,
        "offset_rms": offset_rms,
        "velocity_rmse": velocity_rmse,
        "velocity_cos_mean": velocity_cos,
        "position_r2": position_r2,
        "offset_x": mean_off.x,
        "offset_y": mean_off.y,
        "offset_z": mean_off.z,
    }

# ---------------------------------------------------------------------------
# Capture writer
# ---------------------------------------------------------------------------


class CaptureWriter:
    def __init__(self, base: Path):
        self.base = base
        self.base.mkdir(parents=True, exist_ok=True)
        self.count = 0

    def _csv(self, path: Path, rows: list[dict], fieldnames: list[str]):
        with path.open("w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=fieldnames)
            w.writeheader()
            w.writerows(rows)

    def write_capture(self, tracker: Tracker, reader: ProcessReader, module_base: int, module_size: int, hwnd: int, trigger: dict):
        self.count += 1
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")[:-3]
        folder = self.base / f"capture_{self.count:03d}_{stamp}"
        raw_dir = folder / "raw"
        folder.mkdir(parents=True, exist_ok=True)
        raw_dir.mkdir(parents=True, exist_ok=True)

        state = tracker.snapshot()
        now = time.time()
        history = [s for s in state["history"] if s.t >= now - F9_HISTORY_SECONDS]
        bgas = state["bgas"]
        models = state["models"]
        cameras = state["cameras"]
        local_xyz = state["local_player_xyz"]
        metrics = state["scan_stats"]

        # Refresh every BGA/model/camera once at the F9 boundary.
        # No cursor-based candidate selection is performed.
        refreshed_bgas = []
        for b in bgas:
            xyz = reader.f32x3(b.ptr + ENT_X)
            if xyz is not None and finite(*xyz.as_tuple()) and all(abs(v) < 50000 for v in xyz.as_tuple()):
                refreshed_bgas.append(BgaSnapshot(b.ptr, b.entity_id, b.ob_type, xyz, b.dict_048, b.dict_1b0, b.aoi_slot))

        refreshed_models = []
        for m in models:
            xyz = reader.f32x3(m.space_ptr + SN_X)
            if xyz is not None and finite(*xyz.as_tuple()) and all(abs(v) < 50000 for v in xyz.as_tuple()):
                cache = reader.f32x3(m.space_ptr + SN_CACHE)
                refreshed_models.append(ModelSnapshot(m.ptr, m.meta_ptr, m.space_ptr, xyz, cache))

        refreshed_cameras = {}
        camera_notes = {}
        for ptr in cameras:
            cam, raw, note = camera_read_double(reader, ptr)
            if cam is not None and cam.sane:
                refreshed_cameras[ptr] = cam
                camera_notes[ptr] = note

        if refreshed_bgas:
            bgas = refreshed_bgas
        if refreshed_models:
            models = refreshed_models
        if refreshed_cameras:
            cameras = refreshed_cameras
        if local_xyz is None and state["local_player_ptr"]:
            local_xyz = reader.f32x3(state["local_player_ptr"] + ENT_X)

        # README-like manifest with explicit assumptions and filters.
        manifest = folder / "manifest.txt"
        with manifest.open("w", encoding="utf-8") as f:
            f.write("ROS64 Diagnostic Capture FINAL\n")
            f.write("================================\n\n")
            f.write("READ-ONLY: no process-memory writes.\n\n")
            f.write("PROVEN/RECONCILED INPUT CONSTANTS\n")
            for k, v in [
                ("EM_STATIC_PTR", hex(EM_STATIC_PTR)),
                ("EM_CONTROLLED_ENT", hex(EM_CONTROLLED_ENT)),
                ("ENT_OB_TYPE", hex(ENT_OB_TYPE)),
                ("ENT_ENTITY_ID", hex(ENT_ENTITY_ID)),
                ("ENT_X/Y/Z", "0x1C/0x20/0x24"),
                ("PY_INSTANCE_DICT", hex(PY_INSTANCE_DICT)),
                ("PY_DICT_USED/MASK/TABLE", "0x18/0x20/0x28"),
                ("PY_DICT_ENTRY_KEY/VALUE", "0x08/0x10"),
                ("PY_DICT_ENTRY_STRIDE", hex(PY_DICT_ENTRY_STRIDE)),
                ("CAMERA_VTABLE_RVA", hex(CAMERA_VTABLE_RVA)),
                ("MODEL_SKELETAL_VTABLE_RVA", hex(MODEL_SKELETAL_VTABLE_RVA)),
                ("SPACE_NODE_VTABLE_RVA", hex(SPACE_NODE_VTABLE_RVA)),
                ("MODEL_SPACENODE_PTR", hex(MODEL_SPACENODE_PTR)),
                ("MODEL_META_PTR", hex(MODEL_META_PTR)),
                ("SPACE_X/Y/Z", "0x48/0x4C/0x50"),
            ]:
                f.write(f"{k}={v}\n")
            f.write("\nDIAGNOSTIC-ONLY TESTS (NOT ASSUMED FACTS)\n")
            f.write("BGA +0x48 is inspected only because prior evidence exposed a dict-like pointer there.\n")
            f.write("BGA +0x1B0 is inspected as the current proven PBGA instance-dict offset; for BGA it is tested, not assumed.\n")
            f.write("BGA->Model identity is not declared from screen/cursor proximity.\n")
            f.write("Camera candidates are never ranked by projected-count alone.\n")
            f.write("The camera block is read contiguously so fwd/up/right/pos/focal come from one read.\n")
            f.write("Camera sanity filters: basis lengths within 5%, pairwise dot products <=0.05, focal 0.3..10, finite position <50000.\n")
            f.write("Motion correlation is reported numerically without an arbitrary identity pass/fail threshold.\n")

        # Summary.
        window = window_metrics(hwnd)
        with (folder / "summary.txt").open("w", encoding="utf-8") as f:
            f.write(f"capture_time={trigger['time']}\n")
            f.write(f"trigger_cursor_client={trigger.get('cursor_client')}\n")
            f.write(f"module_base={hex(module_base)}\nmodule_size={hex(module_size)}\n")
            f.write(f"window={window}\n")
            f.write(f"em_ptr={hexp(state['em_ptr'])}\n")
            f.write(f"local_player_ptr={hexp(state['local_player_ptr'])}\n")
            f.write(f"local_player_xyz={fmt_vec(local_xyz)}\n")
            f.write(f"aoi_info={state['aoi_info']}\n")
            f.write(f"bga_count={len(bgas)}\nmodel_count={len(models)}\ncamera_count={len(cameras)}\n")
            f.write(f"history_samples={len(history)}\n")
            f.write(f"scan_stats={metrics}\n")
            f.write(f"tracker_error={state['last_error']}\n")

        # BGA snapshot CSV.
        self._csv(
            folder / "bga_snapshot.csv",
            [{
                "ptr": hexp(b.ptr), "entity_id": b.entity_id, "ob_type": hexp(b.ob_type),
                "x": b.xyz.x, "y": b.xyz.y, "z": b.xyz.z,
                "dict_048": hexp(b.dict_048), "dict_1b0": hexp(b.dict_1b0), "aoi_slot": b.aoi_slot,
            } for b in bgas],
            ["ptr","entity_id","ob_type","x","y","z","dict_048","dict_1b0","aoi_slot"],
        )

        self._csv(
            folder / "model_snapshot.csv",
            [{
                "model_ptr": hexp(m.ptr), "meta_ptr": hexp(m.meta_ptr), "space_ptr": hexp(m.space_ptr),
                "x": m.xyz.x, "y": m.xyz.y, "z": m.xyz.z,
                "cache_x": m.cache_xyz.x if m.cache_xyz else None,
                "cache_y": m.cache_xyz.y if m.cache_xyz else None,
                "cache_z": m.cache_xyz.z if m.cache_xyz else None,
            } for m in models],
            ["model_ptr","meta_ptr","space_ptr","x","y","z","cache_x","cache_y","cache_z"],
        )

        self._csv(
            folder / "camera_candidates.csv",
            [{
                "ptr": hexp(c.ptr), "sane": c.sane, "reason": c.reason,
                "pos_x": c.pos.x, "pos_y": c.pos.y, "pos_z": c.pos.z,
                "fwd_x": c.fwd.x, "fwd_y": c.fwd.y, "fwd_z": c.fwd.z,
                "up_x": c.up.x, "up_y": c.up.y, "up_z": c.up.z,
                "right_x": c.right.x, "right_y": c.right.y, "right_z": c.right.z,
                "yfocal": c.y_focal, "xfocal": c.x_focal,
                "ortho_f32": c.ortho_f32, "ortho_u32": c.ortho_u32,
                "basis_len_err": c.basis_len_err, "basis_ortho_err": c.basis_ortho_err,
                "handedness": c.handedness, "double_read_note": camera_notes.get(c.ptr, ""),
            } for c in cameras.values()],
            ["ptr","sane","reason","pos_x","pos_y","pos_z","fwd_x","fwd_y","fwd_z","up_x","up_y","up_z","right_x","right_y","right_z","yfocal","xfocal","ortho_f32","ortho_u32","basis_len_err","basis_ortho_err","handedness","double_read_note"],
        )

        # Exact pointer relationship scan.
        links = pointer_correlation(reader, bgas, models)
        self._csv(
            folder / "pointer_links.csv",
            links,
            ["direction","bga_ptr","entity_id","model_ptr","matched_ptr","matched_kind","offset"],
        )

        # Camera projections and BGA/model screen tables.
        projection_rows = []
        camera_eval = []
        center = (window["width"] * 0.5, window["height"] * 0.5)
        for cam in cameras.values():
            if not cam.sane:
                continue
            local_proj, local_depth = project(local_xyz, cam, window["width"], window["height"]) if local_xyz else (None, None)
            bga_proj = []
            model_proj = []
            for b in bgas:
                scr, depth = project(b.xyz, cam, window["width"], window["height"])
                bga_proj.append((b, scr, depth))
                projection_rows.append({
                    "camera_ptr": hexp(cam.ptr), "object_kind": "BGA", "object_ptr": hexp(b.ptr),
                    "entity_id": b.entity_id, "x": b.xyz.x, "y": b.xyz.y, "z": b.xyz.z,
                    "screen_x": scr[0] if scr else None, "screen_y": scr[1] if scr else None,
                    "depth": depth,
                    "center_dist_px": screen_dist_to(scr, center),
                    "on_client": bool(scr and -300 <= scr[0] <= window["width"] + 300 and -300 <= scr[1] <= window["height"] + 300),
                })
            for m in models:
                scr, depth = project(m.xyz, cam, window["width"], window["height"])
                model_proj.append((m, scr, depth))
                projection_rows.append({
                    "camera_ptr": hexp(cam.ptr), "object_kind": "MODEL", "object_ptr": hexp(m.ptr),
                    "entity_id": None, "x": m.xyz.x, "y": m.xyz.y, "z": m.xyz.z,
                    "screen_x": scr[0] if scr else None, "screen_y": scr[1] if scr else None,
                    "depth": depth,
                    "center_dist_px": screen_dist_to(scr, center),
                    "on_client": bool(scr and -300 <= scr[0] <= window["width"] + 300 and -300 <= scr[1] <= window["height"] + 300),
                })

            cam_screen_candidates = []
            for b, s, d in bga_proj:
                if s is not None:
                    cam_screen_candidates.append((screen_dist_to(s, center), b.ptr))
            model_screen_candidates = []
            for m, s, d in model_proj:
                if s is not None:
                    model_screen_candidates.append((screen_dist_to(s, center), m.ptr))
            cam_eval = {
                "camera_ptr": hexp(cam.ptr),
                "local_distance_raw": euclid(cam.pos, local_xyz) if local_xyz else None,
                "local_screen_x": local_proj[0] if local_proj else None,
                "local_screen_y": local_proj[1] if local_proj else None,
                "local_depth": local_depth,
                "bga_projected": sum(1 for _, s, _ in bga_proj if s is not None),
                "bga_onscreen": sum(1 for _, s, _ in bga_proj if s and 0 <= s[0] <= window["width"] and 0 <= s[1] <= window["height"]),
                "model_projected": sum(1 for _, s, _ in model_proj if s is not None),
                "model_onscreen": sum(1 for _, s, _ in model_proj if s and 0 <= s[0] <= window["width"] and 0 <= s[1] <= window["height"]),
                "center_nearest_bga_px": min((x[0] for x in cam_screen_candidates), default=None),
                "center_nearest_model_px": min((x[0] for x in model_screen_candidates), default=None),
            }
            if cam_screen_candidates:
                cam_eval["center_nearest_bga_ptr"] = hexp(min(cam_screen_candidates)[1])
            else:
                cam_eval["center_nearest_bga_ptr"] = ""
            if model_screen_candidates:
                cam_eval["center_nearest_model_ptr"] = hexp(min(model_screen_candidates)[1])
            else:
                cam_eval["center_nearest_model_ptr"] = ""
            if local_xyz:
                # This is descriptive, not an automatic active-camera claim.
                cam_eval["local_screen_on_client"] = bool(local_proj and 0 <= local_proj[0] <= window["width"] and 0 <= local_proj[1] <= window["height"])
            else:
                cam_eval["local_screen_on_client"] = False
            camera_eval.append(cam_eval)

        self._csv(
            folder / "projection_rows.csv",
            projection_rows,
            ["camera_ptr","object_kind","object_ptr","entity_id","x","y","z","screen_x","screen_y","depth","center_dist_px","on_client"],
        )
        self._csv(
            folder / "camera_evaluations.csv",
            camera_eval,
            ["camera_ptr","local_distance_raw","local_screen_x","local_screen_y","local_depth","bga_projected","bga_onscreen","model_projected","model_onscreen","center_nearest_bga_px","center_nearest_bga_ptr","center_nearest_model_px","center_nearest_model_ptr","local_screen_on_client"],
        )

        # All-pair current spatial comparison. No pair is marked as identity.
        pair_rows = []
        camera_list = [c for c in cameras.values() if c.sane]
        for cam in camera_list:
            bga_screen = {}
            model_screen = {}
            for b in bgas:
                bga_screen[b.ptr] = project(b.xyz, cam, window["width"], window["height"])[0]
            for m in models:
                model_screen[m.ptr] = project(m.xyz, cam, window["width"], window["height"])[0]
            for b in bgas:
                for m in models:
                    sd = screen_dist_to(bga_screen[b.ptr], model_screen[m.ptr])
                    pair_rows.append({
                        "camera_ptr": hexp(cam.ptr), "bga_ptr": hexp(b.ptr), "entity_id": b.entity_id,
                        "model_ptr": hexp(m.ptr), "space_ptr": hexp(m.space_ptr),
                        "world_distance": euclid(b.xyz, m.xyz), "screen_distance": sd,
                        "bga_screen_x": bga_screen[b.ptr][0] if bga_screen[b.ptr] else None,
                        "bga_screen_y": bga_screen[b.ptr][1] if bga_screen[b.ptr] else None,
                        "model_screen_x": model_screen[m.ptr][0] if model_screen[m.ptr] else None,
                        "model_screen_y": model_screen[m.ptr][1] if model_screen[m.ptr] else None,
                    })
        self._csv(
            folder / "bga_model_pairs.csv",
            pair_rows,
            ["camera_ptr","bga_ptr","entity_id","model_ptr","space_ptr","world_distance","screen_distance","bga_screen_x","bga_screen_y","model_screen_x","model_screen_y"],
        )

        # Motion correlations from pre-trigger history.
        motion_rows = []
        for b in bgas:
            for m in models:
                mm = motion_metrics(history, b.ptr, m.ptr)
                motion_rows.append({
                    "bga_ptr": hexp(b.ptr), "entity_id": b.entity_id,
                    "model_ptr": hexp(m.ptr), "space_ptr": hexp(m.space_ptr),
                    **mm,
                })
        motion_rows.sort(key=lambda r: (-(r["position_r2"] if r["position_r2"] is not None else -1e9), r["offset_rms"] if r["offset_rms"] is not None else 1e99))
        self._csv(
            folder / "motion_correlations.csv",
            motion_rows,
            ["bga_ptr","entity_id","model_ptr","space_ptr","samples","bga_motion","model_motion","offset_rms","velocity_rmse","velocity_cos_mean","position_r2","offset_x","offset_y","offset_z"],
        )

        # Track samples in normalized long format, enough to reconstruct movement.
        trace_rows = []
        for s in history:
            for bptr, xyz in s.bga.items():
                b = next((x for x in bgas if x.ptr == bptr), None)
                trace_rows.append({
                    "t": s.t, "wall_iso": s.wall_iso, "kind": "BGA", "ptr": hexp(bptr),
                    "entity_id": b.entity_id if b else None, "x": xyz.x, "y": xyz.y, "z": xyz.z,
                })
            for mptr, xyz in s.models.items():
                trace_rows.append({
                    "t": s.t, "wall_iso": s.wall_iso, "kind": "MODEL", "ptr": hexp(mptr),
                    "entity_id": None, "x": xyz.x, "y": xyz.y, "z": xyz.z,
                })
        self._csv(
            folder / "trace.csv",
            trace_rows,
            ["t","wall_iso","kind","ptr","entity_id","x","y","z"],
        )

        # Raw BGA memory and all model/meta headers used in pointer analysis.
        for i, b in enumerate(bgas):
            data = reader.read(b.ptr, 0x300)
            if data:
                (raw_dir / f"bga_{i:03d}_{b.entity_id}_{b.ptr:016X}.bin").write_bytes(data)
        for i, m in enumerate(models):
            data = reader.read(m.ptr, 0x240)
            if data:
                (raw_dir / f"model_{i:03d}_{m.ptr:016X}.bin").write_bytes(data)
            if plausible_ptr(m.meta_ptr):
                md = reader.read(m.meta_ptr, 0x240)
                if md:
                    (raw_dir / f"meta_{i:03d}_{m.meta_ptr:016X}.bin").write_bytes(md)
            sd = reader.read(m.space_ptr, 0xD0)
            if sd:
                (raw_dir / f"space_{i:03d}_{m.space_ptr:016X}.bin").write_bytes(sd)

        # Human-readable top motion candidates. This is a report, not a verdict.
        with (folder / "analysis.txt").open("w", encoding="utf-8") as f:
            f.write("FINAL DIAGNOSTIC ANALYSIS DATA\n")
            f.write("==============================\n\n")
            f.write("No automatic identity verdict is produced. Read motion_correlations.csv and pointer_links.csv.\n\n")
            f.write("DIRECT POINTER LINKS\n")
            for row in links:
                f.write(
                    f"{row['direction']} BGA={hexp(row['bga_ptr'])} id={row['entity_id']} "
                    f"MODEL={hexp(row['model_ptr'])} matched={hexp(row['matched_ptr'])} "
                    f"kind={row['matched_kind']} offset={hex(row['offset'])}\n"
                )
            f.write("\nTOP MOTION CORRELATIONS BY POSITION_R2 / OFFSET_RMS\n")
            for row in motion_rows[:100]:
                f.write(
                    f"BGA={row['bga_ptr']} id={row['entity_id']} MODEL={row['model_ptr']} "
                    f"samples={row['samples']} r2={fmt_num(row['position_r2'])} "
                    f"offset_rms={fmt_num(row['offset_rms'])} vel_rmse={fmt_num(row['velocity_rmse'])} "
                    f"vel_cos={fmt_num(row['velocity_cos_mean'])} "
                    f"bga_motion={fmt_num(row['bga_motion'])} model_motion={fmt_num(row['model_motion'])}\n"
                )
            f.write("\nCAMERA EVALUATIONS\n")
            for row in camera_eval:
                f.write(str(row) + "\n")

        # Persist the trigger metadata.
        with (folder / "trigger.txt").open("w", encoding="utf-8") as f:
            for k, v in trigger.items():
                f.write(f"{k}={v}\n")

        print(f"[!!!] F9 CAPTURE WRITTEN: {folder}")
        print(f"     BGAs={len(bgas)} Models={len(models)} Cameras={len(cameras)} History={len(history)}")
        print(f"     DirectPointerLinks={len(links)}")
        print("     Cursor recorded only; NOT used for target selection.")
        return folder

# ---------------------------------------------------------------------------
# Keyboard monitor
# ---------------------------------------------------------------------------


class KeyboardMonitor(threading.Thread):
    def __init__(self):
        super().__init__(name="ROS64-FinalKeyboard", daemon=True)
        self.stop_event = threading.Event()
        self.trigger_lock = threading.Lock()
        self.pending = deque()
        self.prev_f9 = False
        self.prev_f10 = False

    def stop(self):
        self.stop_event.set()

    def triggers(self):
        with self.trigger_lock:
            items = list(self.pending)
            self.pending.clear()
        return items

    def run(self):
        while not self.stop_event.is_set():
            f9 = bool(user32.GetAsyncKeyState(VK_F9) & 0x8000)
            f10 = bool(user32.GetAsyncKeyState(VK_F10) & 0x8000)
            if f9 and not self.prev_f9:
                cp = wintypes.POINT()
                cursor = None
                if user32.GetCursorPos(ctypes.byref(cp)):
                    cursor = (cp.x, cp.y)
                with self.trigger_lock:
                    self.pending.append({
                        "time": datetime.now().isoformat(timespec="milliseconds"),
                        "time_epoch": time.time(),
                        "cursor_screen": cursor,
                    })
            if f10 and not self.prev_f10:
                with self.trigger_lock:
                    self.pending.append({"quit": True, "time": datetime.now().isoformat(timespec="milliseconds")})
            self.prev_f9 = f9
            self.prev_f10 = f10
            time.sleep(0.008)

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------


def main():
    print("ROS64 Diagnostic Capture FINAL")
    print("===============================")
    print("READ-ONLY. No process writes.")
    print()
    print("F9 = capture; F10 = quit")
    print("Cursor distance is NOT used to identify an entity.")
    print("All sane cameras are recorded; no camera is chosen by projection count.")
    print("BGA->Model identity is tested by exact pointers and motion correlation.")
    print()

    pid = find_pid()
    if not pid:
        print("ERROR: ros64.exe not found.")
        return 2
    hwnd, title = find_window(pid)
    if not hwnd:
        print("ERROR: ros64.exe window not found.")
        return 2

    reader = ProcessReader(pid)
    try:
        module_base, module_size = module_info(reader)
        print(f"[+] ros64.exe PID: {pid}")
        print(f"[+] module base:  {hex(module_base)}")
        print(f"[+] image size:   {hex(module_size)}")
        print(f"[+] window:       {hex(hwnd)} title={title!r}")

        out_base = Path.cwd() / "diagnostic_capture_FINAL" / datetime.now().strftime("%Y%m%d_%H%M%S")
        writer = CaptureWriter(out_base)
        print(f"[+] output:       {out_base}")
        print()

        tracker = Tracker(reader, module_base)
        keyboard = KeyboardMonitor()
        tracker.start()
        keyboard.start()

        last_status = 0.0
        try:
            while not tracker.stop.is_set():
                now = time.time()
                if now - last_status >= 2.0:
                    state = tracker.snapshot()
                    sane_cams = sum(1 for c in state["cameras"].values() if c.sane)
                    print(
                        f"[{datetime.now().strftime('%H:%M:%S')}] "
                        f"cameras={len(state['cameras'])}/{sane_cams} "
                        f"models={len(state['models'])} BGAs={len(state['bgas'])} "
                        f"history={len(state['history'])} "
                        f"AOI={'OK' if state['aoi_info'] else 'MISS'}"
                    )
                    last_status = now

                for event in keyboard.triggers():
                    if event.get("quit"):
                        tracker.stop.set()
                        break
                    metrics = tracker.snapshot()
                    cursor_screen = event.get("cursor_screen")
                    win = window_metrics(hwnd)
                    cursor_client = None
                    if cursor_screen is not None:
                        cursor_client = (
                            float(cursor_screen[0] - win["origin_x"]),
                            float(cursor_screen[1] - win["origin_y"]),
                        )
                    event["cursor_client"] = cursor_client
                    print(f"[F9] trigger at {event['time']} cursor_client={cursor_client}")
                    writer.write_capture(
                        tracker=tracker,
                        reader=reader,
                        module_base=module_base,
                        module_size=module_size,
                        hwnd=hwnd,
                        trigger=event,
                    )

                time.sleep(0.02)
        finally:
            tracker.stop_and_join()
            keyboard.stop()
            keyboard.join(timeout=1.0)
    finally:
        reader.close()

    print(f"\nEvidence root: {out_base}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nStopped by Ctrl+C.")
