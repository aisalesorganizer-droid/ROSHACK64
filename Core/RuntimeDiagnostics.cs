using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ROS64Hack.Engine;

namespace ROS64Hack.Core
{
    /// <summary>
    /// Low-overhead runtime instrumentation. Hot-path methods only increment counters.
    /// One compact summary is emitted approximately once per second.
    /// </summary>
    public static class RuntimeDiagnostics
    {
        public const bool Enabled = true;

        private static long _rpmCalls;
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
        private static long _readLoops;
        private static long _drawLoops;
        private static long _readSkips;
        private static long _drawSkips;
        private static long _readTimeUs;
        private static long _drawTimeUs;

        private static long _windowStart = Stopwatch.GetTimestamp();

        public static void RpmCall()
        {
            if (Enabled) Interlocked.Increment(ref _rpmCalls);
        }

        public static void ReadFailure(long address, int requested, int actual, int win32Error, string tag)
        {
            if (Enabled) Interlocked.Increment(ref _rpmFailures);
        }

        public static void EntityNode(long node, long entityPtr, ulong obType, float x, float y, float z)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _entityNodes);

            if (entityPtr == 0)
                Interlocked.Increment(ref _entityPtrRejected);

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                Interlocked.Increment(ref _entityCoordRejected);

            if (MathF.Abs(x) < 0.001f && MathF.Abs(y) < 0.001f && MathF.Abs(z) < 0.001f)
                Interlocked.Increment(ref _entityZeroLike);
        }

        public static void EntityAccepted() => Interlocked.Increment(ref _entityAccepted);

        public static void Calibration(long entityCount, int distinctTypes, int maxCount, bool accepted)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _calibrationCalls);
            if (distinctTypes > 1 && maxCount <= 1)
                Interlocked.Increment(ref _calibrationAmbiguous);
        }

        public static void Camera(long ptr, in CameraData cam, bool valid, string reason)
        {
            if (!Enabled) return;
            if (valid) Interlocked.Increment(ref _cameraValid);
            else Interlocked.Increment(ref _cameraInvalid);
        }

        public static void LocalPlayer(long ptr, ulong obType, float x, float y, float z)
        {
            // Kept for API compatibility; local-player samples are no longer printed.
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
            // Range samples are intentionally silent; the aggregate reject counter is retained.
        }

        public static void W2S(long entityPtr, float wx, float wy, float wz, float vx, float vy, float vz,
                                float sx, float sy, string reason)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _w2sCalls);
            switch (reason)
            {
                case "PROJECTED": Interlocked.Increment(ref _w2sProjected); break;
                case "BEHIND":    Interlocked.Increment(ref _w2sBehind); break;
                case "BOUNDS":    Interlocked.Increment(ref _w2sBounds); break;
            }
        }

        public static void Aimbot(bool enabled, System.Windows.Forms.Keys configuredKey, bool keyDown,
                                  bool shiftDown, int entities)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _aimbotTicks);
            if (keyDown) Interlocked.Increment(ref _aimbotKeyDown);
            if (shiftDown) Interlocked.Increment(ref _aimbotShiftDown);
        }

        public static void ReadLoopSkipped() => Interlocked.Increment(ref _readSkips);
        public static void DrawLoopSkipped() => Interlocked.Increment(ref _drawSkips);

        public static void ReadLoopCompleted(double elapsedMs)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _readLoops);
            Interlocked.Add(ref _readTimeUs, (long)Math.Max(0, elapsedMs * 1000.0));
        }

        public static void DrawLoopCompleted(double elapsedMs)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _drawLoops);
            Interlocked.Add(ref _drawTimeUs, (long)Math.Max(0, elapsedMs * 1000.0));
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

            long rpmCalls = Take(ref _rpmCalls);
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
            long readLoops = Take(ref _readLoops);
            long drawLoops = Take(ref _drawLoops);
            long readSkips = Take(ref _readSkips);
            long drawSkips = Take(ref _drawSkips);
            long readTimeUs = Take(ref _readTimeUs);
            long drawTimeUs = Take(ref _drawTimeUs);

            double avgReadMs = readLoops > 0 ? readTimeUs / 1000.0 / readLoops : 0.0;
            double avgDrawMs = drawLoops > 0 ? drawTimeUs / 1000.0 / drawLoops : 0.0;

            Console.WriteLine(
                $"  [DIAG][SUMMARY] read/s={readLoops} draw/s={drawLoops} " +
                $"readSkip/s={readSkips} drawSkip/s={drawSkips} " +
                $"avgReadMs={avgReadMs:F2} avgDrawMs={avgDrawMs:F2} " +
                $"rpm/s={rpmCalls} rpmFail/s={rpmFail} " +
                $"cam/s={camValidReads} camBad/s={camInvalidReads} " +
                $"entities={entities.Count} enemies={enemies} visible={visible} " +
                $"nodes/s={nodes} accepted/s={accepted} " +
                $"w2s/s={w2s} projected/s={projected} behind/s={behind} bounds/s={bounds} " +
                $"rangeReject/s={espRangeReject} aim/s={aimTicks} shift/s={shiftDown}");
        }
    }
}
