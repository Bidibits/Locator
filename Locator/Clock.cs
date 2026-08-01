using System;

namespace SpawnLocator
{
    // Every timestamp the app stamps on data (reading entry, activity log, a kill) goes
    // through here instead of calling DateTime.Now directly, so that replaying an imported
    // session can make those timestamps come out as the ORIGINAL recorded moment instead of
    // "now" - which is what lets 'simulate' reconstruct true historical chronology rather
    // than relabeling everything with today's date.
    static class Clock
    {
        public static DateTime? Override;
        public static DateTime Now => Override ?? DateTime.Now;
    }
}
