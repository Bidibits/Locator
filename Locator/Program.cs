using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SpawnLocator
{
    enum DistanceMetric
    {
        Chebyshev,  // "blocky": distance = max(|dx|,|dy|,|dz|) -> rings are cube shells
        Euclidean   // classic straight-line distance -> rings are sphere shells
    }

    struct Reading
    {
        public double X, Y, Z;
        public double MinDist, MaxDist;
        public string Range;
    }

    class Program
    {
        static List<Reading> readings = new List<Reading>();
        static bool haveLastEstimate = false;
        static double lastEstX, lastEstY, lastEstZ;
        // Confirmed against real in-game data: distance behaves as straight-line (Euclidean),
        // not blocky/cube-shell (Chebyshev). See the black/gray/yellow/green/blue calibration below.
        static DistanceMetric metric = DistanceMetric.Euclidean;

        // Range -> (min, max) distance band, inclusive.
        // Confirmed from real readings against a known origin point:
        //   green readings landed at ~26-28   -> matches D (26-50)
        //   yellow reading landed at ~100.04  -> matches C (51-100)
        //   gray readings landed at ~101-105  -> matches B (101-150)
        static readonly Dictionary<string, (double min, double max)> Bands = new()
        {
            ["farfar"] = (151, 200),
            ["far"] = (101, 150),
            ["near"] = (51, 100),
            ["close"] = (26, 50),
            ["closest"] = (0, 25),
            ["nothing"] = (201, double.PositiveInfinity), // out of detection range entirely
        };

        // Target sample count for the grid search. Bigger = more precise, slower.
        const long TargetSamples = 2_000_000;

        static void Main()
        {
            Console.WriteLine("=== Spawn Locator ===");
            Console.WriteLine("Enter readings as:  x y z range        e.g.  120 64 -30 near");
            Console.WriteLine("Distance bands -> farfar:151-250  far:101-150  near:51-100  close:26-50  closest:0-25  nothing:201+");
            Console.WriteLine($"Current distance metric: {metric} (type 'metric chebyshev' or 'metric euclidean' to change)");
            Console.WriteLine("Other commands: list | estimate | delete N | found | reset | help | exit");
            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                string? line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (tokens[0].Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    tokens[0].Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                else if (tokens[0].Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                }
                else if (tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    readings.Clear();
                    haveLastEstimate = false;
                    Console.WriteLine("All readings cleared.");
                }
                else if (tokens[0].Equals("found", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Length == 4 &&
                        double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double fx) &&
                        double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fy) &&
                        double.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double fz))
                    {
                        if (haveLastEstimate)
                        {
                            double err = Math.Sqrt(Math.Pow(fx - lastEstX, 2) + Math.Pow(fy - lastEstY, 2) + Math.Pow(fz - lastEstZ, 2));
                            Console.WriteLine($"Found at ({fx},{fy},{fz}) - estimate was off by {err:0.#} blocks. Nice.");
                        }
                        else
                        {
                            Console.WriteLine($"Found at ({fx},{fy},{fz}). Logged.");
                        }
                    }
                    else if (tokens.Length > 1)
                    {
                        Console.WriteLine("To log where you found it: found x y z   (or just 'found' with no coords)");
                    }
                    else
                    {
                        Console.WriteLine("Got it - marking this one found.");
                    }

                    readings.Clear();
                    haveLastEstimate = false;
                    Console.WriteLine("Starting fresh for the next item. Enter readings whenever you're ready.");
                }
                else if (tokens[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    ListReadings();
                }
                else if ((tokens[0].Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                          tokens[0].Equals("remove", StringComparison.OrdinalIgnoreCase)))
                {
                    if (tokens.Length != 2 || !int.TryParse(tokens[1], out int delIndex))
                    {
                        Console.WriteLine("Usage: delete N   (N is the reading number shown by 'list')");
                    }
                    else if (delIndex < 1 || delIndex > readings.Count)
                    {
                        Console.WriteLine($"No reading #{delIndex}. There are {readings.Count} reading(s) - use 'list' to see them.");
                    }
                    else
                    {
                        var removed = readings[delIndex - 1];
                        readings.RemoveAt(delIndex - 1);
                        Console.WriteLine($"Removed #{delIndex}: ({removed.X},{removed.Y},{removed.Z}) range={removed.Range}.");
                        if (readings.Count > 0) ComputeAndPrintEstimate();
                        else Console.WriteLine("No readings left.");
                    }
                }
                else if (tokens[0].Equals("estimate", StringComparison.OrdinalIgnoreCase))
                {
                    ComputeAndPrintEstimate();
                }
                else if (tokens[0].Equals("metric", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 2)
                {
                    if (tokens[1].StartsWith("cheb", StringComparison.OrdinalIgnoreCase))
                    {
                        metric = DistanceMetric.Chebyshev;
                        Console.WriteLine("Metric set to Chebyshev (blocky / cube-shell rings).");
                    }
                    else if (tokens[1].StartsWith("euc", StringComparison.OrdinalIgnoreCase))
                    {
                        metric = DistanceMetric.Euclidean;
                        Console.WriteLine("Metric set to Euclidean (circular / sphere-shell rings).");
                    }
                    else
                    {
                        Console.WriteLine("Unknown metric. Use 'metric chebyshev' or 'metric euclidean'.");
                    }
                    if (readings.Count > 0) ComputeAndPrintEstimate();
                }
                else if (tokens.Length == 4)
                {
                    TryAddReading(tokens);
                }
                else
                {
                    Console.WriteLine("Didn't understand that. Type 'help' for usage.");
                }
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("Add a reading:      x y z range         e.g.  -12 70 305 near");
            Console.WriteLine("Ranges: closest, close, near, far, farfar, nothing (out of range, 201+)");
            Console.WriteLine("List readings:       list");
            Console.WriteLine("Delete a reading:    delete N        (N is the number shown by 'list'; numbers shift after a delete)");
            Console.WriteLine("Force an estimate:   estimate");
            Console.WriteLine("Item found:          found            (or 'found x y z' to log the spot and see how close the estimate was)");
            Console.WriteLine("Clear everything:    reset");
            Console.WriteLine("Switch distance rule: metric chebyshev | metric euclidean");
            Console.WriteLine("Quit:                exit");
            Console.WriteLine();
            Console.WriteLine("Chebyshev = blocky/square rings (max axis offset). Euclidean = round rings (straight-line distance).");
            Console.WriteLine();
        }

        static void TryAddReading(string[] tokens)
        {
            if (!double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            {
                Console.WriteLine("Couldn't parse x/y/z as numbers. Format:  x y z letter");
                return;
            }

            string range = tokens[3].Trim().ToLowerInvariant();
            if (!Bands.ContainsKey(range))
            {
                Console.WriteLine("Range must be one of: closest, close, near, far, farfar, nothing.");
                return;
            }

            var (min, max) = Bands[range];
            readings.Add(new Reading { X = x, Y = y, Z = z, MinDist = min, MaxDist = max, Range = range });

            Console.WriteLine($"Added reading #{readings.Count}: pos=({x},{y},{z}) range={range} -> distance {min}-{max}");
            ComputeAndPrintEstimate();
        }

        static void ListReadings()
        {
            if (readings.Count == 0)
            {
                Console.WriteLine("No readings yet.");
                return;
            }
            for (int i = 0; i < readings.Count; i++)
            {
                var r = readings[i];
                Console.WriteLine($"  #{i + 1}: ({r.X},{r.Y},{r.Z}) range={r.Range} -> {r.MinDist}-{r.MaxDist}");
            }
        }

        // Returns distance between a candidate point and a reading position, per current metric
        static double Distance(double x, double y, double z, Reading r)
        {
            double dx = Math.Abs(x - r.X);
            double dy = Math.Abs(y - r.Y);
            double dz = Math.Abs(z - r.Z);

            if (metric == DistanceMetric.Chebyshev)
                return Math.Max(dx, Math.Max(dy, dz));
            else
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static void ComputeAndPrintEstimate()
        {
            if (readings.Count == 0)
            {
                Console.WriteLine("No readings yet - nothing to estimate.");
                return;
            }

            // Step 1: intersect the axis-aligned bounding boxes implied by each reading.
            // For both metrics, "distance <= maxDist" implies the point lies within
            // [pos - maxDist, pos + maxDist] on every axis, so this bound is valid either way.
            double minX = double.NegativeInfinity, maxX = double.PositiveInfinity;
            double minY = double.NegativeInfinity, maxY = double.PositiveInfinity;
            double minZ = double.NegativeInfinity, maxZ = double.PositiveInfinity;

            foreach (var r in readings)
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
                Console.WriteLine("!! These readings are contradictory - no location satisfies all of them.");
                Console.WriteLine("   The reading was still added to the dataset (nothing gets thrown away");
                Console.WriteLine("   automatically). Use 'list' to review everything and 'delete N' to");
                Console.WriteLine("   remove whichever entry turned out to be wrong.");
                return;
            }

            if (double.IsInfinity(minX) || double.IsInfinity(maxX) ||
                double.IsInfinity(minY) || double.IsInfinity(maxY) ||
                double.IsInfinity(minZ) || double.IsInfinity(maxZ))
            {
                Console.WriteLine("Not enough info yet to bound a search area - 'nothing' readings only rule");
                Console.WriteLine("points OUT, they can't pin a region down by themselves. Add at least one");
                Console.WriteLine("black/gray/yellow/green/blue reading to establish a boundary.");
                return;
            }

            double sizeX = maxX - minX;
            double sizeY = maxY - minY;
            double sizeZ = maxZ - minZ;
            double volume = Math.Max(1, sizeX) * Math.Max(1, sizeY) * Math.Max(1, sizeZ);

            // Step 2: choose a grid step so total sample count stays near TargetSamples
            double step = Math.Max(1.0, Math.Cbrt(volume / TargetSamples));

            var validPoints = new List<(double x, double y, double z)>();

            for (double x = minX; x <= maxX; x += step)
            {
                for (double y = minY; y <= maxY; y += step)
                {
                    for (double z = minZ; z <= maxZ; z += step)
                    {
                        bool ok = true;
                        foreach (var r in readings)
                        {
                            // Round to the nearest whole block: the game reports a rounded
                            // distance reading, not the raw continuous value, so a true
                            // distance of e.g. 100.04 should still count as "100" and pass
                            // a 51-100 band. Comparing the raw float here would wrongly
                            // reject points that sit within rounding distance of an edge.
                            double d = Math.Round(Distance(x, y, z, r), MidpointRounding.AwayFromZero);
                            if (d < r.MinDist || d > r.MaxDist) { ok = false; break; }
                        }
                        if (ok) validPoints.Add((x, y, z));
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"[{readings.Count} reading(s), metric={metric}, grid step={step:0.##}]");

            if (validPoints.Count == 0)
            {
                Console.WriteLine("No sampled point satisfied every reading at this resolution.");
                Console.WriteLine("Either the readings are (nearly) contradictory, or the valid region is");
                Console.WriteLine("thinner than the current grid step - try 'estimate' again after adding");
                Console.WriteLine("another reading to shrink the search box, which increases resolution.");
                Console.WriteLine();
                return;
            }

            double avgX = validPoints.Average(p => p.x);
            double avgY = validPoints.Average(p => p.y);
            double avgZ = validPoints.Average(p => p.z);

            double lowX = validPoints.Min(p => p.x), highX = validPoints.Max(p => p.x);
            double lowY = validPoints.Min(p => p.y), highY = validPoints.Max(p => p.y);
            double lowZ = validPoints.Min(p => p.z), highZ = validPoints.Max(p => p.z);

            Console.WriteLine($"Estimated location (avg of {validPoints.Count} candidates): " +
                               $"({avgX:0.#}, {avgY:0.#}, {avgZ:0.#})");
            Console.WriteLine($"Possible region spans: X[{lowX:0.#},{highX:0.#}]  " +
                               $"Y[{lowY:0.#},{highY:0.#}]  Z[{lowZ:0.#},{highZ:0.#}]");
            Console.WriteLine();

            lastEstX = avgX; lastEstY = avgY; lastEstZ = avgZ;
            haveLastEstimate = true;
        }
    }
}
