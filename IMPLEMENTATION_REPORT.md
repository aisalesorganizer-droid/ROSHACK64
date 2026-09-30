ROS64Hack — Camera Recovery Fix Pass
=====================================

This pass implements only the camera-recovery correction from the approved checklist.
The Shift-lock stabilization work is intentionally NOT changed in this pass.

Implemented
-----------
1. Removed player-distance as an active-camera invalidation condition.
   A valid scoped camera may be far from the player and remains active.

2. Centralized structural camera validation in CameraData.IsStructurallyValid().
   The same rule is used by normal active-camera validation and heap-scan recovery.

3. Structural validation now requires:
   - finite position and focal data
   - sane position bounds
   - YFocal in the shared valid range (>0.3 and <10)
   - Right/Up/Forward vector magnitudes within the existing tolerance
   - pairwise orthogonality (dot products within 0.15)
   - handedness/cross-product consistency (cross(Up,Forward) ≈ Right within 0.20)

4. Removed the recovery-only YFocal <= 6 restriction.
   High-zoom states are now eligible when they satisfy the same shared validity rule.

5. Recovery/selection prefers continuity with the last known-good camera pointer/state.
   Player-to-camera distance is only a secondary selection cue.

6. Existing ADS/focal diagnostics and the previously corrected aimbot/head-targeting code are preserved.

Intentionally unchanged
-----------------------
- World-to-screen projection math
- Camera basis offsets
- Entity discovery / XYZ path
- 1000 m ESP-range setting
- Head/feet ESP projection policy
- Shift-lock activation behavior
- Shift-lock stabilization (reserved for a separate pass)

Validation performed in this environment
-----------------------------------------
- All C# source files inspected.
- Changed source files have balanced structural blocks under lexical brace checking.
- No references remain to FAR_CAM_THRESHOLD, RESCAN_INTERVAL, or _updateCount in Camera.cs.
- Camera.Update() is still called with ADS state from HackOverlay.
- CameraData.IsStructurallyValid() is referenced by both the recovery scanner and CameraData.IsValid.
- Numerical regression: the previously observed invalid candidate with crossErr=1.60214 is rejected; the previously validated camera basis passes with crossErr≈0.000043.

Build note
----------
The container used for this packaging task does not have the .NET SDK installed, so a real `dotnet build` could not be executed here. The source package is prepared for local Release build on the user's Windows development machine.
