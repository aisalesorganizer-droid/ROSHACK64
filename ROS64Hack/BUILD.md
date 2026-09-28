# ROS64 Hack — Build & Run Guide

## Requirements
- .NET 6.0 SDK (x64) — https://dotnet.microsoft.com/download/dotnet/6.0
- Windows 10/11 x64
- Run as Administrator (required for OpenProcess + ReadProcessMemory)

## Build

```cmd
cd ROS64Hack
dotnet build -c Release
```

Output: `bin\Release\net6.0-windows\ROS64Hack.exe`

Or publish as single self-contained exe:
```cmd
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Run

1. Start ros64.exe (via Myth Games Launcher) and enter a match
2. Run ROS64Hack.exe **as Administrator**
3. Overlay attaches automatically — waits if game not yet open
4. Press **INSERT** to show/hide the menu
5. Press **DELETE** to exit

## Hotkeys

| Key    | Action              |
|--------|---------------------|
| INSERT | Toggle menu on/off  |
| DELETE | Exit cheat          |
| RMB    | Aimbot (hold)       |
| W/A/S/D + SPACE/CTRL | NoClip movement (when enabled) |

## File Map

```
Core/
  Mem.cs           64-bit RPM/WPM wrapper
  Offsets.cs       All proven image-relative offsets from RE session
  WorldToScreen.cs Dot-product W2S formula (replaces old 4×4 matrix)

Engine/
  Camera.cs        Camera acquisition (static chain + heap-scan fallback)
  EntityData.cs    Entity snapshot struct
  EntityLoop.cs    RB-tree walker + dynamic ob_type calibration
  PlayerReader.cs  Local player fast-path via EM+0x10F8

Features/
  Settings.cs      All feature toggles (shared state)
  ESP.cs           Box / line / health / distance drawing
  Aimbot.cs        Target selection + smooth mouse movement
  NoClip.cs        WPM-based position writing

Overlay/
  HackOverlay.cs   Transparent GDI+ form, drives read+draw loops
  MenuForm.cs      Tab menu (VISUAL / AIMBOT / NOCLIP / Misc)
```

## Known Gaps

| Item           | Status | Notes |
|----------------|--------|-------|
| Health offset  | ❌ Not found | Shows "??" in ESP; BGA+0x4C is candidate |
| Entity names   | ❌ Not found | Shows "Player" for all enemies |
| Item ESP       | 🔲 Stub | Toggle exists, logic not yet wired |
| Vehicle ESP    | 🔲 Stub | Toggle exists, logic not yet wired |

## If ob_type calibration fails

The first call to `EntityLoop.Calibrate()` reads the local player's ob_type
from the controlledEntities fast-path, then auto-discovers the enemy ob_type
from the all-entities tree. If this produces 0 entities:

1. Open WinDbg attached to ros64.exe
2. Dump `[0x1431F53D8]` to get live EM ptr
3. Dump `em_ptr + 0x10F8` to get controlledEntities sentinel
4. Walk one level: `sentinel + 0x08` → root node → `node + 0x20` → PBGA ptr
5. Read `PBGA + 0x08` → new ob_type value
6. Update `Offsets.OB_TYPE_PBGA_FALLBACK` and `OB_TYPE_BGA_FALLBACK`

## Re-scan after game update

All addresses in `Offsets.cs` are **image-relative** (calculated as `absolute_VA - 0x140000000`).
If the game binary changes, re-run the RE process starting from:
- `PyEval_EvalFrameEx` export string scan for the new EM constructor
- Camera vtable scan for `0x141D84DC8` (update if changed)
