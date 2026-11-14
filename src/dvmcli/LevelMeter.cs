// SPDX-License-Identifier: AGPL-3.0-only
/**
* dvmcli - headless Linux proof of concept for the DVM Desktop Dispatch Console.
*
* Audio level meter for the microphone: RMS level with a fast rise and a 20 dB/s fall
* (VU-style ballistics), a peak hold, and a clip indicator. Fed from the capture thread,
* read from the UI thread.
*/

namespace dvmcli
{
    /// <summary>
    /// Thread-safe level meter for 16-bit PCM blocks.
    /// </summary>
    public sealed class LevelMeter
    {
        /// <summary>Bottom of the scale, dBFS.</summary>
        public const double FloorDb = -60.0;
        /// <summary>How fast the level falls back, dB per second.</summary>
        public const double FallDbPerSecond = 20.0;
        /// <summary>How long a peak (and a clip) stays shown.</summary>
        public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.5);
        /// <summary>No audio for this long means the meter is idle.</summary>
        public static readonly TimeSpan IdleTime = TimeSpan.FromMilliseconds(300);
        /// <summary>A sample at or above this magnitude counts as clipping (about -0.2 dBFS).</summary>
        public const int ClipLevel = 32000;

        private readonly object sync = new object();
        private double levelDb = FloorDb;
        private DateTime levelAt;
        private double holdDb = FloorDb;
        private DateTime holdAt;
        private DateTime clipAt = DateTime.MinValue;
        private DateTime lastPush = DateTime.MinValue;

        /// <summary>One reading of the meter.</summary>
        public readonly record struct Reading(bool Active, double LevelDb, double PeakDb, bool Clipping);

        /// <summary>Converts a linear 16-bit magnitude to dBFS, clamped to the floor.</summary>
        public static double ToDb(double magnitude) =>
            magnitude <= 0 ? FloorDb : Math.Max(FloorDb, 20.0 * Math.Log10(magnitude / 32768.0));

        /// <summary>
        /// Adds one block of samples.
        /// </summary>
        public void Push(short[] samples, DateTime? at = null)
        {
            if (samples == null || samples.Length == 0)
                return;

            double sum = 0;
            int peak = 0;
            foreach (short s in samples)
            {
                sum += (double)s * s;
                int a = Math.Abs((int)s);
                if (a > peak) peak = a;
            }
            double rmsDb = ToDb(Math.Sqrt(sum / samples.Length));
            double peakDb = ToDb(peak);
            DateTime now = at ?? DateTime.UtcNow;

            lock (sync)
            {
                double fallen = Decayed(now);
                levelDb = Math.Max(rmsDb, fallen);
                levelAt = now;
                if (peakDb >= holdDb || now - holdAt > HoldTime)
                {
                    holdDb = peakDb;
                    holdAt = now;
                }
                if (peak >= ClipLevel)
                    clipAt = now;
                lastPush = now;
            }
        }

        /// <summary>
        /// Reads the meter.
        /// </summary>
        public Reading Read(DateTime? at = null)
        {
            DateTime now = at ?? DateTime.UtcNow;
            lock (sync)
            {
                bool active = now - lastPush <= IdleTime;
                if (!active)
                    return new Reading(false, FloorDb, FloorDb, false);
                double level = Decayed(now);
                double peak = now - holdAt <= HoldTime ? Math.Max(holdDb, level) : level;
                return new Reading(true, level, peak, now - clipAt <= HoldTime);
            }
        }

        /// <summary>Clears the meter (when the microphone closes).</summary>
        public void Reset()
        {
            lock (sync)
            {
                levelDb = holdDb = FloorDb;
                clipAt = lastPush = DateTime.MinValue;
            }
        }

        private double Decayed(DateTime now) =>
            Math.Max(FloorDb, levelDb - FallDbPerSecond * Math.Max(0, (now - levelAt).TotalSeconds));
    } // public sealed class LevelMeter
}
