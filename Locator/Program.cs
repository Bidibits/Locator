using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpawnLocator
{
    class Program
    {
        static readonly List<Reading> readings = new List<Reading>();
        static readonly PingTimer timer = new PingTimer();

        static RelicTier relic = Relics.Repaired;
        static int nextSeq = 1;

        // Part 2 baseline: what a single reading allows on its own, cached per reading id.
        // Each track measures against its OWN oldest reading, so every hunt starts at 100%
        // and falls from there - a second skeleton's track isn't scored against the first's.
        // A solo solve never changes unless the metric does, hence the cache.
        static readonly Dictionary<int, double> soloVolumeCache = new Dictionary<int, double>();

        // Confirmed against real in-game data: distance behaves as straight-line (Euclidean),
        // not blocky/cube-shell (Chebyshev). Calibrated against a known origin point with the
        // Repaired (Grade II) relic, whose bands are A 0-25, B 26-50, C 51-100, D 101-150,
        // E 151-200, F silent:
        //   B readings landed at ~26-28    -> matches B (26-50)
        //   C reading landed at ~100.04    -> matches C (51-100)
        //   D readings landed at ~101-105  -> matches D (101-150)
        static DistanceMetric metric = DistanceMetric.Euclidean;

        static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try { Console.Title = "Ghost Seek Locator"; } catch { }

            Ui.Line("=== Ghost Seek Locator ===", ConsoleColor.Cyan);
            Ui.Line("Triangulates a Praying Skeleton from Ghost Seek relic sound readings.");
            Ui.Line();

            AskForRelic();

            Ui.Line("Enter readings as:  x y z letter        e.g.  120 64 -30 C");
            Ui.Line("Commands: list | estimate | tracks | conflicts | delete N | found x y z | output");
            Ui.Line("          starttimer | ping | stoptimer | relic | bands | metric | reset | help | exit");
            Ui.Line();

            while (true)
            {
                Ui.Raw("> ");
                string? line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();

                // Bare Enter while the countdown runs is the fastest way to log a sound:
                // you hear it, you hit Enter.
                if (line.Length == 0)
                {
                    if (timer.Running) Ui.Line("  " + timer.Ping(), ConsoleColor.DarkCyan);
                    continue;
                }

                if (!Dispatch(line)) break;
            }

            timer.Stop();
        }

        // Returns false to quit.
        static bool Dispatch(string line)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string head = tokens[0].ToLowerInvariant();

            // "x y z found" has to be checked before the plain 4-token reading form.
            if (tokens.Length == 4 && tokens[3].Equals("found", StringComparison.OrdinalIgnoreCase))
            {
                HandleFound(tokens[0], tokens[1], tokens[2]);
                return true;
            }

            switch (head)
            {
                case "exit":
                case "quit":
                    return false;

                case "help":
                    PrintHelp();
                    return true;

                case "bands":
                    Relics.PrintTable(relic);
                    return true;

                case "relic":
                case "tier":
                case "seeker":
                case "seek":
                    ChangeRelic(tokens.Skip(1));
                    return true;

                case "reset":
                    readings.Clear();
                    soloVolumeCache.Clear();
                    nextSeq = 1;
                    Ui.Line("All readings cleared.");
                    return true;

                case "found":
                    if (tokens.Length == 4) HandleFound(tokens[1], tokens[2], tokens[3]);
                    else if (tokens.Length == 1) ClearEverythingAsFound();
                    else Ui.Line("Usage: found x y z   (or 'x y z found', or bare 'found' to wipe the board)");
                    return true;

                case "list":
                    ListReadings();
                    return true;

                case "conflicts":
                    ListConflicts();
                    return true;

                case "output":
                case "export":
                case "save":
                    WriteReport(tokens.Skip(1));
                    return true;

                case "tracks":
                case "estimate":
                    Estimate();
                    return true;

                case "delete":
                case "remove":
                    DeleteReading(tokens);
                    return true;

                case "metric":
                    ChangeMetric(tokens);
                    return true;

                case "starttimer":
                case "timerstart":
                    StartTimer(tokens);
                    return true;

                case "ping":
                case "heard":
                    Ui.Line("  " + timer.Ping(), ConsoleColor.DarkCyan);
                    return true;

                case "stoptimer":
                case "timerstop":
                    if (!timer.Running) Ui.Line("Timer isn't running.");
                    else timer.Stop();
                    return true;

                case "timer":
                    Ui.Line("  " + timer.Status());
                    return true;
            }

            if (tokens.Length == 4) TryAddReading(tokens);
            else Ui.Line("Didn't understand that. Type 'help' for usage.");

            return true;
        }

        // ---------- relic selection ----------

        static void AskForRelic()
        {
            Ui.Line("Which Ghost Seek are you using?");
            Relics.PrintChoices();
            Ui.Line("Type a grade (g1 / gii / tier3 / 2) or a name (refined).");

            while (true)
            {
                Ui.Raw("relic> ");
                string? input = Console.ReadLine();
                if (input == null) { relic = Relics.Repaired; break; }

                input = input.Trim();
                if (input.Length == 0)
                {
                    Ui.Line($"  Defaulting to {Relics.Repaired.Label}.", ConsoleColor.DarkGray);
                    relic = Relics.Repaired;
                    break;
                }

                var parsed = Relics.Parse(input);
                if (parsed == null)
                {
                    Ui.Line("  Didn't recognise that. Try: g1, g2, g3, gi, gii, giii, tier1, 2, makeshift, repaired, refined.", ConsoleColor.Yellow);
                    continue;
                }

                relic = parsed;
                break;
            }

            Ui.Line($"Using {relic.Label}.", ConsoleColor.Green);
            Relics.PrintTable(relic);
        }

        static void ChangeRelic(IEnumerable<string> rest)
        {
            string spec = string.Join(" ", rest).Trim();
            if (spec.Length == 0)
            {
                Ui.Line($"Currently using {relic.Label}.");
                Relics.PrintTable(relic);
                return;
            }

            var parsed = Relics.Parse(spec);
            if (parsed == null)
            {
                Ui.Line("Didn't recognise that relic. Try: g1 / gii / tier3 / makeshift / repaired / refined.", ConsoleColor.Yellow);
                return;
            }

            relic = parsed;
            Ui.Line($"Switched to {relic.Label}.", ConsoleColor.Green);
            if (readings.Count > 0)
                Ui.Line($"  Existing {readings.Count} reading(s) keep the distances they were entered with - only new entries use the table below.", ConsoleColor.DarkGray);
            Relics.PrintTable(relic);
        }

        static void ChangeMetric(string[] tokens)
        {
            if (tokens.Length < 2)
            {
                Ui.Line($"Current metric: {metric}. Use 'metric chebyshev' or 'metric euclidean'.");
                return;
            }

            if (tokens[1].StartsWith("cheb", StringComparison.OrdinalIgnoreCase))
            {
                metric = DistanceMetric.Chebyshev;
                Ui.Line("Metric set to Chebyshev (blocky / cube-shell rings).");
            }
            else if (tokens[1].StartsWith("euc", StringComparison.OrdinalIgnoreCase))
            {
                metric = DistanceMetric.Euclidean;
                Ui.Line("Metric set to Euclidean (round / sphere-shell rings).");
            }
            else
            {
                Ui.Line("Unknown metric. Use 'metric chebyshev' or 'metric euclidean'.");
                return;
            }

            soloVolumeCache.Clear();   // solo volumes are metric-dependent
            if (readings.Count > 0) Estimate();
        }

        // ---------- readings ----------

        static void TryAddReading(string[] tokens)
        {
            if (!TryCoords(tokens[0], tokens[1], tokens[2], out double x, out double y, out double z))
            {
                Ui.Line("Couldn't parse x/y/z as numbers. Format:  x y z letter");
                return;
            }

            string raw = tokens[3].Trim();
            Band? band = ResolveBand(raw);
            if (band == null)
            {
                string letters = string.Join(" ", relic.Bands.Select(b => b.Letter));
                Ui.Line($"'{raw}' isn't a band on the {relic.Label}. Valid: {letters} (or 'nothing'). Type 'bands' for the table.", ConsoleColor.Yellow);
                return;
            }

            var reading = new Reading
            {
                Seq = nextSeq++,
                X = x, Y = y, Z = z,
                MinDist = band.Min,
                MaxDist = band.Max,
                Letter = band.Letter,
                RelicName = relic.Name
            };
            readings.Add(reading);

            Ui.Line($"Added #{reading.Seq}: {reading.PosText}  {band.Letter} -> {band.RangeText}");
            Estimate();
        }

        // Accepts the band letter, or 'nothing'/'none'/'silent'/'x'/'-' for out of range -
        // so you never have to remember whether silence is E, F or G on this relic.
        static Band? ResolveBand(string raw)
        {
            string s = raw.ToLowerInvariant();
            if (s == "nothing" || s == "none" || s == "silent" || s == "silence" || s == "x" || s == "-")
                return relic.Silent;

            if (s.Length == 1 && char.IsLetter(s[0])) return relic.Find(s[0]);
            return null;
        }

        static bool TryCoords(string a, string b, string c, out double x, out double y, out double z)
        {
            x = y = z = 0;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        static void ListReadings()
        {
            if (readings.Count == 0)
            {
                Ui.Line("No readings yet.");
                return;
            }

            var conf = Analysis.Confidence(readings, metric);
            var tracks = Analysis.BuildTracks(readings, metric);
            var trackOf = new Dictionary<int, int>();
            foreach (var t in tracks)
                foreach (var r in t.Readings) trackOf[r.Seq] = t.Index;

            Ui.Line();
            Ui.Line($"  {"id",-4} {"position",-26} {"band",-4} {"range",-15} {"track",-6} confidence");
            foreach (var r in readings.OrderBy(r => r.Seq))
            {
                double c = conf[r.Seq];
                Ui.Raw($"  #{r.Seq,-3} {r.PosText,-26} {r.Letter,-4} {r.BandText,-15} {trackOf[r.Seq],-6} ");
                lock (Ui.Gate)
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = Ui.ConfidenceColor(c);
                    Console.WriteLine($"{Ui.Bar(c, 10)} {c * 100,3:0}%");
                    Console.ForegroundColor = prev;
                }
            }
            Ui.Line();
        }

        static void ListConflicts()
        {
            if (readings.Count < 2)
            {
                Ui.Line("Need at least two readings before anything can conflict.");
                return;
            }

            var pairs = Analysis.Conflicts(readings, metric);
            if (pairs.Count == 0)
            {
                Ui.Line("No conflicts - every reading is compatible with every other.", ConsoleColor.Green);
                return;
            }

            Ui.Line();
            Ui.Line($"{pairs.Count} conflicting pair(s) - no single point can satisfy both halves:", ConsoleColor.Yellow);
            foreach (var (a, b) in pairs)
            {
                double d = a.DistanceTo(b.X, b.Y, b.Z, metric);
                Ui.Line($"  #{a.Seq} {a.Letter} ({a.BandText})  vs  #{b.Seq} {b.Letter} ({b.BandText})   - they sit {d:0.#} blocks apart");
            }
            Ui.Line();
        }

        static void DeleteReading(string[] tokens)
        {
            if (tokens.Length != 2 || !int.TryParse(tokens[1], out int id))
            {
                Ui.Line("Usage: delete N   (N is the #id shown by 'list'; ids are stable and never reused)");
                return;
            }

            var target = readings.FirstOrDefault(r => r.Seq == id);
            if (target == null)
            {
                Ui.Line($"No reading #{id}. Use 'list' to see what's there.");
                return;
            }

            readings.Remove(target);
            Ui.Line($"Removed #{id}: {target.PosText} {target.Letter}.");
            if (readings.Count > 0) Estimate();
            else Ui.Line("No readings left.");
        }

        // ---------- part 5: found ----------

        static void HandleFound(string sx, string sy, string sz)
        {
            if (!TryCoords(sx, sy, sz, out double fx, out double fy, out double fz))
            {
                Ui.Line("Couldn't parse those coordinates. Usage: found x y z   (or 'x y z found')");
                return;
            }

            // Work out which track was pointing here before anything is removed, so the
            // accuracy report compares against the estimate that actually led you there.
            var tracks = Analysis.BuildTracks(readings, metric);
            var matched = readings.Where(r => r.Satisfies(fx, fy, fz, metric)).ToList();

            Ui.Line();
            Ui.Line($"Skeleton confirmed at ({fx:0.#}, {fy:0.#}, {fz:0.#}).", ConsoleColor.Green);

            if (matched.Count == 0)
            {
                Ui.Line("  None of your readings are consistent with that spot, so nothing was removed.", ConsoleColor.Yellow);
                Ui.Line("  Either a coordinate is mistyped, or every reading you have describes a different skeleton.");
                Ui.Line("  Use 'reset' if you want to start clean.");
                return;
            }

            var owner = tracks
                .OrderByDescending(t => t.Readings.Count(r => matched.Contains(r)))
                .First();

            owner.Result = Solver.Solve(owner.Readings, metric);
            if (owner.Result.Bounded && owner.Result.Count > 0)
            {
                double err = Math.Sqrt(Math.Pow(fx - owner.Result.Cx, 2)
                                     + Math.Pow(fy - owner.Result.Cy, 2)
                                     + Math.Pow(fz - owner.Result.Cz, 2));
                Ui.Line($"  Track {owner.Index} estimated ({owner.Result.Cx:0.#}, {owner.Result.Cy:0.#}, {owner.Result.Cz:0.#}) - off by {err:0.#} blocks.");
            }

            foreach (var r in matched) readings.Remove(r);
            Ui.Line($"  Retired {matched.Count} reading(s) that pointed here: {string.Join(" ", matched.Select(r => "#" + r.Seq))}");

            if (readings.Count == 0)
            {
                Ui.Line("  Board is clear. Enter readings whenever you're ready for the next one.");
                return;
            }

            Ui.Line($"  {readings.Count} reading(s) left over - those were describing something else.", ConsoleColor.Cyan);
            Estimate();
        }

        static void ClearEverythingAsFound()
        {
            readings.Clear();
            soloVolumeCache.Clear();
            nextSeq = 1;
            Ui.Line("Marked found and wiped every reading. Starting fresh.");
        }

        // ---------- the estimate ----------

        static void Estimate()
        {
            if (readings.Count == 0)
            {
                Ui.Line("No readings yet - nothing to estimate.");
                return;
            }

            var tracks = Analysis.BuildTracks(readings, metric);
            foreach (var t in tracks) t.Result = Solver.Solve(t.Readings, metric);

            Ui.Line();
            Ui.Line($"[{readings.Count} reading(s) - {relic.Label} - {metric}]", ConsoleColor.DarkGray);

            if (tracks.Count > 1)
            {
                Ui.Line();
                Ui.Line($"!! Your readings split into {tracks.Count} groups that cannot all describe one skeleton.", ConsoleColor.Yellow);
                Ui.Line("   Most likely you have picked up two separate spawns. Each group is solved on its own");
                Ui.Line("   below; 'conflicts' shows exactly which pairs disagree.");
            }

            foreach (var t in tracks) PrintTrack(t, tracks.Count);
            Ui.Line();
        }

        // What the earliest bounded reading of this track allows on its own - the 100% mark
        // that track's progress is measured against.
        static double BaselineFor(Track t)
        {
            foreach (var r in t.Readings)
            {
                if (r.IsSilent) continue;   // silence rules points out, it can't bound anything

                if (!soloVolumeCache.TryGetValue(r.Seq, out double v))
                {
                    var solo = Solver.Solve(new[] { r }, metric);
                    v = solo.Bounded ? solo.Volume : 0;
                    soloVolumeCache[r.Seq] = v;
                }
                if (v > 0) return v;
            }
            return 0;
        }

        static void PrintTrack(Track t, int trackCount)
        {
            var r = t.Result!;
            var conf = Analysis.Confidence(readings, metric);
            double avgConf = t.Readings.Average(x => conf[x.Seq]);

            Ui.Line();
            string header = trackCount > 1
                ? $"Track {t.Index} - {t.Readings.Count} reading(s): {t.IdList}"
                : $"Solution - {t.Readings.Count} reading(s): {t.IdList}";
            Ui.Line(header, trackCount > 1 ? ConsoleColor.Cyan : ConsoleColor.White);
            Ui.Line($"  Group confidence  {Ui.Bar(avgConf, 10)} {avgConf * 100:0}%");

            if (r.BoxEmpty)
            {
                Ui.Line("  These readings are contradictory - no location satisfies all of them.", ConsoleColor.Red);
                Ui.Line("  Nothing was thrown away; use 'list' and 'delete N' to drop whichever entry was wrong.");
                return;
            }

            if (!r.Bounded)
            {
                Ui.Line("  Not enough info to bound a search area - silent readings only rule points OUT.", ConsoleColor.Yellow);
                Ui.Line("  Add at least one reading where you actually heard the relic.");
                return;
            }

            if (r.Count == 0)
            {
                Ui.Line("  No sampled point satisfied every reading at this resolution.", ConsoleColor.Yellow);
                Ui.Line("  Either the readings are nearly contradictory, or the valid region is thinner than");
                Ui.Line("  the grid step - add another reading to shrink the box, which raises resolution.");
                return;
            }

            Ui.Line($"  Estimate          ({r.Cx:0.#}, {r.Cy:0.#}, {r.Cz:0.#})", ConsoleColor.Green);
            Ui.Line($"  Region            X[{r.MinX:0.#}, {r.MaxX:0.#}]  Y[{r.MinY:0.#}, {r.MaxY:0.#}]  Z[{r.MinZ:0.#}, {r.MaxZ:0.#}]");

            double baseline = BaselineFor(t);
            if (baseline > 0)
            {
                double frac = r.Volume / baseline;
                Ui.Line($"  Search space      {Ui.Bar(frac, 20)} {frac * 100:0.##}% of reading #{t.Readings[0].Seq} alone");
            }
            Ui.Line($"                    ~{r.Volume:N0} blocks³   ({r.Count:N0} candidates @ step {r.Step:0.##})", ConsoleColor.DarkGray);

            // Part 3: even a consistent reading set can leave two separated pockets.
            if (r.Blobs.Count > 1)
            {
                var top = r.Blobs.Take(4).ToList();
                double gap = Solver.Separation(top[0], top[1]);
                Ui.Line();
                Ui.Line($"  ! Valid area splits into {r.Blobs.Count} separated pockets, {gap:0} blocks apart at the widest.", ConsoleColor.Yellow);
                Ui.Line("    Could be two skeletons, or just not enough readings to break the symmetry yet.");
                for (int i = 0; i < top.Count; i++)
                {
                    var b = top[i];
                    double share = (double)b.Count / r.Count * 100;
                    Ui.Line($"      pocket {i + 1}  ({b.Cx,8:0.#}, {b.Cy,8:0.#}, {b.Cz,8:0.#})  {share,5:0.#}% of candidates");
                    Ui.Line($"                 {b.Span}", ConsoleColor.DarkGray);
                }
            }
        }

        // ---------- export ----------

        static void WriteReport(IEnumerable<string> rest)
        {
            string name = string.Join(" ", rest).Trim().Trim('"');
            if (name.Length == 0)
                name = $"locator-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            else if (!Path.HasExtension(name))
                name += ".txt";

            string path;
            try
            {
                path = Path.GetFullPath(name);
            }
            catch (Exception ex)
            {
                Ui.Line($"That isn't a usable filename: {ex.Message}", ConsoleColor.Yellow);
                return;
            }

            // Solve before reporting so the file carries the same numbers the screen would,
            // rather than whatever was left over from the last estimate.
            var tracks = Analysis.BuildTracks(readings, metric);
            foreach (var t in tracks) t.Result = Solver.Solve(t.Readings, metric);

            string report = Export.BuildReport(relic, metric, readings, tracks, BaselineFor);

            try
            {
                File.WriteAllText(path, report, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Ui.Line($"Couldn't write that file: {ex.Message}", ConsoleColor.Yellow);
                return;
            }

            Ui.Line($"Wrote {readings.Count} reading(s) and {tracks.Count} track(s) to:", ConsoleColor.Green);
            Ui.Line($"  {path}");
        }

        // ---------- timer ----------

        static void StartTimer(string[] tokens)
        {
            double? explicitInterval = null;
            if (tokens.Length >= 2 &&
                double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            {
                if (secs < 1 || secs > 120)
                {
                    Ui.Line("Interval should be between 1 and 120 seconds.");
                    return;
                }
                explicitInterval = secs;
            }

            timer.Start(explicitInterval);
            Ui.Line("Loop running - it shows at the right edge of the line and in the window title.", ConsoleColor.DarkCyan);
            Ui.Line("  The countdown refills the moment it empties and the loop counter ticks up.");
            Ui.Line("  When you hear the relic, hit Enter on an empty line (or type 'ping') to re-align.");
            Ui.Line("  It learns the real interval from the gaps between your pings; you don't have to");
            Ui.Line("  catch every sound. 'timer' for the count, 'stoptimer' to end.");
        }

        // ---------- help ----------

        static void PrintHelp()
        {
            Ui.Line();
            Ui.Line("Readings", ConsoleColor.Cyan);
            Ui.Line("  x y z letter         add a reading      e.g.  -12 70 305 C");
            Ui.Line("  x y z nothing        no sound at all (also: none, silent, x, -)");
            Ui.Line("  list                 every reading with its track and confidence");
            Ui.Line("  conflicts            which pairs of readings disagree, and by how much");
            Ui.Line("  delete N             drop reading #N (ids are stable, they never shift)");
            Ui.Line("  estimate / tracks    re-solve without adding anything");
            Ui.Line("  output [file]        write the whole board to a text file (default: timestamped)");
            Ui.Line("  reset                wipe everything");
            Ui.Line();
            Ui.Line("Finding one", ConsoleColor.Cyan);
            Ui.Line("  found x y z          confirm a kill; retires only the readings that pointed there,");
            Ui.Line("  x y z found          leaving anything that was describing a different skeleton");
            Ui.Line("  found                wipe the board entirely");
            Ui.Line();
            Ui.Line("Relic", ConsoleColor.Cyan);
            Ui.Line("  relic g1 / gii / tier3 / refined      switch relic grade");
            Ui.Line("  bands                                 show the current band table");
            Ui.Line("  metric euclidean | chebyshev          round vs blocky rings");
            Ui.Line();
            Ui.Line("Sound timer", ConsoleColor.Cyan);
            Ui.Line("  starttimer [secs]    start the loop (default 20s); it refills and counts each cycle");
            Ui.Line("  <Enter> or ping      you heard it - re-align the loop and calibrate the interval");
            Ui.Line("  timer                loop number, time to next sound, how long it's been running");
            Ui.Line("  stoptimer            stop, and report the total loop count");
            Ui.Line();
            Ui.Line("A is always the closest ring. Letters step outward; the letter after the last");
            Ui.Line("audible ring means silence, which you can always just type as 'nothing'.");
            Ui.Line();
        }
    }
}
