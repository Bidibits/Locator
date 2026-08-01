using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpawnLocator
{
    // Writes the session log Import.cs reads back: every command typed (or replayed),
    // verbatim, with a timestamp and a short outcome. Deliberately excludes anything
    // computed - no estimate, no region, no candidate counts, no pockets - because all of
    // that is 100% reproducible by replaying these same commands through the app's own
    // parser. Storing it here would just be stale duplication the moment another reading
    // comes in. Plain ASCII, invariant-culture, UTF-8 without a BOM, so it parses the same
    // everywhere and matches how the program reads its own input.
    static class Export
    {
        public const int FormatVersion = 1;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string EntryTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        public static string BuildLog(IReadOnlyList<Session> sessions)
        {
            var ordered = sessions.OrderBy(s => s.StartedAt).ToList();
            int totalCommands = ordered.Sum(s => s.Entries.Count);

            var sb = new StringBuilder();
            sb.AppendLine("=== Ghost Seek Locator session log ===");
            sb.AppendLine($"Format-Version {FormatVersion}");
            sb.AppendLine($"Generated       {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)}");
            sb.AppendLine($"Sessions        {ordered.Count}");
            sb.AppendLine($"Commands        {totalCommands}");
            sb.AppendLine();

            for (int i = 0; i < ordered.Count; i++)
                AppendSession(sb, ordered[i], i + 1);

            return sb.ToString();
        }

        static void AppendSession(StringBuilder sb, Session s, int index)
        {
            sb.AppendLine($"--- Session {index} ---");
            sb.AppendLine($"Started   {s.StartedAt.ToString(EntryTimeFormat, Inv)}");
            sb.AppendLine($"Duration  {FormatDuration(s.Duration)}");
            sb.AppendLine($"Commands  {s.Entries.Count}");

            foreach (var e in s.Entries)
            {
                sb.AppendLine(string.Join("\t",
                    e.Timestamp.ToString(EntryTimeFormat, Inv),
                    Sanitize(e.RawInput),
                    Sanitize(e.Outcome)));
            }

            sb.AppendLine($"--- end session {index} ---");
            sb.AppendLine();
        }

        // Tabs/newlines would break the 3-field structure a line is parsed back into.
        static string Sanitize(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        static string FormatDuration(TimeSpan span) =>
            $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
