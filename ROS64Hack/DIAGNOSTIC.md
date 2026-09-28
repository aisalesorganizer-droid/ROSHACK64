# ROS64Hack diagnostic build

This is a separate copy of the project. The normal source archive is not modified.

## Run

From this extracted folder:

```cmd
dotnet build -c Release && bin\Release\net8.0-windows\ROS64Hack.exe
```

Start `ros64.exe`, enter the same session where ESP/aimbot currently fails, then let the overlay run for about 10 seconds. Do not change offsets or source code during the run.

The console now reports:

- `[DIAG][RPM-FAIL]` — failed `ReadProcessMemory` calls with address, requested/actual bytes, Win32 error, and field tag.
- `[DIAG][CAM]` — actual camera values and the exact current validity reason.
- `[DIAG][ENT]` — sampled RB-tree entity node, entity pointer, entity ID, `ob_type`, and XYZ.
- `[DIAG][CAL]` — whether type calibration is statistically ambiguous.
- `[DIAG][W2S]` — projected/behind/bounds rejection with world/view/screen values.
- `[DIAG][AIM]` — configured key plus live RMB/Shift state.
- `[DIAG][SUMMARY]` — one-second aggregate counts (`/s`) for the complete pipeline.

Send the console output from the first camera acquisition through several `[DIAG][SUMMARY]` lines. No offsets should be changed before that output is analyzed.
