ROS64Hack — Scope/Focal Diagnostic + Precise Head Aim

Base: ROS64Hack_scope_diagnostic_v2.zip

Changes in this package:
1. Engine/Camera.cs
   - Keeps existing ADS diagnostics and camera-selection behavior unchanged.
   - On ADS transition/selection/rescan diagnostic events only, reads camera +0x300..+0x3FF.
   - Logs plausible float candidates in [0.5, 12.0] with YFocal/value ratio to identify the live horizontal focal value.
   - This probe is not used by W2S or camera selection.
2. Features/Aimbot.cs
   - Head point now uses the verified ESP head height of +21.5 raw world units.
   - Head mode targets the exact projected head point; the old 10% torso blend is removed.
   - Head mode requires a valid head projection.
   - Aim response is clamped to 0.01..1.0; default is 0.85.
   - Sub-pixel correction prevents integer truncation from stalling just short of the head.
3. Features/Settings.cs
   - Default AimbotSmoothing changed from 0.15 to 0.85 for tighter head tracking.
4. Overlay/MenuForm.cs
   - Slider label now reads AIM RESPONSE to match the behavior (higher = tighter/faster).

Intentionally unchanged:
- WorldToScreen mathematics.
- Camera basis/offsets.
- FAR_CAM_THRESHOLD distance-based recovery has been removed; recovery now occurs only on structural camera invalidity.
- Entity enumeration.
- Normal read/draw architecture.

Build note: this environment has no .NET SDK, so only structural/static validation was possible here.


## Camera-recovery fix implemented
- Player-to-camera distance no longer invalidates the active camera.
- Active camera recovery occurs only on failed/structurally-invalid reads.
- Recovery candidates use the same orthonormal-basis validation as normal camera reads.
- Recovery no longer applies the old YFocal <= 6 cutoff; valid scoped values up to the shared <10 rule are eligible.
- Recovery selection prefers continuity with the last known-good camera pointer/state; player distance is secondary.
- W2S and ESP mathematics were not changed in this pass.
