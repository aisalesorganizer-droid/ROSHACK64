using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ROS64Hack.Engine;

namespace ROS64Hack.Core
{
    /// <summary>
    /// Runtime-only instrumentation. This file does not alter feature decisions;
    /// it records the data needed to identify which existing gate is failing.
    /// </summary>
    public static class RuntimeDiagnostics
    {
        public const bool Enabled = true;

        private static long _rpmFailures;
        private static long _entityNodes;
        private static long _entityAccepted;
        private static long _entityCoordRejected;
        private static long _entityPtrRejected;
        private static long _entityZeroLike;
        private static long _calibrationCalls;
        private static long _calibrationAmbiguous;
        private static long _cameraValid;
        private static long _cameraInvalid;
        private static long _espCameraRejected;
        private static long _espRangeRejected;
        private static long _w2sCalls;
        private static long _w2sProjected;
        private static long _w2sBehind;
        private static long _w2sBounds;
        private static long _aimbotTicks;
        private static long _aimbotKeyDown;
        private static long _aimbotShiftDown;

        private static long _windowStart = Stopwatch.GetTimestamp();
        private static int _failureSamples;
        private static int _entitySamples;
        private static int _w2sSamples;
        private static int _cameraSamples;
        private static int _localPlayerSamples;
        private static int _espRangeSamples;
        private static readonly object _consoleLock = new();

        public static void ReadFailure(long address, int requested, int actual, int win32Error, string tag)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _rpmFailures);

