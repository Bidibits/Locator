using System;

namespace SpawnLocator
{
    enum DistanceMetric
    {
        Chebyshev,  // "blocky": distance = max(|dx|,|dy|,|dz|) -> rings are cube shells
        Euclidean   // classic straight-line distance -> rings are sphere shells
    }

    // Which coordinate display the player is reading readings off of. Minecraft = the
    // real world coordinates. Abyss = a lore-flavored display some servers show instead,
    // whose Y can differ from real Minecraft-Y by a large, per-section constant offset.
    enum CoordsMode
    {
        Minecraft,
        Abyss
    }

    // One "I stood here and the seeker made sound X" observation.
    //
    // MinDist/MaxDist are resolved from the seeker's band table at entry time and stored
    // absolutely, so swapping seekers mid-hunt never retroactively changes what an old
    // reading meant - the letter is just a label for the distance window.
    class Reading
    {
        public int Seq;                 // stable id, never reused; drives recency weighting
        public double X, Y, Z;
        public double MinDist, MaxDist;
        public char Letter;
        public string SeekerName = "";
        public DateTime Timestamp;      // wall-clock moment the reading was entered
        public bool Retired;            // true once a 'found' matched it - kept, never deleted
        public int? RetiredByKillId;

        public bool IsSilent => double.IsPositiveInfinity(MaxDist);

        public string BandText => IsSilent ? $"{MinDist:0}+ (silent)" : $"{MinDist:0}-{MaxDist:0}";
        public string PosText => $"({X:0.#}, {Y:0.#}, {Z:0.#})";
        public string TimeText => Timestamp.ToString("HH:mm:ss");
        public string FullTimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

        public double DistanceTo(double x, double y, double z, DistanceMetric metric)
        {
            double dx = Math.Abs(x - X);
            double dy = Math.Abs(y - Y);
            double dz = Math.Abs(z - Z);

            if (metric == DistanceMetric.Chebyshev)
                return Math.Max(dx, Math.Max(dy, dz));

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // The seeker reports a rounded distance, not the raw continuous value, so a true
        // 100.04 still counts as "100" and must pass a 51-100 band. Comparing the raw
        // float would wrongly reject points sitting within rounding distance of an edge.
        public bool Satisfies(double x, double y, double z, DistanceMetric metric)
        {
            double d = Math.Round(DistanceTo(x, y, z, metric), MidpointRounding.AwayFromZero);
            return d >= MinDist && d <= MaxDist;
        }
    }
}
