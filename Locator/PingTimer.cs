using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SpawnLocator
{
    // The relic re-announces itself on a fixed cadence (roughly 15-20s). This runs that
    // cadence as a loop: the countdown refills the moment it empties and the loop counter
    // ticks up, so you can see both how long until the next sound and how many have gone by.
    //
    // The countdown is drawn by a background thread at the right-hand edge of whatever line
    // the cursor is on, then the cursor is put straight back - so it never disturbs typing.
    // The window title mirrors it, which keeps working even when the console is too narrow
    // or output is redirected.
    class PingTimer
    {
        const double DefaultInterval = 20.0;
        const int CalibrationWindow = 6;

        // Absolute floor, so a double-tap of Enter can't be read as a real cycle.
        const double MinGap = 1.0;

        // A measured cycle is only believed if it lands within this window of the interval
        // we already hold. Relative rather than fixed seconds, so 'starttimer 2' calibrates
        // just as well as the stock 20s does.
        const double CalibLow = 0.4;
        const double CalibHigh = 2.5;

        // A ping landing this early into a cycle is the same sound the auto-rollover just
        // counted, not a new one - don't tally it twice.
        const double SameSoundFraction = 0.15;

        // Loops without a confirming ping before the display admits it may have drifted.
        const int DriftLoops = 3;

        readonly object gate = new object();
        readonly List<double> gaps = new List<double>();

        double interval = DefaultInterval;
        DateTime anchor;        // start of the cycle currently counting down
        DateTime lastPing;      // last sound you actually confirmed, for calibration
        DateTime startedAt;
        long loops;             // completed cycles since 'starttimer'
        long loopsAtLastPing;
        bool running;
        Thread? thread;
        int lastDrawWidth;

        public bool Running { get { lock (gate) return running; } }
        public long Loops { get { lock (gate) return loops; } }

        public void Start(double? explicitInterval)
        {
            lock (gate)
            {
                if (explicitInterval.HasValue)
                {
                    interval = explicitInterval.Value;
                    gaps.Clear();
                }
                var now = DateTime.UtcNow;
                anchor = now;
                lastPing = now;
                startedAt = now;
                loops = 0;
                loopsAtLastPing = 0;
                running = true;
            }
            EnsureThread();
        }

        // Called the moment you actually hear the relic. Re-aligns the loop to the real sound
        // and uses the observed gap to learn the true interval instead of trusting the default.
        public string Ping()
        {
            lock (gate)
            {
                var now = DateTime.UtcNow;

                if (!running)
                {
                    anchor = now;
                    lastPing = now;
                    startedAt = now;
                    loops = 0;
                    loopsAtLastPing = 0;
                    running = true;
                    EnsureThreadNoLock();
                    return $"Loop started from this ping. Interval {interval:0.0}s until calibrated.";
                }

                Roll(now);

                // If the rollover fired a moment ago it already counted this sound.
                double sinceAnchor = (now - anchor).TotalSeconds;
                if (sinceAnchor > interval * SameSoundFraction) loops++;
                anchor = now;

                double gap = (now - lastPing).TotalSeconds;
                lastPing = now;
                long skipped = loops - loopsAtLastPing;
                loopsAtLastPing = loops;

                if (gap < MinGap)
                    return $"Ping - loop {loops}. Only {gap:0.0}s after the last one, ignored for calibration.";

                // You do not have to ping every single sound. Fold a multi-cycle gap down to
                // one cycle so pinging every second or third sound still calibrates correctly.
                double cycles = Math.Max(1, Math.Round(gap / interval));
                double perCycle = gap / cycles;

                if (perCycle < interval * CalibLow || perCycle > interval * CalibHigh)
                    return $"Ping - loop {loops}. {perCycle:0.0}s per cycle is too far from {interval:0.0}s to trust, interval unchanged.";

                gaps.Add(perCycle);
                if (gaps.Count > CalibrationWindow) gaps.RemoveAt(0);
                interval = Median(gaps);

                string span = cycles > 1 ? $" ({gap:0.0}s over {cycles:0} cycles)" : "";
                string missed = skipped > 1 ? $", {skipped - 1} sound(s) went by unconfirmed" : "";
                return $"Ping - loop {loops}. Interval now {interval:0.0}s{span}, median of {gaps.Count}{missed}.";
            }
        }

        // Silent if it was already stopped - Main calls this again on the way out, and the
        // summary should only ever print once.
        public void Stop()
        {
            bool wasRunning;
            long done;
            double elapsed, perLoop;

            lock (gate)
            {
                wasRunning = running;
                if (!running) return;

                var now = DateTime.UtcNow;
                Roll(now);
                done = loops;
                elapsed = (now - startedAt).TotalSeconds;
                perLoop = interval;
                running = false;
            }

            if (!wasRunning) return;

            ClearLine();
            try { Console.Title = "Ghost Seek Locator"; } catch { }
            Ui.Line($"Timer stopped after {done} loop(s) over {FormatSpan(elapsed)} at {perLoop:0.0}s per loop.");
        }

        public string Status()
        {
            lock (gate)
            {
                if (!running)
                    return "Timer is not running. 'starttimer' to begin, or 'starttimer 15' to set the interval.";

                var now = DateTime.UtcNow;
                Roll(now);

                double remaining = interval - (now - anchor).TotalSeconds;
                double sincePing = (now - lastPing).TotalSeconds;
                string calib = gaps.Count == 0 ? "uncalibrated (default)" : $"calibrated from {gaps.Count} ping(s)";
                long unconfirmed = loops - loopsAtLastPing;

                string drift = unconfirmed >= DriftLoops
                    ? $" No ping in {unconfirmed} loop(s) ({FormatSpan(sincePing)}) - the loop may have drifted, or you walked out of range."
                    : "";

                return $"Loop {loops + 1}, next sound in {remaining:0.0}s. "
                     + $"Interval {interval:0.0}s, {calib}. Running {FormatSpan((now - startedAt).TotalSeconds)}.{drift}";
            }
        }

        // Advance past however many whole intervals have elapsed. Keeps the phase rather than
        // resetting it, so the loop stays aligned to the sound even after a long pause.
        void Roll(DateTime now)
        {
            if (interval <= 0) return;
            double elapsed = (now - anchor).TotalSeconds;
            if (elapsed < interval) return;

            long whole = (long)(elapsed / interval);
            loops += whole;
            anchor = anchor.AddSeconds(whole * interval);
        }

        void EnsureThread()
        {
            lock (gate) EnsureThreadNoLock();
        }

        void EnsureThreadNoLock()
        {
            if (thread != null) return;
            thread = new Thread(RenderLoop) { IsBackground = true, Name = "ping-countdown" };
            thread.Start();
        }

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        static string FormatSpan(double seconds)
        {
            if (seconds < 60) return $"{seconds:0}s";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m {ts.Seconds}s";
        }

        void RenderLoop()
        {
            while (true)
            {
                bool active;
                lock (gate) active = running;
                if (active) Draw();
                Thread.Sleep(200);
            }
        }

        void Draw()
        {
            double remaining, span;
            long loopNumber, unconfirmed;

            lock (gate)
            {
                var now = DateTime.UtcNow;
                Roll(now);
                span = interval;
                remaining = interval - (now - anchor).TotalSeconds;
                loopNumber = loops + 1;
                unconfirmed = loops - loopsAtLastPing;
            }

            double elapsedFraction = span <= 0 ? 0 : Math.Clamp(1 - remaining / span, 0, 1);
            string text = $"[{Ui.Bar(elapsedFraction, 10)} {remaining,4:0.0}s  loop {loopNumber}]";

            try { Console.Title = $"Ghost Seek - loop {loopNumber}, next in {remaining:0.0}s"; } catch { }

            if (Console.IsOutputRedirected) return;

            lock (Ui.Gate)
            {
                try
                {
                    int col = Console.WindowWidth - text.Length - 2;
                    if (col < 32) return;   // narrow window: title bar only, don't fight the prompt

                    int left = Console.CursorLeft, top = Console.CursorTop;
                    var prev = Console.ForegroundColor;

                    Console.SetCursorPosition(col, top);
                    Console.ForegroundColor = unconfirmed >= DriftLoops ? ConsoleColor.DarkYellow : ConsoleColor.DarkCyan;
                    Console.Write(text);
                    Console.ForegroundColor = prev;
                    Console.SetCursorPosition(left, top);

                    lastDrawWidth = text.Length;
                }
                catch (IOException) { }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        void ClearLine()
        {
            if (Console.IsOutputRedirected || lastDrawWidth == 0) return;

            lock (Ui.Gate)
            {
                try
                {
                    int col = Console.WindowWidth - lastDrawWidth - 2;
                    if (col < 0) return;
                    int left = Console.CursorLeft, top = Console.CursorTop;
                    Console.SetCursorPosition(col, top);
                    Console.Write(new string(' ', lastDrawWidth));
                    Console.SetCursorPosition(left, top);
                }
                catch (IOException) { }
                catch (ArgumentOutOfRangeException) { }
            }
        }
    }
}
