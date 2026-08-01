using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SpawnLocator
{
    // Reads back whatever Export.BuildLog wrote. Never throws - a malformed or
    // newer-than-supported file comes back as a rejected outcome with a line-numbered
    // error, never a crash, since this may be fed a hand-edited or corrupted file.
    static class Import
    {
        const string EntryTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        public static bool TryParse(string text, out List<Session> sessions, out string error)
        {
            sessions = new List<Session>();
            error = "";

            var lines = text.Replace("\r\n", "\n").Split('\n');

            int formatVersion = -1;
            for (int i = 0; i < lines.Length && i < 10; i++)
            {
                if (lines[i].StartsWith("Format-Version", StringComparison.Ordinal))
                {
                    var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !int.TryParse(parts[1], out formatVersion))
                    {
                        error = $"line {i + 1}: couldn't read the Format-Version line.";
                        return false;
                    }
                    break;
                }
            }

            if (formatVersion < 0)
            {
                error = "No 'Format-Version' header found - this doesn't look like a Locator session log.";
                return false;
            }
            if (formatVersion > Export.FormatVersion)
            {
                error = $"This file is format version {formatVersion}, but this copy of the app only understands "
                      + $"up to version {Export.FormatVersion}. Update the app.";
                return false;
            }

            Session? current = null;
            bool inBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.StartsWith("--- Session ", StringComparison.Ordinal) && line.TrimEnd().EndsWith("---", StringComparison.Ordinal))
                {
                    if (inBlock)
                    {
                        error = $"line {i + 1}: a new session block started before the previous one ended.";
                        return false;
                    }
                    current = new Session { Imported = true };
                    inBlock = true;
                    continue;
                }

                if (line.StartsWith("--- end session", StringComparison.Ordinal))
                {
                    if (!inBlock || current == null)
                    {
                        error = $"line {i + 1}: 'end session' with no session block open.";
                        return false;
                    }
                    sessions.Add(current);
                    current = null;
                    inBlock = false;
                    continue;
                }

                if (!inBlock) continue;   // between blocks, or before the first one - ignore

                if (line.StartsWith("Started", StringComparison.Ordinal))
                {
                    var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 ||
                        !DateTime.TryParseExact($"{parts[1]} {parts[2]}", EntryTimeFormat, CultureInfo.InvariantCulture,
                                                 DateTimeStyles.None, out DateTime started))
                    {
                        error = $"line {i + 1}: couldn't read the session's Started timestamp.";
                        return false;
                    }
                    current!.StartedAt = started;
                    continue;
                }

                // Cosmetic summary lines - recomputed on export, never trusted on import.
                if (line.StartsWith("Duration", StringComparison.Ordinal) || line.StartsWith("Commands", StringComparison.Ordinal))
                    continue;

                if (line.Trim().Length == 0) continue;

                var fields = line.Split('\t');
                if (fields.Length != 3)
                {
                    error = $"line {i + 1}: expected 3 tab-separated fields (timestamp, command, outcome), found {fields.Length}.";
                    return false;
                }

                if (!DateTime.TryParseExact(fields[0], EntryTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ts))
                {
                    error = $"line {i + 1}: couldn't read the entry's timestamp '{fields[0]}'.";
                    return false;
                }

                current!.Entries.Add(new ActivityLogEntry { Timestamp = ts, RawInput = fields[1], Outcome = fields[2] });
            }

            if (inBlock)
            {
                error = "The file ends mid-session - the last '--- Session N ---' block was never closed.";
                return false;
            }

            if (sessions.Count == 0)
            {
                error = "No session blocks found in this file.";
                return false;
            }

            return true;
        }
    }
}
