
#!/usr/bin/env python3
"""
ROS64 Diagnostic Capture v4
---------------------------

READ-ONLY diagnostic collector for ROS Legacy / ros64.exe.

Purpose:
    Capture the live camera + ModelSkeletal -> SpaceNode -> XYZ chain while
    you are in-game, so we can correlate a visually confirmed character with
    the actual native spatial object that moves with that character.

What it DOES:
    - Finds ros64.exe.
    - Finds its module base.
    - Finds the live Camera object by its known vtable.
    - Finds live ModelSkeletal objects by their known vtable.
    - Validates ModelSkeletal +0x38 -> SpaceNode by SpaceNode vtable.
    - Reads SpaceNode +0x48/+0x4C/+0x50 XYZ.
    - Projects those XYZ values with the currently reconstructed camera transform.
    - Captures game client size, client origin, cursor position.
    - Continuously records candidate positions.
    - F9 creates a synchronized snapshot centered on the current mouse position.
    - F9 records every currently known valid camera candidate and reevaluates
      them against the latest model positions at the trigger moment.
    - Dumps raw memory around the closest candidates in the F9 snapshot.
    - Writes CSV + TXT evidence files.

What it DOES NOT do:
    - Does not write to ros64.exe.
    - Does not change game memory.
    - Does not patch the game.
    - Does not assume the 29-entry 8D099640 table is the player list.
    - Does not assume every ModelSkeletal object is a player/enemy.

Requirements:
    Windows 10/11
    Python 3.10+
    Run from an elevated console if ros64.exe denies VM_READ access.

Controls:
    F9  = focused capture now
    F10 = quit

Output:
    ./diagnostic_capture/<timestamp>/
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
import csv
import math
import os
import struct
import sys
import time
import threading
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
MEM_PRIVATE = 0x20000

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

PTR_MASK = 0xFFFFFFFFFFFFFFFF

# ---------------------------------------------------------------------------
# Known current ROS64 structure data from the reconciled evidence
# ---------------------------------------------------------------------------

# These are RVAs, derived from the previously observed image-relative vtables.
MODEL_SKELETAL_VTABLE_RVA = 0x01C1ACE0
SPACE_NODE_VTABLE_RVA = 0x01C137C8
CAMERA_VTABLE_RVA = 0x01D84DC8

# ModelSkeletal -> SpaceNode
MODEL_SPACENODE_PTR = 0x38

# SpaceNode transform
SN_X = 0x48
SN_Y = 0x4C
SN_Z = 0x50
SN_XYZ_CACHE = 0xC8

# Model metadata pointer worth recording for later ownership analysis
MODEL_META_PTR = 0x30

# Camera structure
CAM_FWD = 0x08
CAM_UP = 0x18
CAM_RIGHT = 0x28
CAM_POS = 0x38
CAM_YFOCAL = 0x9C
CAM_ORTHO = 0x348
CAM_XFOCAL = 0x360

MAX_REGION_READ = 16 * 1024 * 1024
MAX_SCAN_TOTAL_BYTES = 2 * 1024 * 1024 * 1024  # safety cap per scan pass

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

psapi.GetModuleBaseNameW.argtypes = [
    wintypes.HANDLE,
    wintypes.HMODULE,
    wintypes.LPWSTR,
    wintypes.DWORD,
]
psapi.GetModuleBaseNameW.restype = wintypes.DWORD

psapi.GetModuleInformation = getattr(psapi, "GetModuleInformation", None)
if psapi.GetModuleInformation is not None:
    class MODULEINFO(ctypes.Structure):
        _fields_ = [
            ("lpBaseOfDll", ctypes.c_void_p),
            ("SizeOfImage", wintypes.DWORD),
            ("EntryPoint", ctypes.c_void_p),
        ]
    psapi.GetModuleInformation.argtypes = [
        wintypes.HANDLE,
        wintypes.HMODULE,
        ctypes.POINTER(MODULEINFO),
        wintypes.DWORD,
    ]
    psapi.GetModuleInformation.restype = wintypes.BOOL

user32.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
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
# Data structures
# ---------------------------------------------------------------------------

@dataclass
class Camera:
    ptr: int
    pos: tuple[float, float, float]
    fwd: tuple[float, float, float]
    up: tuple[float, float, float]
    right: tuple[float, float, float]
    y_focal: float
    x_focal: float
    ortho: int
    valid: bool
    reason: str

@dataclass
class Candidate:
    model_ptr: int
    model_meta_ptr: int
    space_ptr: int
    xyz: tuple[float, float, float]
    xyz_cache: tuple[float, float, float] | None
    screen: tuple[float, float] | None
    depth: float | None
    screen_distance_to_cursor: float | None

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def hexp(value: int | None) -> str:
    return "0x%016X" % (value or 0)

def finite_triplet(v):
    return all(math.isfinite(float(x)) for x in v)

def dist2(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2

def is_plausible_ptr(p: int) -> bool:
    return 0x10000 <= p <= PTR_MASK and (p & 0x7) == 0

def fmt_xyz(v):
    return "(" + ",".join(f"{float(x):.3f}" for x in v) + ")"

def fmt_screen(v):
    if v is None:
        return "NONE"
    return f"({v[0]:.2f},{v[1]:.2f})"

def read_u64(reader, addr):
    data = reader.read(addr, 8)
    if data is None or len(data) != 8:
        return None
    return struct.unpack_from("<Q", data)[0]

def read_f32(reader, addr):
    data = reader.read(addr, 4)
    if data is None or len(data) != 4:
        return None
    return struct.unpack_from("<f", data)[0]

def read_f32x3(reader, addr):
    data = reader.read(addr, 12)
    if data is None or len(data) != 12:
        return None
    return struct.unpack_from("<fff", data)

# ---------------------------------------------------------------------------
# Process / memory access
# ---------------------------------------------------------------------------

class ProcessReader:
    def __init__(self, pid: int):
        self.pid = pid
        self.handle = kernel32.OpenProcess(
            PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
            False,
            pid,
        )
        if not self.handle:
            err = ctypes.get_last_error()
            raise OSError(f"OpenProcess failed for PID {pid}, Win32 error {err}")

    def close(self):
        if self.handle:
            kernel32.CloseHandle(self.handle)
            self.handle = None

    def read(self, addr: int, size: int) -> bytes | None:
        if size <= 0:
            return b""
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t(0)
        ok = kernel32.ReadProcessMemory(
            self.handle,
            ctypes.c_void_p(addr),
            buf,
            size,
            ctypes.byref(got),
        )
        if not ok or got.value != size:
            return None
        return buf.raw

    def query_regions(self):
        mbi = MEMORY_BASIC_INFORMATION()
        addr = 0
        max_addr = 0x00007FFFFFFFFFFF
        while addr < max_addr:
            ret = kernel32.VirtualQueryEx(
                self.handle,
                ctypes.c_void_p(addr),
                ctypes.byref(mbi),
                ctypes.sizeof(mbi),
            )
            if not ret:
                break

            base = int(mbi.BaseAddress or 0)
            size = int(mbi.RegionSize)

            if (
                size > 0
                and mbi.State == MEM_COMMIT
                and not (mbi.Protect & PAGE_GUARD)
                and (mbi.Protect & 0xFF) in READABLE_PROTECTIONS
                and mbi.Type == MEM_PRIVATE
            ):
                yield base, size, int(mbi.Protect)

            next_addr = base + size
            if next_addr <= addr:
                break
            addr = next_addr

    def scan_u64_value(self, value: int, max_total_bytes=MAX_SCAN_TOTAL_BYTES):
        needle = struct.pack("<Q", value & PTR_MASK)
        total = 0
        hits = set()

        for base, size, _protect in self.query_regions():
            if total >= max_total_bytes:
                break

            remaining = min(size, max_total_bytes - total)
            offset = 0

            while offset < remaining:
                chunk_size = min(MAX_REGION_READ, remaining - offset)
                blob = self.read(base + offset, chunk_size)
                if blob is None:
                    offset += chunk_size
                    total += chunk_size
                    continue

                start = 0
                while True:
                    idx = blob.find(needle, start)
                    if idx < 0:
                        break
                    hit = base + offset + idx
                    hits.add(hit)
                    start = idx + 1

                offset += chunk_size
                total += chunk_size

        return sorted(hits), total

# ---------------------------------------------------------------------------
# Process discovery
# ---------------------------------------------------------------------------

def find_ros64_pid() -> int | None:
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap:
        return None

    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)

        if not kernel32.Process32FirstW(snap, ctypes.byref(entry)):
            return None

        while True:
            name = entry.szExeFile
            if name.lower() == "ros64.exe":
                return int(entry.th32ProcessID)

            if not kernel32.Process32NextW(snap, ctypes.byref(entry)):
                break

        return None
    finally:
        kernel32.CloseHandle(snap)

def find_main_window(pid: int) -> int | None:
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_cb(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True

        proc_id = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(proc_id))
        if proc_id.value != pid:
            return True

        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(max(n + 1, 2))
        user32.GetWindowTextW(hwnd, buf, len(buf))
        title = buf.value.strip()

        if title:
            found.append(hwnd)
            return False

        return True

    user32.EnumWindows(enum_cb, 0)
    return found[0] if found else None

def get_module_info(reader: ProcessReader):
    mods = (wintypes.HMODULE * 1024)()
    needed = wintypes.DWORD(0)

    if not psapi.EnumProcessModules(
        reader.handle,
        mods,
        ctypes.sizeof(mods),
        ctypes.byref(needed),
    ):
        raise OSError(f"EnumProcessModules failed, Win32 error {ctypes.get_last_error()}")

    count = needed.value // ctypes.sizeof(wintypes.HMODULE)

    for i in range(count):
        mod = mods[i]
        name_buf = ctypes.create_unicode_buffer(260)
        psapi.GetModuleBaseNameW(reader.handle, mod, name_buf, 260)
        name = name_buf.value

        if name.lower() == "ros64.exe":
            base = ctypes.cast(mod, ctypes.c_void_p).value
            size = None

            if psapi.GetModuleInformation is not None:
                info = MODULEINFO()
                if psapi.GetModuleInformation(
                    reader.handle,
                    mod,
                    ctypes.byref(info),
                    ctypes.sizeof(info),
                ):
                    size = int(info.SizeOfImage)

            return int(base), size

    raise RuntimeError("ros64.exe module was not found inside the process.")

# ---------------------------------------------------------------------------
# Window / camera / projection
# ---------------------------------------------------------------------------

def get_window_metrics(hwnd):
    if not hwnd:
        return None

    rect = wintypes.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(rect)):
        return None

    origin = wintypes.POINT(0, 0)
    if not user32.ClientToScreen(hwnd, ctypes.byref(origin)):
        return None

    cursor = wintypes.POINT()
    if not user32.GetCursorPos(ctypes.byref(cursor)):
        cursor_pos = None
    else:
        cursor_pos = (float(cursor.x), float(cursor.y))

    width = rect.right - rect.left
    height = rect.bottom - rect.top

    client_cursor = None
    if cursor_pos is not None:
        client_cursor = (
            cursor_pos[0] - float(origin.x),
            cursor_pos[1] - float(origin.y),
        )

    return {
        "client_width": width,
        "client_height": height,
        "client_origin_screen": (origin.x, origin.y),
        "cursor_screen": cursor_pos,
        "cursor_client": client_cursor,
    }

def read_camera(reader: ProcessReader, camera_ptr: int) -> Camera:
    def v(off):
        return read_f32(reader, camera_ptr + off)

    pos = tuple(v(CAM_POS + i * 4) for i in range(3))
    fwd = tuple(v(CAM_FWD + i * 4) for i in range(3))
    up = tuple(v(CAM_UP + i * 4) for i in range(3))
    right = tuple(v(CAM_RIGHT + i * 4) for i in range(3))
    yf = v(CAM_YFOCAL)
    xf = v(CAM_XFOCAL)
    ortho_raw = read_u64(reader, camera_ptr + CAM_ORTHO)

    values = [*pos, *fwd, *up, *right, yf, xf]
    if any(x is None for x in values):
        return Camera(camera_ptr, (0, 0, 0), (0, 0, 0), (0, 0, 0),
                      (0, 0, 0), 0.0, 0.0, 0, False, "read-failure")

    valid = (
        finite_triplet(pos)
        and finite_triplet(fwd)
        and finite_triplet(up)
        and finite_triplet(right)
        and math.isfinite(yf)
        and math.isfinite(xf)
        and 0.1 < yf < 10.0
        and 0.1 < xf < 10.0
    )

    return Camera(
        camera_ptr,
        pos,
        fwd,
        up,
        right,
        float(yf),
        float(xf),
        int(ortho_raw or 0),
        valid,
        "OK" if valid else "invalid-fields",
    )

def _camera_basis_metrics(cam: Camera):
    vectors = [cam.fwd, cam.up, cam.right]
    lengths = [math.sqrt(sum(x * x for x in v)) for v in vectors]
    length_error = sum(abs(x - 1.0) for x in lengths)

    dots = (
        cam.fwd[0] * cam.up[0] + cam.fwd[1] * cam.up[1] + cam.fwd[2] * cam.up[2],
        cam.fwd[0] * cam.right[0] + cam.fwd[1] * cam.right[1] + cam.fwd[2] * cam.right[2],
        cam.up[0] * cam.right[0] + cam.up[1] * cam.right[1] + cam.up[2] * cam.right[2],
    )
    orthogonality_error = sum(abs(x) for x in dots)
    return lengths, dots, length_error, orthogonality_error


def evaluate_camera(cam: Camera, reference_points, window, cursor_override=None):
    """Collect projection evidence without collapsing it into a single winner."""
    evidence = {
        "projected": 0,
        "onscreen": 0,
        "nearest_cursor": None,
        "nearest_model_ptr": None,
    }

    if not reference_points or not window:
        return evidence

    width = int(window.get("client_width") or 0)
    height = int(window.get("client_height") or 0)
    cursor = cursor_override if cursor_override is not None else window.get("cursor_client")
    if width <= 0 or height <= 0:
        return evidence

    nearest = None
    nearest_model = None
    for item in reference_points[:256]:
        if isinstance(item, Candidate):
            world = item.xyz
            model_ptr = item.model_ptr
        else:
            world = item
            model_ptr = None

        screen, depth = project(world, cam, width, height)
        if depth is None or depth <= 0.01 or screen is None:
            continue

        evidence["projected"] += 1
        sx, sy = screen
        if 0.0 <= sx <= float(width) and 0.0 <= sy <= float(height):
            evidence["onscreen"] += 1

        if cursor is not None:
            d = math.sqrt(dist2(screen, cursor))
            if nearest is None or d < nearest:
                nearest = d
                nearest_model = model_ptr

    evidence["nearest_cursor"] = nearest
    evidence["nearest_model_ptr"] = nearest_model
    return evidence


def discover_cameras(reader: ProcessReader, camera_vtable: int):
    """Return all structurally valid camera candidates from the vtable scan."""
    hits, scanned = reader.scan_u64_value(camera_vtable)
    cameras = []
    seen = set()
    for h in hits:
        if h in seen:
            continue
        seen.add(h)
        cam = read_camera(reader, h)
        if cam.valid:
            cameras.append(cam)
    return cameras, hits, scanned


def select_camera(cameras, reference_points=None, window=None):
    """Select a working camera while preserving the full candidate set elsewhere.

    The selector favors live onscreen evidence, then projected evidence, then
    cursor proximity. It does not use the raw 64-bit ORTHO field as evidence.
    """
    if not cameras:
        return None, []

    evaluated = []
    for cam in cameras:
        evidence = evaluate_camera(cam, reference_points, window)
        lengths, dots, length_error, ortho_error = _camera_basis_metrics(cam)
        origin_distance = math.sqrt(sum(x * x for x in cam.pos))
        evaluated.append({
            "camera": cam,
            "evidence": evidence,
            "lengths": lengths,
            "dots": dots,
            "basis_length_error": length_error,
            "basis_orthogonality_error": ortho_error,
            "origin_distance": origin_distance,
        })

    def score(item):
        cam = item["camera"]
        ev = item["evidence"]
        nearest = ev["nearest_cursor"]
        # When model evidence exists, real in-client projections dominate.
        if reference_points and window:
            return (
                -ev["onscreen"],
                -ev["projected"],
                float("inf") if nearest is None else nearest,
                item["basis_length_error"],
                item["basis_orthogonality_error"],
                0 if cam.ortho == 0 else 1,
                0 if item["origin_distance"] >= 1.0 else 1,
            )
        return (
            item["basis_length_error"],
            item["basis_orthogonality_error"],
            0 if cam.ortho == 0 else 1,
            0 if item["origin_distance"] >= 1.0 else 1,
        )

    evaluated.sort(key=score)
    return evaluated[0]["camera"], evaluated


def closest_camera_evidence(evaluated):
    """Return the camera with the smallest trigger-cursor distance, if any.

    This is diagnostic evidence only. It is deliberately not treated as proof
    that the camera is the active render camera.
    """
    candidates = [
        item for item in evaluated
        if item["evidence"]["nearest_cursor"] is not None
    ]
    if not candidates:
        return None
    return min(candidates, key=lambda item: item["evidence"]["nearest_cursor"])


def format_camera_evidence(item, index):
    cam = item["camera"]
    ev = item["evidence"]
    return (
        f"[{index:02d}] ptr={hexp(cam.ptr)} "
        f"pos={fmt_xyz(cam.pos)} "
        f"fwd={fmt_xyz(cam.fwd)} "
        f"up={fmt_xyz(cam.up)} "
        f"right={fmt_xyz(cam.right)} "
        f"yfocal={cam.y_focal:.5f} xfocal={cam.x_focal:.5f} "
        f"projected={ev['projected']} onscreen={ev['onscreen']} "
        f"nearestCursor={'NONE' if ev['nearest_cursor'] is None else f'{ev['nearest_cursor']:.3f}'} "
        f"nearestModel={hexp(ev['nearest_model_ptr'])} "
        f"basisLenErr={item['basis_length_error']:.6f} "
        f"basisOrthoErr={item['basis_orthogonality_error']:.6f} "
        f"ortho_raw={cam.ortho}"
    )

def project(world, cam: Camera, width: int, height: int):
    if not cam.valid or width <= 0 or height <= 0:
        return None, None

    dx = world[0] - cam.pos[0]
    dy = world[1] - cam.pos[1]
    dz = world[2] - cam.pos[2]

    vx = dx * cam.right[0] + dy * cam.right[1] + dz * cam.right[2]
    vy = dx * cam.up[0] + dy * cam.up[1] + dz * cam.up[2]
    vz = dx * cam.fwd[0] + dy * cam.fwd[1] + dz * cam.fwd[2]

    if vz <= 0.01:
        return None, vz

    ar = cam.y_focal / cam.x_focal if abs(cam.x_focal) > 1e-6 else (width / height)

    sx = width * 0.5 * (1.0 + ar * vx / vz)
    sy = height * 0.5 * (1.0 - cam.y_focal * vy / vz)

    return (sx, sy), vz

# ---------------------------------------------------------------------------
# ModelSkeletal discovery
# ---------------------------------------------------------------------------

def collect_models(reader: ProcessReader, module_base: int, window, camera: Camera | None):
    model_vtable = module_base + MODEL_SKELETAL_VTABLE_RVA
    space_vtable = module_base + SPACE_NODE_VTABLE_RVA

    model_hits, scanned = reader.scan_u64_value(model_vtable)

    candidates: list[Candidate] = []

    cursor = window["cursor_client"] if window else None
    width = window["client_width"] if window else 0
    height = window["client_height"] if window else 0

    for model_ptr in model_hits:
        if not is_plausible_ptr(model_ptr):
            continue

        space_ptr = read_u64(reader, model_ptr + MODEL_SPACENODE_PTR)
        if not space_ptr or not is_plausible_ptr(space_ptr):
            continue

        actual_space_vt = read_u64(reader, space_ptr)
        if actual_space_vt != space_vtable:
            continue

        xyz = read_f32x3(reader, space_ptr + SN_X)
        if xyz is None or not finite_triplet(xyz):
            continue

        cache = read_f32x3(reader, space_ptr + SN_XYZ_CACHE)
        if cache is not None and not finite_triplet(cache):
            cache = None

        meta = read_u64(reader, model_ptr + MODEL_META_PTR) or 0

        screen = None
        depth = None
        cursor_dist = None

        if camera is not None and width and height:
            screen, depth = project(xyz, camera, width, height)
            if screen is not None and cursor is not None:
                cursor_dist = math.sqrt(dist2(screen, cursor))

        candidates.append(
            Candidate(
                model_ptr=model_ptr,
                model_meta_ptr=meta,
                space_ptr=space_ptr,
                xyz=(float(xyz[0]), float(xyz[1]), float(xyz[2])),
                xyz_cache=cache,
                screen=screen,
                depth=depth,
                screen_distance_to_cursor=cursor_dist,
            )
        )

    candidates.sort(
        key=lambda c: (
            c.screen_distance_to_cursor
            if c.screen_distance_to_cursor is not None
            else float("inf"),
            c.model_ptr,
        )
    )

    return candidates, model_hits, scanned

# ---------------------------------------------------------------------------
# Evidence writer
# ---------------------------------------------------------------------------

def hexdump(data: bytes, base: int, width=16):
    lines = []
    for i in range(0, len(data), width):
        row = data[i:i+width]
        hexpart = " ".join(f"{b:02X}" for b in row)
        asciipart = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        lines.append(f"{base+i:016X}  {hexpart:<{width*3-1}}  {asciipart}")
    return "\n".join(lines)

class CaptureWriter:
    def __init__(self, out_dir: Path):
        self.out_dir = out_dir
        self.out_dir.mkdir(parents=True, exist_ok=True)

        self.track_csv = self.out_dir / "model_tracks.csv"
        self.capture_index = 0

        if not self.track_csv.exists():
            with self.track_csv.open("w", newline="", encoding="utf-8") as f:
                w = csv.writer(f)
                w.writerow([
                    "timestamp",
                    "model_ptr",
                    "space_ptr",
                    "meta_ptr",
                    "x",
                    "y",
                    "z",
                    "screen_x",
                    "screen_y",
                    "depth",
                    "cursor_distance",
                ])

    def append_tracks(self, candidates):
        ts = datetime.now().isoformat(timespec="milliseconds")
        with self.track_csv.open("a", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            for c in candidates:
                sx, sy = (c.screen if c.screen is not None else (None, None))
                w.writerow([
                    ts,
                    hexp(c.model_ptr),
                    hexp(c.space_ptr),
                    hexp(c.model_meta_ptr),
                    f"{c.xyz[0]:.6f}",
                    f"{c.xyz[1]:.6f}",
                    f"{c.xyz[2]:.6f}",
                    "" if sx is None else f"{sx:.3f}",
                    "" if sy is None else f"{sy:.3f}",
                    "" if c.depth is None else f"{c.depth:.3f}",
                    "" if c.screen_distance_to_cursor is None else f"{c.screen_distance_to_cursor:.3f}",
                ])

    def write_snapshot(
        self,
        reader,
        module_base,
        module_size,
        hwnd,
        window,
        camera,
        candidates,
        camera_evidence=None,
        camera_candidates=None,
        top_raw_count=8,
        trigger_info=None,
        camera_scan_started=None,
        camera_scan_completed=None,
        state_camera_updated=None,
    ):
        self.capture_index += 1
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")[:-3]
        folder = self.out_dir / f"capture_{self.capture_index:03d}_{stamp}"
        folder.mkdir(parents=True, exist_ok=True)

        txt = folder / "snapshot.txt"

        with txt.open("w", encoding="utf-8") as f:
            f.write("ROS64 DIAGNOSTIC SNAPSHOT\n")
            f.write("=" * 78 + "\n")
            f.write(f"time={datetime.now().isoformat(timespec='milliseconds')}\n")
            f.write(f"pid={reader.pid}\n")
            f.write(f"module_base={hexp(module_base)}\n")
            f.write(f"module_size={module_size}\n")
            f.write(f"hwnd={hex(hwnd) if hwnd else 'NONE'}\n")

            if trigger_info:
                f.write("\nF9 TRIGGER\n")
                f.write("-" * 78 + "\n")
                f.write(f"trigger_time={trigger_info.get('time', 'UNKNOWN')}\n")
                f.write(f"cursor_screen_at_trigger={trigger_info.get('cursor_screen')}\n")
                f.write(f"cursor_client_at_trigger={trigger_info.get('cursor_client')}\n")

            if window:
                f.write(
                    "client="
                    f"{window['client_width']}x{window['client_height']} "
                    f"origin_screen={window['client_origin_screen']}\n"
                )
                f.write(f"cursor_screen={window['cursor_screen']}\n")
                f.write(f"cursor_client={window['cursor_client']}\n")
            else:
                f.write("client=UNKNOWN\n")

            if trigger_info:
                f.write(f"camera_state_capture_time={trigger_info.get('camera_state_capture_time', 'UNKNOWN')}\n")
                f.write(f"camera_scan_started={camera_scan_started or 'UNKNOWN'}\n")
                f.write(f"camera_scan_completed={camera_scan_completed or 'UNKNOWN'}\n")
                f.write(f"state_camera_updated={state_camera_updated or 'UNKNOWN'}\n")

            f.write("\nCAMERA SELECTED FOR CAPTURE\n")
            f.write("-" * 78 + "\n")
            f.write("\nCAMERA\n")
            f.write("-" * 78 + "\n")
            if camera is None:
                f.write("camera=NOT_FOUND\n")
            else:
                f.write(f"ptr={hexp(camera.ptr)} valid={camera.valid} reason={camera.reason}\n")
                f.write(f"pos={fmt_xyz(camera.pos)}\n")
                f.write(f"fwd={fmt_xyz(camera.fwd)}\n")
                f.write(f"up={fmt_xyz(camera.up)}\n")
                f.write(f"right={fmt_xyz(camera.right)}\n")
                f.write(f"yfocal={camera.y_focal:.7f}\n")
                f.write(f"xfocal={camera.x_focal:.7f}\n")
                f.write(f"ortho={camera.ortho}\n")

            f.write("\nALL CAMERA CANDIDATES AT CAPTURE\n")
            f.write("-" * 78 + "\n")
            f.write(f"count={len(camera_candidates or [])}\n")
            if camera_evidence:
                for i, item in enumerate(camera_evidence):
                    f.write(format_camera_evidence(item, i) + "\n")
            else:
                f.write("camera_evidence=NONE\n")

            f.write("\nMODELSKELETAL CANDIDATES\n")
            f.write("-" * 78 + "\n")
            f.write(f"count={len(candidates)}\n\n")

            for i, c in enumerate(candidates):
                f.write(
                    f"[{i:03d}] model={hexp(c.model_ptr)} "
                    f"meta={hexp(c.model_meta_ptr)} "
                    f"space={hexp(c.space_ptr)} "
                    f"xyz={fmt_xyz(c.xyz)} "
                    f"cache={fmt_xyz(c.xyz_cache) if c.xyz_cache else 'NONE'} "
                    f"screen={fmt_screen(c.screen)} "
                    f"depth={'NONE' if c.depth is None else f'{c.depth:.3f}'} "
                    f"cursor_dist={'NONE' if c.screen_distance_to_cursor is None else f'{c.screen_distance_to_cursor:.3f}'}\n"
                )

        # Raw dumps around the most relevant candidates.
        for i, c in enumerate(candidates[:top_raw_count]):
            model_blob = reader.read(c.model_ptr, 0x120)
            if model_blob is not None:
                (folder / f"candidate_{i:03d}_ModelSkeletal.txt").write_text(
                    hexdump(model_blob, c.model_ptr),
                    encoding="utf-8",
                )

            space_blob = reader.read(c.space_ptr, 0xE0)
            if space_blob is not None:
                (folder / f"candidate_{i:03d}_SpaceNode.txt").write_text(
                    hexdump(space_blob, c.space_ptr),
                    encoding="utf-8",
                )

        return folder

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def key_down(vk):
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)

def wait_key_release(vk):
    while key_down(vk):
        time.sleep(0.03)


class SharedState:
    def __init__(self):
        self.lock = threading.Lock()
        self.camera = None
        self.camera_candidates = []
        self.camera_evidence = []
        self.candidates = []
        self.window = None
        self.last_model_scan = 0.0
        self.last_camera_scan = 0.0
        self.model_scan_busy = False
        self.camera_scan_busy = False
        self.camera_scan_started = None
        self.camera_scan_completed = None
        self.camera_state_updated = None
        self.started = time.time()

    def copy(self):
        with self.lock:
            return (
                self.camera,
                list(self.camera_candidates),
                list(self.camera_evidence),
                list(self.candidates),
                dict(self.window) if self.window else None,
                self.last_model_scan,
                self.last_camera_scan,
                self.model_scan_busy,
                self.camera_scan_busy,
            )


class KeyboardMonitor(threading.Thread):
    """
    Dedicated keyboard watcher.

    The previous implementation checked F9 from the same loop that performs
    multi-GB process scans. That allowed F9 presses to be missed for seconds.
    This thread polls independently and records the cursor position at the
    exact F9 edge.
    """
    daemon = True

    def __init__(self, hwnd, state, capture_event, quit_event):
        super().__init__(name="ROS64-Hotkeys")
        self.hwnd = hwnd
        self.state = state
        self.capture_event = capture_event
        self.quit_event = quit_event
        self.latest_trigger = None
        self.lock = threading.Lock()
        self.last_f9 = False
        self.last_f10 = False

    def _capture_cursor(self):
        screen = wintypes.POINT()
        if not user32.GetCursorPos(ctypes.byref(screen)):
            return None, None

        screen_pos = (float(screen.x), float(screen.y))

        window = self.hwnd
        if not window:
            return screen_pos, None

        origin = wintypes.POINT(0, 0)
        if not user32.ClientToScreen(window, ctypes.byref(origin)):
            return screen_pos, None

        return screen_pos, (
            screen_pos[0] - float(origin.x),
            screen_pos[1] - float(origin.y),
        )

    def run(self):
        while not self.quit_event.is_set():
            f9 = key_down(VK_F9)
            f10 = key_down(VK_F10)

            if f10 and not self.last_f10:
                self.quit_event.set()

            if f9 and not self.last_f9:
                screen_pos, client_pos = self._capture_cursor()
                trigger = {
                    "time": datetime.now().isoformat(timespec="milliseconds"),
                    "cursor_screen": screen_pos,
                    "cursor_client": client_pos,
                }
                with self.lock:
                    self.latest_trigger = trigger
                self.capture_event.set()

            self.last_f9 = f9
            self.last_f10 = f10
            time.sleep(0.005)

    def take_trigger(self):
        with self.lock:
            trigger = self.latest_trigger
            self.latest_trigger = None
            return trigger


def refresh_candidates(reader, module_base, window, camera, previous):
    """
    Cheap live reread of already-discovered ModelSkeletal objects.
    No process-wide scan occurs here.
    """
    if not previous:
        return []

    space_vtable = module_base + SPACE_NODE_VTABLE_RVA
    cursor = window["cursor_client"] if window else None
    width = window["client_width"] if window else 0
    height = window["client_height"] if window else 0

    refreshed = []

    for old in previous:
        model_ptr = old.model_ptr

        space_ptr = read_u64(reader, model_ptr + MODEL_SPACENODE_PTR)
        if not space_ptr or not is_plausible_ptr(space_ptr):
            continue

        if read_u64(reader, space_ptr) != space_vtable:
            continue

        xyz = read_f32x3(reader, space_ptr + SN_X)
        if xyz is None or not finite_triplet(xyz):
            continue

        cache = read_f32x3(reader, space_ptr + SN_XYZ_CACHE)
        if cache is not None and not finite_triplet(cache):
            cache = None

        screen = None
        depth = None
        cursor_dist = None

        if camera is not None and width and height:
            screen, depth = project(xyz, camera, width, height)
            if screen is not None and cursor is not None:
                cursor_dist = math.sqrt(dist2(screen, cursor))

        refreshed.append(
            Candidate(
                model_ptr=model_ptr,
                model_meta_ptr=read_u64(reader, model_ptr + MODEL_META_PTR) or 0,
                space_ptr=space_ptr,
                xyz=(float(xyz[0]), float(xyz[1]), float(xyz[2])),
                xyz_cache=cache,
                screen=screen,
                depth=depth,
                screen_distance_to_cursor=cursor_dist,
            )
        )

    refreshed.sort(
        key=lambda c: (
            c.screen_distance_to_cursor
            if c.screen_distance_to_cursor is not None
            else float("inf"),
            c.model_ptr,
        )
    )
    return refreshed


def main():
    print("ROS64 Diagnostic Capture v4")
    print("===========================")
    print("READ-ONLY. No process writes.")
    print()
    print("F9 detection runs on a dedicated keyboard thread.")
    print("Camera selection uses live projection evidence when models are available.")
    print("F9 records ALL known camera candidates and reevaluates them at trigger time.")
    print("Background discovery/tracking keeps running independently.")
    print()

    pid = find_ros64_pid()
    if pid is None:
        print("ERROR: ros64.exe is not running.")
        return 2

    print(f"[+] ros64.exe PID: {pid}")

    reader = ProcessReader(pid)
    writer = CaptureWriter(
        Path.cwd() / "diagnostic_capture" / datetime.now().strftime("%Y%m%d_%H%M%S")
    )

    try:
        module_base, module_size = get_module_info(reader)
        print(f"[+] module base: {hexp(module_base)}")
        if module_size:
            print(f"[+] image size:   0x{module_size:X}")

        hwnd = find_main_window(pid)
        print(f"[+] window:       {hex(hwnd) if hwnd else 'NOT FOUND'}")
        print(f"[+] output:       {writer.out_dir}")

        state = SharedState()
        capture_event = threading.Event()
        quit_event = threading.Event()

        keyboard = KeyboardMonitor(
            hwnd=hwnd,
            state=state,
            capture_event=capture_event,
            quit_event=quit_event,
        )
        keyboard.start()

        camera_vtable = module_base + CAMERA_VTABLE_RVA
        stop_workers = threading.Event()

        def discovery_worker():
            next_camera = 0.0
            next_models = 0.0

            while not stop_workers.is_set() and not quit_event.is_set():
                now = time.time()

                if now >= next_camera:
                    camera_scan_started = datetime.now().isoformat(timespec="milliseconds")
                    with state.lock:
                        state.camera_scan_busy = True
                        reference_points = list(state.candidates)
                        camera_window = dict(state.window) if state.window else None
                    try:
                        cameras, hits, scanned = discover_cameras(reader, camera_vtable)
                        camera, evaluated = select_camera(
                            cameras,
                            reference_points=reference_points,
                            window=camera_window,
                        )
                        camera_scan_completed = datetime.now().isoformat(timespec="milliseconds")
                        with state.lock:
                            state.camera = camera
                            state.camera_candidates = cameras
                            state.camera_evidence = evaluated
                            state.last_camera_scan = time.time()
                            state.camera_scan_started = camera_scan_started
                            state.camera_scan_completed = camera_scan_completed
                            state.camera_state_updated = datetime.now().isoformat(timespec="milliseconds")
                    finally:
                        with state.lock:
                            state.camera_scan_busy = False
                    next_camera = time.time() + 5.0

                    if camera:
                        selected_item = next((x for x in evaluated if x['camera'].ptr == camera.ptr), None)
                        sev = selected_item['evidence'] if selected_item else {}
                        print(
                            f"[DISCOVERY][CAM] selected={hexp(camera.ptr)} "
                            f"validCandidates={len(cameras)} hits={len(hits)} scanned={scanned:,} "
                            f"projected={sev.get('projected', 0)} "
                            f"onscreen={sev.get('onscreen', 0)} "
                            f"nearestCursor={sev.get('nearest_cursor')}"
                        )
                        for i, item in enumerate(evaluated[:12]):
                            print("    " + format_camera_evidence(item, i))
                    else:
                        print("[DISCOVERY][CAM] no valid camera found")

                if now >= next_models:
                    with state.lock:
                        state.model_scan_busy = True
                        cam_for_scan = state.camera
                    try:
                        window = get_window_metrics(hwnd)
                        models, hits, scanned = collect_models(
                            reader, module_base, window, cam_for_scan
                        )
                        with state.lock:
                            state.candidates = models
                            state.window = window
                            state.last_model_scan = time.time()
                    finally:
                        with state.lock:
                            state.model_scan_busy = False

                    print(
                        f"[DISCOVERY][MODEL] models={len(models)} "
                        f"vtableHits={len(hits)} scanned={scanned:,}"
                    )
                    next_models = time.time() + 15.0

                time.sleep(0.02)

        def tracking_worker():
            next_track = 0.0
            while not stop_workers.is_set() and not quit_event.is_set():
                now = time.time()
                if now >= next_track:
                    window = get_window_metrics(hwnd)

                    with state.lock:
                        camera = state.camera
                        previous = list(state.candidates)

                    # Re-read camera fields from the known current pointer.
                    live_camera = camera
                    if camera is not None:
                        live_camera = read_camera(reader, camera.ptr)
                        if not live_camera.valid:
                            live_camera = camera

                    live_candidates = refresh_candidates(
                        reader,
                        module_base,
                        window,
                        live_camera,
                        previous,
                    )

                    with state.lock:
                        state.camera = live_camera
                        state.window = window
                        state.candidates = live_candidates

                    next_track = now + 0.10

                time.sleep(0.01)

        t_discovery = threading.Thread(
            target=discovery_worker,
            name="ROS64-Discovery",
            daemon=True,
        )
        t_tracking = threading.Thread(
            target=tracking_worker,
            name="ROS64-Tracking",
            daemon=True,
        )

        t_discovery.start()
        t_tracking.start()

        print()
        print("LIVE")
        print("  F9  = focused capture")
        print("  F10 = quit")
        print()

        last_status = 0.0

        while not quit_event.is_set():
            now = time.time()

            if now - last_status >= 2.0:
                with state.lock:
                    camera = state.camera
                    candidates = list(state.candidates)
                    window = dict(state.window) if state.window else None
                    cam_busy = state.camera_scan_busy
                    model_busy = state.model_scan_busy

                focused = [
                    c for c in candidates
                    if c.screen_distance_to_cursor is not None
                ]

                focused_text = (
                    f" nearest={focused[0].screen_distance_to_cursor:.1f}px "
                    f"model={hexp(focused[0].model_ptr)} "
                    f"screen={fmt_screen(focused[0].screen)}"
                    if focused else
                    " nearest=NONE"
                )

                print(
                    f"[{datetime.now().strftime('%H:%M:%S')}] "
                    f"camera={'OK' if camera and camera.valid else 'MISS'} "
                    f"models={len(candidates)} "
                    f"camScan={'BUSY' if cam_busy else 'idle'} "
                    f"modelScan={'BUSY' if model_busy else 'idle'}"
                    f"{focused_text}"
                )
                last_status = now

            if capture_event.is_set():
                capture_event.clear()
                trigger = keyboard.take_trigger()
                capture_time = datetime.now().isoformat(timespec="milliseconds")

                with state.lock:
                    selected_camera = state.camera
                    known_camera_ptrs = [c.ptr for c in state.camera_candidates]
                    previous_evidence = list(state.camera_evidence)
                    candidates = list(state.candidates)
                    window = dict(state.window) if state.window else None
                    cam_scan_started = state.camera_scan_started
                    cam_scan_completed = state.camera_scan_completed
                    cam_state_updated = state.camera_state_updated

                # Re-read every camera address from the most recent camera scan.
                # This is a bounded set of known pointers, not a multi-GB scan.
                fresh_cameras = []
                for ptr in known_camera_ptrs:
                    cam = read_camera(reader, ptr)
                    if cam.valid:
                        fresh_cameras.append(cam)

                cursor_at_trigger = trigger.get("cursor_client") if trigger else None
                _, fresh_evidence = select_camera(
                    fresh_cameras,
                    reference_points=candidates,
                    window=window,
                )

                # IMPORTANT: F9 does not silently replace the tracked camera.
                # The purpose of this capture is to compare ALL camera candidates
                # at the exact trigger moment. The tracked camera remains the
                # camera used for the primary model reprojection.
                camera = selected_camera

                # Re-read the selected camera one more time so the snapshot's
                # primary camera fields are also trigger-time values.
                if camera is not None:
                    live_selected = read_camera(reader, camera.ptr)
                    if live_selected.valid:
                        camera = live_selected

                # Reproject candidates using the trigger-time cursor and camera.
                # This prevents the F9 snapshot from mixing stale screen coordinates
                # with a newly read camera state.
                trigger_candidates = refresh_candidates(
                    reader,
                    module_base,
                    {**window, "cursor_client": cursor_at_trigger} if window else window,
                    camera,
                    candidates,
                )

                if trigger:
                    trigger["camera_state_capture_time"] = capture_time

                folder = writer.write_snapshot(
                    reader=reader,
                    module_base=module_base,
                    module_size=module_size,
                    hwnd=hwnd,
                    window={**window, "cursor_client": cursor_at_trigger} if window else window,
                    camera=camera,
                    candidates=trigger_candidates,
                    camera_evidence=fresh_evidence or previous_evidence,
                    camera_candidates=fresh_cameras if fresh_cameras else [
                        item["camera"] for item in previous_evidence
                    ],
                    top_raw_count=8,
                    trigger_info=trigger,
                    camera_scan_started=cam_scan_started,
                    camera_scan_completed=cam_scan_completed,
                    state_camera_updated=cam_state_updated,
                )

                writer.append_tracks(trigger_candidates)

                print()
                print(f"[!!!] F9 CAPTURE WRITTEN: {folder}")
                print(
                    f"     trigger cursor client="
                    f"{trigger.get('cursor_client') if trigger else 'UNKNOWN'}"
                )
                print(f"     trigger_time={trigger.get('time') if trigger else 'UNKNOWN'}")
                print(f"     knownCameraCandidates={len(fresh_cameras)}")
                print(f"     trackedCameraAtF9={hexp(camera.ptr) if camera else 'NONE'}")

                evaluated_now = fresh_evidence or previous_evidence
                selected_item = next((x for x in evaluated_now if x['camera'].ptr == camera.ptr), None)
                if selected_item:
                    print(
                        f"     trackedEvidence projected={selected_item['evidence']['projected']} "
                        f"onscreen={selected_item['evidence']['onscreen']} "
                        f"nearestCursor={selected_item['evidence']['nearest_cursor']}"
                    )

                closest_item = closest_camera_evidence(evaluated_now)
                if closest_item:
                    cc = closest_item['camera']
                    cev = closest_item['evidence']
                    print(
                        f"     closestCursorCamera={hexp(cc.ptr)} "
                        f"distance={cev['nearest_cursor']:.3f} "
                        f"model={hexp(cev['nearest_model_ptr'])} "
                        f"projected={cev['projected']} onscreen={cev['onscreen']}"
                    )
                else:
                    print("     closestCursorCamera=NONE")

                print(f"     candidates={len(trigger_candidates)}")
                for i, c in enumerate(trigger_candidates[:12]):
                    print(
                        f"     #{i:02d} "
                        f"model={hexp(c.model_ptr)} "
                        f"space={hexp(c.space_ptr)} "
                        f"xyz={fmt_xyz(c.xyz)} "
                        f"screen={fmt_screen(c.screen)} "
                        f"cursorDist={c.screen_distance_to_cursor}"
                    )
                print()

            time.sleep(0.02)

        stop_workers.set()
        t_discovery.join(timeout=1.0)
        t_tracking.join(timeout=1.0)
        keyboard.join(timeout=1.0)

    finally:
        reader.close()

    print(f"\nEvidence is in: {writer.out_dir}")
    return 0

if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nStopped by Ctrl+C.")
