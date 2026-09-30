# Run the Entity -> ModelSkeletal -> SpaceNode diagnostic

This package is a copy of the current working `ROS64Hack` tree with only the targeted evidence diagnostic integrated.

The diagnostic starts automatically with the overlay. There is no hotkey and no source edit required before running it.

## Build

Run from the project directory:

```bat
dotnet clean
dotnet build -c Release
```

Then run:

```bat
bin\Release\net8.0-windows\ROS64Hack.exe
```

## Capture conditions

Use a real in-game match/state where the local player and several `BattleGroundAvatar` objects are present in `avatarInAoI`.

Once the overlay is running, leave it alone for roughly 15–20 seconds. The diagnostic performs one expensive exact-object scan and then 12 continuity samples at one-second intervals.

Moving the local player once during the continuity window is useful, but the diagnostic does not require any special input.

## What to send back

Send the complete console output beginning at:

```text
[ENTITY-MODEL-DIAG] TARGETED ENTITY -> MODEL -> SPACENODE
```

and ending at:

```text
[ENTITY-MODEL-DIAG][FINAL EVIDENCE]
```

Do not trim the model scan, reverse scan, candidate, or continuity sections. Those are the evidence needed to decide the final codebase path.

## Important

The scan is intentionally heavy, but it uses a separate diagnostic timer and quiet `ReadProcessMemory` calls. The normal camera, W2S, ESP, and aimbot paths are not rewritten by the diagnostic.
