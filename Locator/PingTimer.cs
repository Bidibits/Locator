using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SpawnLocator
{
    // The relic re-announces itself on a fixed cadence (roughly 15-20s). This counts down
    // to the next expected sound so you know whether to keep still or keep walking.
    //
    // The countdown is drawn by a background thread at the right-hand edge of whatever line
    // the cursor is on, then the cursor is put straight back - so it never disturbs typing.
    // The window title mirrors it, which keeps working even when the console is too narrow
    // or output is redirected.
    class PingTimer
    {
        const double DefaultInterval = 20.0;
        const double MinPlausibleGap = 5.0;
        const double MaxPlausibleGap = 60.0;
        const int CalibrationWindow = 6;

        readonly object gate = new object();
        readonly List<double> gaps = new List<double>();

        double interval = DefaultInterval;
        DateTime lastPing;
        bool running;
        Thread? thread;
        int lastDrawWidth;

        public bool Running { get { lock (gate) return running; } }

        public void Start(double? explicitInterval)
        {
            lock (gate)
            {
                if (explicitInterval.HasValue)
                {
                    interval = explicitInterval.Value;
                    gaps.Clear();
                }
                lastPing = DateTime.UtcNow;
                running = true;
            }

            if (thread == null)
            {
                thread = new Thread(RenderLoop) { IsBackground = true, Name = "ping-countdown" };
                thread.Start();
            }
        }

        // Called the moment you actually hear the relic. Re-syncs the countdown and uses the
        // observed gap to learn the real interval instead of trusting the 20s default.
        public string Ping()
        {
            lock (gate)
            {
                var now = DateTime.UtcNow;
                if (!running)
                {
                    lastPing = now;
                    running = true;
                    if (thread == null)
                    {
                        thread = new Thread(RenderLoop) { IsBackground = true, Name = "ping-countdown" };
                        thread.Start();
                    }
                    return $"Timer started from this ping. Interval {interval:0.0}s until calibrated.";
                }

                double gap = (now - lastPing).TotalSeconds;
                lastPing = now;

                if (gap < MinPlausibleGap || gap > MaxPlausibleGap)
                    return $"Ping. Gap was {gap:0.0}s - too far off to trust, interval stays {interval:0.0}s.";

                gaps.Add(gap);
                if (gaps.Count > CalibrationWindow) gaps.RemoveAt(0);
                interval = Median(gaps);
                return $"Ping. Gap {gap:0.0}s -> interval now {interval:0.0}s (median of {gaps.Count}).";
            }
        }

        public void Stop()
        {
            lock (gate) running = false;
            ClearLine();
            try { Console.Title = "Ghost Seek Locator"; } catch { }
        }

        public string Status()
        {
            lock (gate)
            {
                if (!running) return "Timer is not running. 'starttimer' to begin, or 'starttimer 15' to set the interval.";
                double remaining = interval - (DateTime.UtcNow - lastPing).TotalSeconds;
                string calib = gaps.Count == 0 ? "uncalibrated (default)" : $"calibrated from {gaps.Count} ping(s)";
                return remaining >= 0
                    ? $"Next sound in {remaining:0.0}s. Interval {interval:0.0}s, {calib}."
                    : $"Overdue by {-remaining:0.0}s - you may have walked out of range. Interval {interval:0.0}s, {calib}.";
            }
        }

        static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
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
            lock (gate)
            {
                span = interval;
                remaining = interval - (DateTime.UtcNow - lastPing).TotalSeconds;
            }

            string label = remaining >= 0 ? $"{remaining,4:0.0}s" : $"OVERDUE {-remaining,3:0}s";
            double elapsed = span <= 0 ? 1 : Math.Clamp(1 - remaining / span, 0, 1);
            string text = $"[{Ui.Bar(elapsed, 10)} {label}]";

            try { Console.Title = $"Ghost Seek - next sound {label}"; } catch { }

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
                    Console.ForegroundColor = remaining >= 0 ? ConsoleColor.DarkCyan : ConsoleColor.Magenta;
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
