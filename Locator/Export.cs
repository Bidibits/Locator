using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpawnLocator
{
    // Dumps the current board to a plain-text report: everything 'list', 'conflicts' and
    // 'estimate' would show, plus a tab-separated block that pastes straight into a
    // spreadsheet. Deliberately ASCII-only and invariant-culture, so the file reads the
    // same everywhere and numbers parse the same way the program parses input.
    static class Export
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string BuildReport(
            RelicTier relic,
            DistanceMetric metric,
            IReadOnlyList<Reading> readings,
            IReadOnlyList<Track> tracks,
            Func<Track, double> baselineFor)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== Ghost Seek Locator - session export ===");
            sb.AppendLine($"Generated   {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)}");
            sb.AppendLine($"Relic       {relic.Label} (reaches {relic.Reach.ToString("0", Inv)} blocks)");
            sb.AppendLine($"Metric      {metric}");
            sb.AppendLine($"Readings    {readings.Count}");
            sb.AppendLine();

            AppendBands(sb, relic);

            if (readings.Count == 0)
            {
                sb.AppendLine("No readings on the board.");
                return sb.ToString();
            }

            var conf = Analysis.Confidence(readings, metric);
            var trackOf = new Dictionary<int, int>();
            foreach (var t in tracks)
                foreach (var r in t.Readings) trackOf[r.Seq] = t.Index;

            AppendReadings(sb, readings, conf, trackOf);
            AppendTsv(sb, readings, conf, trackOf);
            AppendConflicts(sb, readings, metric);

            foreach (var t in tracks) AppendTrack(sb, t, tracks.Count, conf, baselineFor(t));

            sb.AppendLine("Bands are inclusive block distances. 'silent' means the relic made no sound,");
            sb.AppendLine("which only rules locations out - it cannot bound a region on its own.");
            return sb.ToString();
        }

        static void AppendBands(StringBuilder sb, RelicTier relic)
        {
            sb.AppendLine("--- Band table ---");
            foreach (var b in relic.Bands)
                sb.AppendLine($"  {b.Letter}   {b.RangeText}");
            sb.AppendLine();
        }

        static void AppendReadings(StringBuilder sb, IReadOnlyList<Reading> readings,
                                   Dictionary<int, double> conf, Dictionary<int, int> trackOf)
        {
            sb.AppendLine($"--- Readings ({readings.Count}) ---");
            sb.AppendLine($"  {"id",-5} {"x",10} {"y",10} {"z",10}  {"band",-5} {"range",-14} {"track",-6} conf");
            foreach (var r in readings.OrderBy(r => r.Seq))
            {
                sb.AppendLine(
                    $"  {"#" + r.Seq,-5} {r.X.ToString("0.##", Inv),10} {r.Y.ToString("0.##", Inv),10} {r.Z.ToString("0.##", Inv),10}"
                  + $"  {r.Letter,-5} {r.BandText,-14} {trackOf[r.Seq],-6} {(conf[r.Seq] * 100).ToString("0", Inv)}%");
            }
            sb.AppendLine();
        }

        static void AppendTsv(StringBuilder sb, IReadOnlyList<Reading> readings,
                              Dictionary<int, double> conf, Dictionary<int, int> trackOf)
        {
            sb.AppendLine("--- Tab-separated (paste into a spreadsheet) ---");
            sb.AppendLine("id\tx\ty\tz\tband\tmin\tmax\ttrack\tconfidence\trelic");
            foreach (var r in readings.OrderBy(r => r.Seq))
            {
                string max = r.IsSilent ? "" : r.MaxDist.ToString("0.##", Inv);
                sb.AppendLine(string.Join("\t",
                    r.Seq.ToString(Inv),
                    r.X.ToString("0.##", Inv),
                    r.Y.ToString("0.##", Inv),
                    r.Z.ToString("0.##", Inv),
                    r.Letter.ToString(),
                    r.MinDist.ToString("0.##", Inv),
                    max,
                    trackOf[r.Seq].ToString(Inv),
                    conf[r.Seq].ToString("0.###", Inv),
                    r.RelicName));
            }
            sb.AppendLine();
            sb.AppendLine("  (an empty 'max' means the reading was silent - no upper bound)");
            sb.AppendLine();
        }

        static void AppendConflicts(StringBuilder sb, IReadOnlyList<Reading> readings, DistanceMetric metric)
        {
            var pairs = Analysis.Conflicts(readings, metric);
            sb.AppendLine($"--- Conflicts ({pairs.Count}) ---");
            if (pairs.Count == 0)
            {
                sb.AppendLine("  None - every reading is compatible with every other.");
            }
            else
            {
                foreach (var (a, b) in pairs)
                {
                    double d = a.DistanceTo(b.X, b.Y, b.Z, metric);
                    string left = $"#{a.Seq} {a.Letter} ({a.BandText})";
                    string right = $"#{b.Seq} {b.Letter} ({b.BandText})";
                    sb.AppendLine($"  {left,-24} vs  {right,-24} {d.ToString("0.#", Inv)} blocks apart");
                }
            }
            sb.AppendLine();
        }

        static void AppendTrack(StringBuilder sb, Track t, int trackCount,
                                Dictionary<int, double> conf, double baseline)
        {
            var r = t.Result;
            string title = trackCount > 1 ? $"Track {t.Index}" : "Solution";
            sb.AppendLine($"--- {title}: {t.Readings.Count} reading(s) {t.IdList} ---");

            double avg = t.Readings.Average(x => conf[x.Seq]);
            sb.AppendLine($"  Group confidence  {(avg * 100).ToString("0", Inv)}%");

            if (r == null)
            {
                sb.AppendLine("  Not solved.");
                sb.AppendLine();
                return;
            }

            if (r.BoxEmpty)
            {
                sb.AppendLine("  CONTRADICTORY - no location satisfies all of these readings.");
                sb.AppendLine();
                return;
            }

            if (!r.Bounded)
            {
                sb.AppendLine("  UNBOUNDED - silent readings only rule points out; nothing pins a region.");
                sb.AppendLine();
                return;
            }

            if (r.Count == 0)
            {
                sb.AppendLine("  NO CANDIDATES at this resolution - readings are nearly contradictory, or the");
                sb.AppendLine("  valid region is thinner than the grid step.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"  Estimate          ({r.Cx.ToString("0.#", Inv)}, {r.Cy.ToString("0.#", Inv)}, {r.Cz.ToString("0.#", Inv)})");
            sb.AppendLine($"  Region            X[{r.MinX.ToString("0.#", Inv)}, {r.MaxX.ToString("0.#", Inv)}]"
                        + $"  Y[{r.MinY.ToString("0.#", Inv)}, {r.MaxY.ToString("0.#", Inv)}]"
                        + $"  Z[{r.MinZ.ToString("0.#", Inv)}, {r.MaxZ.ToString("0.#", Inv)}]");
            if (baseline > 0)
                sb.AppendLine($"  Search space      {(r.Volume / baseline * 100).ToString("0.##", Inv)}% of reading #{t.Readings[0].Seq} alone");
            sb.AppendLine($"  Volume            ~{r.Volume.ToString("N0", Inv)} blocks^3"
                        + $"   ({r.Count.ToString("N0", Inv)} candidates @ step {r.Step.ToString("0.##", Inv)})");

            if (r.Blobs.Count > 1)
            {
                sb.AppendLine($"  Pockets           {r.Blobs.Count} separated regions,"
                            + $" {Solver.Separation(r.Blobs[0], r.Blobs[1]).ToString("0", Inv)} blocks apart at the widest");
                for (int i = 0; i < r.Blobs.Count && i < 4; i++)
                {
                    var b = r.Blobs[i];
                    sb.AppendLine($"    pocket {i + 1}  ({b.Cx.ToString("0.#", Inv)}, {b.Cy.ToString("0.#", Inv)}, {b.Cz.ToString("0.#", Inv)})"
                                + $"  {((double)b.Count / r.Count * 100).ToString("0.#", Inv)}% of candidates");
                    sb.AppendLine($"              X[{b.MinX.ToString("0.#", Inv)}, {b.MaxX.ToString("0.#", Inv)}]"
                                + $"  Y[{b.MinY.ToString("0.#", Inv)}, {b.MaxY.ToString("0.#", Inv)}]"
                                + $"  Z[{b.MinZ.ToString("0.#", Inv)}, {b.MaxZ.ToString("0.#", Inv)}]");
                }
            }

            sb.AppendLine();
        }
    }
}
