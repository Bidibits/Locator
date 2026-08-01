using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnLocator
{
    // A self-consistent set of readings: one candidate skeleton.
    class Track
    {
        public int Index;
        public List<Reading> Readings = new List<Reading>();
        public SolveResult? Result;

        public string IdList => string.Join(" ", Readings.Select(r => "#" + r.Seq));
    }

    static class Analysis
    {
        // How fast an older reading loses weight. 0.85 means each step back in entry
        // order counts ~15% less, so a couple of fresh readings can out-vote several
        // stale ones - which is the point: if you have walked to a different skeleton,
        // the new readings are the ones describing where you actually are.
        public const double RecencyDecay = 0.85;

        // Band edges are rounded, so allow a block of slack before calling two readings
        // genuinely incompatible.
        const double Slack = 1.0;

        // Can any single point satisfy both readings at once?
        //
        // Reading i allows a sphere shell of radius [a_i, b_i] around p_i. Two spheres of
        // radii r1, r2 whose centres sit D apart intersect exactly when |r1-r2| <= D <= r1+r2.
        // Free to pick any r1 in [a1,b1] and r2 in [a2,b2], so the shells are compatible when
        //     max(0, a1-b2, a2-b1)  <=  D  <=  b1 + b2
        // Exact for a pair; three readings can still be pairwise fine yet jointly impossible,
        // which the grid solve catches later.
        public static bool Compatible(Reading a, Reading b, DistanceMetric metric)
        {
            double d = a.DistanceTo(b.X, b.Y, b.Z, metric);

            double lo = Math.Max(0, Math.Max(a.MinDist - b.MaxDist, b.MinDist - a.MaxDist));
            double hi = a.MaxDist + b.MaxDist;   // +inf if either is a silent reading

            return d >= lo - Slack && d <= hi + Slack;
        }

        public static double RecencyWeight(Reading r, int newestSeq)
        {
            return Math.Pow(RecencyDecay, Math.Max(0, newestSeq - r.Seq));
        }

        // Part 4: per-reading confidence.
        //
        // Every other reading votes on this one, weighted by how recent that voter is.
        // Agreement pulls toward 100%, contradiction pulls toward 0%. A reading that
        // conflicts with four old readings sits near the bottom on its own - but once a
        // second fresh reading backs it up, the two newest weights dominate the sum and
        // both climb, while the four stale ones slide.
        public static Dictionary<int, double> Confidence(IReadOnlyList<Reading> readings, DistanceMetric metric)
        {
            var scores = new Dictionary<int, double>();
            if (readings.Count == 0) return scores;

            int newest = readings.Max(r => r.Seq);

            foreach (var r in readings)
            {
                double agree = 0, conflict = 0;

                foreach (var other in readings)
                {
                    if (ReferenceEquals(other, r)) continue;
                    double w = RecencyWeight(other, newest);
                    if (Compatible(r, other, metric)) agree += w;
                    else conflict += w;
                }

                double total = agree + conflict;
                // Nothing to corroborate or contradict it yet - a lone reading is taken at face value.
                scores[r.Seq] = total <= 0 ? 1.0 : Math.Clamp(0.5 + 0.5 * (agree - conflict) / total, 0, 1);
            }

            return scores;
        }

        public static List<(Reading a, Reading b)> Conflicts(IReadOnlyList<Reading> readings, DistanceMetric metric)
        {
            var pairs = new List<(Reading, Reading)>();
            for (int i = 0; i < readings.Count; i++)
                for (int j = i + 1; j < readings.Count; j++)
                    if (!Compatible(readings[i], readings[j], metric))
                        pairs.Add((readings[i], readings[j]));
            return pairs;
        }

        // Part 3: split readings into groups that are each internally consistent.
        //
        // If two skeletons spawned far apart and you took readings near both, no single
        // location explains everything - but each subset does. Greedy, seeded newest-first
        // so the freshest readings anchor track 1.
        public static List<Track> BuildTracks(IReadOnlyList<Reading> readings, DistanceMetric metric)
        {
            var tracks = new List<Track>();

            foreach (var r in readings.OrderByDescending(x => x.Seq))
            {
                Track? home = null;
                foreach (var t in tracks)
                {
                    if (t.Readings.All(m => Compatible(m, r, metric))) { home = t; break; }
                }
                if (home == null)
                {
                    home = new Track();
                    tracks.Add(home);
                }
                home.Readings.Add(r);
            }

            foreach (var t in tracks)
                t.Readings.Sort((a, b) => a.Seq.CompareTo(b.Seq));

            // Most-supported track first, ties broken by whichever holds the newest reading.
            tracks.Sort((a, b) =>
            {
                int byCount = b.Readings.Count.CompareTo(a.Readings.Count);
                if (byCount != 0) return byCount;
                return b.Readings.Max(r => r.Seq).CompareTo(a.Readings.Max(r => r.Seq));
            });

            for (int i = 0; i < tracks.Count; i++) tracks[i].Index = i + 1;
            return tracks;
        }
    }
}
