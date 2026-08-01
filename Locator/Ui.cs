using System;

namespace SpawnLocator
{
    // Every console write goes through here. The ping timer redraws its countdown
    // from a background thread, so without a shared gate it would scribble into
    // the middle of whatever the main loop was printing.
    static class Ui
    {
        public static readonly object Gate = new object();

        public static void Line(string text = "")
        {
            lock (Gate) Console.WriteLine(text);
        }

        public static void Line(string text, ConsoleColor color)
        {
            lock (Gate)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(text);
                Console.ForegroundColor = prev;
            }
        }

        public static void Raw(string text)
        {
            lock (Gate) Console.Write(text);
        }

        // 0..1 -> "██████░░░░"
        public static string Bar(double fraction, int width = 20)
        {
            if (double.IsNaN(fraction)) fraction = 0;
            fraction = Math.Clamp(fraction, 0, 1);
            int filled = (int)Math.Round(fraction * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        public static ConsoleColor ConfidenceColor(double confidence)
        {
            if (confidence >= 0.85) return ConsoleColor.Green;
            if (confidence >= 0.50) return ConsoleColor.Yellow;
            return ConsoleColor.Red;
        }
    }
}