            lock (_consoleLock)
            {
                if (_failureSamples >= 12) return;
                _failureSamples++;
                Console.WriteLine(
                    $"  [DIAG][RPM-FAIL] {(string.IsNullOrWhiteSpace(tag) ? "untagged" : tag)} " +
                    $"addr=0x{address:X} requested={requested} read={actual} err={win32Error}");
            }
        }

        public static void EntityNode(long node, long entityPtr, ulong obType, float x, float y, float z, uint entityId)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _entityNodes);

            if (entityPtr == 0)
                Interlocked.Increment(ref _entityPtrRejected);

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                Interlocked.Increment(ref _entityCoordRejected);

            if (MathF.Abs(x) < 0.001f && MathF.Abs(y) < 0.001f && MathF.Abs(z) < 0.001f)
                Interlocked.Increment(ref _entityZeroLike);

            if (_entitySamples >= 8) return;
            lock (_consoleLock)
            {
                if (_entitySamples >= 8) return;
                _entitySamples++;
                Console.WriteLine(
                    $"  [DIAG][ENT] node=0x{node:X} ep=0x{entityPtr:X} id={entityId} " +
                    $"type=0x{obType:X} xyz=({x:F3},{y:F3},{z:F3})");
            }
        }

        public static void EntityAccepted() => Interlocked.Increment(ref _entityAccepted);

        public static void Calibration(long entityCount, int distinctTypes, int maxCount, bool accepted)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _calibrationCalls);
            if (distinctTypes > 1 && maxCount <= 1)
            {
                Interlocked.Increment(ref _calibrationAmbiguous);
                Console.WriteLine(
                    $"  [DIAG][CAL] AMBIGUOUS: entities={entityCount} distinctTypes={distinctTypes} " +
                    $"maxCount={maxCount}; current code is selecting types without a unique dominant sample.");
            }
            else if (accepted)
            {
                Console.WriteLine(
                    $"  [DIAG][CAL] selected from entities={entityCount} distinctTypes={distinctTypes} maxCount={maxCount}");
            }
        }

        public static void Camera(long ptr, in CameraData cam, bool valid, string reason)
        {
            if (!Enabled) return;
            if (valid) Interlocked.Increment(ref _cameraValid);
            else Interlocked.Increment(ref _cameraInvalid);

            lock (_consoleLock)
            {
                if (_cameraSamples >= 4) return;
                _cameraSamples++;
                Console.WriteLine(
                    $"  [DIAG][CAM] ptr=0x{ptr:X} valid={valid} reason={reason} " +
                    $"pos=({cam.PosX:F3},{cam.PosY:F3},{cam.PosZ:F3}) " +
                    $"fwd=({cam.FwdX:F4},{cam.FwdY:F4},{cam.FwdZ:F4}) " +
                    $"up=({cam.UpX:F4},{cam.UpY:F4},{cam.UpZ:F4}) " +
                    $"right=({cam.RightX:F4},{cam.RightY:F4},{cam.RightZ:F4}) " +
                    $"focal={cam.YFocal:F5}/{cam.XFocal:F5} ortho={cam.Ortho}");
            }
        }

        public static void LocalPlayer(long ptr, ulong obType, float x, float y, float z)
        {
            if (!Enabled) return;
            lock (_consoleLock)
            {
                if (_localPlayerSamples >= 2) return;
                _localPlayerSamples++;
                Console.WriteLine(
                    $"  [DIAG][PLAYER] ptr=0x{ptr:X} type=0x{obType:X} xyz=({x:F3},{y:F3},{z:F3})");
            }
        }

        public static void EspCameraRejected() => Interlocked.Increment(ref _espCameraRejected);
        public static void EspRangeRejected() => Interlocked.Increment(ref _espRangeRejected);

        public static void EspRangeSample(
            long entityPtr,
            float ex, float ey, float ez,
            in CameraData cam,
            float distance, float maxRange,
            bool rejected)
        {
            if (!Enabled) return;

            lock (_consoleLock)
            {
                if (_espRangeSamples >= 6) return;
                _espRangeSamples++;
                Console.WriteLine(
                    $"  [DIAG][RANGE] ep=0x{entityPtr:X} " +
                    $"entity=({ex:F3},{ey:F3},{ez:F3}) " +
                    $"cam=({cam.PosX:F3},{cam.PosY:F3},{cam.PosZ:F3}) " +
                    $"dist={distance:F3} max={maxRange:F3} " +
                    $"result={(rejected ? "REJECT" : "PASS")}");
            }
        }

        public static void W2S(long entityPtr, float wx, float wy, float wz, float vx, float vy, float vz,
                                float sx, float sy, string reason)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _w2sCalls);
            switch (reason)
            {
                case "PROJECTED":
                    Interlocked.Increment(ref _w2sProjected);
                    break;
                case "BEHIND":
                    Interlocked.Increment(ref _w2sBehind);
                    break;
                case "BOUNDS":
                    Interlocked.Increment(ref _w2sBounds);
                    break;
            }

            if (_w2sSamples >= 10) return;
            lock (_consoleLock)
            {
                if (_w2sSamples >= 10) return;
                _w2sSamples++;
                Console.WriteLine(
                    $"  [DIAG][W2S] ep=0x{entityPtr:X} reason={reason} " +
                    $"world=({wx:F3},{wy:F3},{wz:F3}) view=({vx:F5},{vy:F5},{vz:F5}) " +
                    $"screen=({sx:F2},{sy:F2})");
            }
        }

        public static void Aimbot(bool enabled, System.Windows.Forms.Keys configuredKey, bool keyDown,
                                  bool shiftDown, int entities)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _aimbotTicks);
            if (keyDown) Interlocked.Increment(ref _aimbotKeyDown);
            if (shiftDown) Interlocked.Increment(ref _aimbotShiftDown);

            lock (_consoleLock)
            {
                // One state line per diagnostic window is enough; the counters in the summary
                // carry the complete per-second state information.
                if (_aimbotTicks % 30 == 1)
                {
                    Console.WriteLine(
                        $"  [DIAG][AIM] enabled={enabled} configuredKey={configuredKey} " +
                        $"activationDown={keyDown} shiftDown={shiftDown} entities={entities}");
                }
            }
        }

        private static long Take(ref long value) => Interlocked.Exchange(ref value, 0);

        public static void Frame(in CameraData cam, List<EntityData> entities)
        {
            if (!Enabled) return;

            long now = Stopwatch.GetTimestamp();
            long start = Interlocked.Read(ref _windowStart);
            long elapsed = now - start;
            if (elapsed < Stopwatch.Frequency) return;
            if (Interlocked.CompareExchange(ref _windowStart, now, start) != start) return;

            int visible = 0;
            int enemies = 0;
            foreach (var e in entities)
            {
                if (e.IsEnemy) enemies++;
                if (e.IsEnemy && e.OnScreen) visible++;
            }

            long rpmFail = Take(ref _rpmFailures);
            long nodes = Take(ref _entityNodes);
            long accepted = Take(ref _entityAccepted);
            long coordReject = Take(ref _entityCoordRejected);
            long ptrReject = Take(ref _entityPtrRejected);
            long zeroLike = Take(ref _entityZeroLike);
            long calCalls = Take(ref _calibrationCalls);
            long calAmbiguous = Take(ref _calibrationAmbiguous);
            long camValidReads = Take(ref _cameraValid);
            long camInvalidReads = Take(ref _cameraInvalid);
            long espCamReject = Take(ref _espCameraRejected);
            long espRangeReject = Take(ref _espRangeRejected);
            long w2s = Take(ref _w2sCalls);
            long projected = Take(ref _w2sProjected);
            long behind = Take(ref _w2sBehind);
            long bounds = Take(ref _w2sBounds);
            long aimTicks = Take(ref _aimbotTicks);
            long aimDown = Take(ref _aimbotKeyDown);
            long shiftDown = Take(ref _aimbotShiftDown);

            Console.WriteLine(
                $"  [DIAG][SUMMARY] camValid={cam.IsValid} entities={entities.Count} enemies={enemies} visible={visible} " +
                $"rpmFail/s={rpmFail} nodes/s={nodes} accepted/s={accepted} " +
                $"coordReject/s={coordReject} ptrReject/s={ptrReject} zeroLike/s={zeroLike} " +
                $"calCalls/s={calCalls} calAmbiguous/s={calAmbiguous} " +
                $"camValidReads/s={camValidReads} camInvalidReads/s={camInvalidReads} " +
                $"espCamReject/s={espCamReject} espRangeReject/s={espRangeReject} " +
                $"w2s/s={w2s} projected/s={projected} behind/s={behind} bounds/s={bounds} " +
                $"aimTicks/s={aimTicks} aimDown/s={aimDown} shiftDown/s={shiftDown}");

            lock (_consoleLock)
            {
                _failureSamples = 0;
                _entitySamples = 0;
                _w2sSamples = 0;
                _cameraSamples = 0;
                _localPlayerSamples = 0;
                _espRangeSamples = 0;
            }
        }
    }
}
