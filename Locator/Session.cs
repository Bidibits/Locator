using System;
using System.Collections.Generic;

namespace SpawnLocator
{
    // One command as typed (or replayed), what it did, and when.
    class ActivityLogEntry
    {
        public DateTime Timestamp;
        public string RawInput = "";
        public string Outcome = "";
    }

    // A contiguous run of commands sharing one start time - normally one process's lifetime,
    // or one block replayed from an imported file. Identity is StartedAt, not a stored index:
    // the index shown to a user is always recomputed by sorting sessions on StartedAt, so it
    // survives being imported and re-exported without renumbering corruption.
    class Session
    {
        public DateTime StartedAt;
        public List<ActivityLogEntry> Entries = new List<ActivityLogEntry>();
        public bool Imported;

        public TimeSpan Duration => Entries.Count == 0
            ? TimeSpan.Zero
            : Entries[Entries.Count - 1].Timestamp - StartedAt;
    }
}
