using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnLocator
{
    // A spatially connected lump of valid candidates. More than one of these from a
    // single consistent reading set means the geometry genuinely allows two separate
    // places - typically the mirrored solution you get before you have enough readings,
    // or a second skeleton on another platform / at another altitude.
    class Blob
    {
        public long Count;
        public double Cx, Cy, Cz;
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public double Volume;

        public string Span => $"X[{MinX:0.#}, {MaxX:0.#}]  Y[{MinY:0.#}, {MaxY:0.#}]  Z[{MinZ:0.#}, {MaxZ:0.#}]";
    }

    class SolveResult
    {
        public bool BoxEmpty;    // the bounding boxes never overlapped - readings contradict
        public bool Bounded;     // at least one reading pins a finite region
        public long Count;
        public double Step;
        public double Volume;    // in blocks^3, comparable across different grid steps
        public double Cx, Cy, Cz;
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public List<Blob> Blobs = new List<Blob>();
    }

    static class Solver
    {
        // Target sample count for the grid search. Bigger = more precise, slower.
        public const long TargetSamples = 2_000_000;

        sealed class Cell
        {
            public long Count;
            public double Sx, Sy, Sz;
            public double MinX = double.PositiveInfinity, MaxX = double.NegativeInfinity;
            public double MinY = double.PositiveInfinity, MaxY = double.NegativeInfinity;
            public double MinZ = double.PositiveInfinity, MaxZ = double.NegativeInfinity;
        }

        public static SolveResult Solve(IReadOnlyList<Reading> group, DistanceMetric metric)
        {
            var result = new SolveResult();
            if (group.Count == 0) return result;

            // Step 1: intersect the axis-aligned boxes implied by each reading. For both
            // metrics "distance <= maxDist" implies the point lies within
            // [pos - maxDist, pos + maxDist] on every axis, so the bound is valid either way.
            double minX = double.NegativeInfinity, maxX = double.PositiveInfinity;
            double minY = double.NegativeInfinity, maxY = double.PositiveInfinity;
            double minZ = double.NegativeInfinity, maxZ = double.PositiveInfinity;

            foreach (var r in group)
            {
                minX = Math.Max(minX, r.X - r.MaxDist);
                maxX = Math.Min(maxX, r.X + r.MaxDist);
                minY = Math.Max(minY, r.Y - r.MaxDist);
                maxY = Math.Min(maxY, r.Y + r.MaxDist);
                minZ = Math.Max(minZ, r.Z - r.MaxDist);
                maxZ = Math.Min(maxZ, r.Z + r.MaxDist);
            }

            if (minX > maxX || minY > maxY || minZ > maxZ)
            {
                result.BoxEmpty = true;
                return result;
            }

            if (double.IsInfinity(minX) || double.IsInfinity(maxX) ||
                double.IsInfinity(minY) || double.IsInfinity(maxY) ||
                double.IsInfinity(minZ) || double.IsInfinity(maxZ))
            {
                // Silent readings only rule points out; they cannot bound a region alone.
                return result;
            }

            result.Bounded = true;

            double sizeX = maxX - minX, sizeY = maxY - minY, sizeZ = maxZ - minZ;
            double boxVolume = Math.Max(1, sizeX) * Math.Max(1, sizeY) * Math.Max(1, sizeZ);

            // Step 2: choose a grid step so total sample count stays near TargetSamples.
            double step = Math.Max(1.0, Math.Cbrt(boxVolume / TargetSamples));
            result.Step = step;

            // Step 3: sample. Rather than keeping every hit (millions of points), accumulate
            // into coarse cells - enough to find separated lumps, bounded in memory.
            double cellSize = Math.Max(step * 3, 8.0);
            var cells = new Dictionary<(long, long, long), Cell>();

            long count = 0;
            double sumX = 0, sumY = 0, sumZ = 0;
            double loX = double.PositiveInfinity, hiX = double.NegativeInfinity;
            double loY = double.PositiveInfinity, hiY = double.NegativeInfinity;
            double loZ = double.PositiveInfinity, hiZ = double.NegativeInfinity;

            for (double x = minX; x <= maxX; x += step)
            {
                for (double y = minY; y <= maxY; y += step)
                {
                    for (double z = minZ; z <= maxZ; z += step)
                    {
                        bool ok = true;
                        foreach (var r in group)
                        {
                            if (!r.Satisfies(x, y, z, metric)) { ok = false; break; }
                        }
                        if (!ok) continue;

                        count++;
                        sumX += x; sumY += y; sumZ += z;
                        if (x < loX) loX = x; if (x > hiX) hiX = x;
                        if (y < loY) loY = y; if (y > hiY) hiY = y;
                        if (z < loZ) loZ = z; if (z > hiZ) hiZ = z;

                        var key = ((long)Math.Floor(x / cellSize),
                                   (long)Math.Floor(y / cellSize),
                                   (long)Math.Floor(z / cellSize));
                        if (!cells.TryGetValue(key, out var cell))
                        {
                            cell = new Cell();
                            cells[key] = cell;
                        }
                        cell.Count++;
                        cell.Sx += x; cell.Sy += y; cell.Sz += z;
                        if (x < cell.MinX) cell.MinX = x; if (x > cell.MaxX) cell.MaxX = x;
                        if (y < cell.MinY) cell.MinY = y; if (y > cell.MaxY) cell.MaxY = y;
                        if (z < cell.MinZ) cell.MinZ = z; if (z > cell.MaxZ) cell.MaxZ = z;
                    }
                }
            }

            result.Count = count;
            if (count == 0) return result;

            result.Volume = count * step * step * step;
            result.Cx = sumX / count; result.Cy = sumY / count; result.Cz = sumZ / count;
            result.MinX = loX; result.MaxX = hiX;
            result.MinY = loY; result.MaxY = hiY;
            result.MinZ = loZ; result.MaxZ = hiZ;
            result.Blobs = FindBlobs(cells, step);

            return result;
        }

        // Flood fill over occupied cells (26-neighbourhood) to find disconnected lumps.
        static List<Blob> FindBlobs(Dictionary<(long, long, long), Cell> cells, double step)
        {
            var blobs = new List<Blob>();
            var seen = new HashSet<(long, long, long)>();
            var queue = new Queue<(long, long, long)>();

            foreach (var start in cells.Keys)
            {
                if (!seen.Add(start)) continue;

                var members = new List<Cell>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    var key = queue.Dequeue();
                    members.Add(cells[key]);

                    for (long dx = -1; dx <= 1; dx++)
                        for (long dy = -1; dy <= 1; dy++)
                            for (long dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                var n = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz);
                                if (cells.ContainsKey(n) && seen.Add(n)) queue.Enqueue(n);
                            }
                }

                var blob = new Blob
                {
                    MinX = double.PositiveInfinity, MaxX = double.NegativeInfinity,
                    MinY = double.PositiveInfinity, MaxY = double.NegativeInfinity,
                    MinZ = double.PositiveInfinity, MaxZ = double.NegativeInfinity
                };
                double sx = 0, sy = 0, sz = 0;
                foreach (var c in members)
                {
                    blob.Count += c.Count;
                    sx += c.Sx; sy += c.Sy; sz += c.Sz;
                    blob.MinX = Math.Min(blob.MinX, c.MinX); blob.MaxX = Math.Max(blob.MaxX, c.MaxX);
                    blob.MinY = Math.Min(blob.MinY, c.MinY); blob.MaxY = Math.Max(blob.MaxY, c.MaxY);
                    blob.MinZ = Math.Min(blob.MinZ, c.MinZ); blob.MaxZ = Math.Max(blob.MaxZ, c.MaxZ);
                }
                blob.Cx = sx / blob.Count; blob.Cy = sy / blob.Count; blob.Cz = sz / blob.Count;
                blob.Volume = blob.Count * step * step * step;
                blobs.Add(blob);
            }

            blobs.Sort((a, b) => b.Count.CompareTo(a.Count));
            return blobs;
        }

        public static double Separation(Blob a, Blob b)
        {
            double dx = a.Cx - b.Cx, dy = a.Cy - b.Cy, dz = a.Cz - b.Cz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
