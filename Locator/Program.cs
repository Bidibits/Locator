using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpawnLocator
{
    partial class Program
    {
        static readonly List<Reading> readings = new List<Reading>();
        static readonly List<Kill> kills = new List<Kill>();
        static readonly List<Session> sessions = new List<Session>();
        static Session currentSession = null!;
        static readonly PingTimer timer = new PingTimer();

        static SeekerTier seeker = Seekers.Repaired;
        static int nextSeq = 1;
        static int nextKillId = 1;
        static bool replaying;

        // Part 2 baseline: what a single reading allows on its own, cached per reading id.
        // Each track measures against its OWN oldest reading, so every hunt starts at 100%
        // and falls from there - a second skeleton's track isn't scored against the first's.
        // A solo solve never changes unless the metric does, hence the cache.
        static readonly Dictionary<int, double> soloVolumeCache = new Dictionary<int, double>();

        // Confirmed against real in-game data: distance behaves as straight-line (Euclidean),
        // not blocky/cube-shell (Chebyshev). Calibrated against a known origin point with the
        // Repaired (Grade II) seeker, whose bands are A 0-25, B 26-50, C 51-100, D 101-150,
        // E 151-200, F silent:
        //   B readings landed at ~26-28    -> matches B (26-50)
        //   C reading landed at ~100.04    -> matches C (51-100)
        //   D readings landed at ~101-105  -> matches D (101-150)
        static DistanceMetric metric = DistanceMetric.Euclidean;

        static List<Reading> Active() => readings.Where(r => !r.Retired).ToList();

        static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try { Console.Title = "Ghost Seek Locator"; } catch { }

            Ui.Line("=== Ghost Seek Locator ===", ConsoleColor.Cyan);
            Ui.Line("Triangulates a Praying Skeleton from Ghost Seek sound readings.");
            Ui.Line();
            Ui.Line("This session's activity (every command you type, and its outcome) stays in memory", ConsoleColor.DarkYellow);
            Ui.Line("for this run only. It is never written anywhere and never leaves this machine unless", ConsoleColor.DarkYellow);
            Ui.Line("you explicitly run 'output' (or 'export'/'save') to save it yourself.", ConsoleColor.DarkYellow);
            Ui.Line();

            currentSession = new Session { StartedAt = DateTime.Now };
            sessions.Add(currentSession);

            AutoSelectSeeker();

            Ui.Line("Enter readings as:  x y z letter        e.g.  120 64 -30 C");
            Ui.Line("Commands: list | estimate | tracks | conflicts | delete N | found x y z | history");
            Ui.Line("          output | import | sessions | simulate | starttimer | ping | stoptimer");
            Ui.Line("          seeker | bands | metric | reset | web | help | exit");
            Ui.Line();

            while (true)
            {
                Ui.Raw("> ");
                string? line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();

                if (!ProcessLine(line)) break;
            }

            timer.Stop();
        }

        // Shared by the live loop and by replay, so a blank Enter (ping) behaves identically
        // either way and both paths log through the same place. Locked for the same reason
        // Dispatch() is - a browser request logging an entry at the same moment must not race
        // this appending to the same activity list.
        static bool ProcessLine(string line)
        {
            if (line.Length == 0)
            {
                string outcome;
                lock (commandLock)
                {
                    if (replaying)
                    {
                        outcome = "ping (replayed, timer not touched)";
                    }
                    else if (timer.Running)
                    {
                        outcome = timer.Ping();
                        Ui.Line("  " + outcome, ConsoleColor.DarkCyan);
                    }
                    else
                    {
                        outcome = "no-op, timer not running";
                    }
                    if (!replaying) RecordActivity(line, outcome);
                }
                return true;
            }
            var (cont, _) = Dispatch(line);
            return cont;
        }

        // Locked so a browser request (see Program.Web.cs) and a keystroke here can never both be
        // mutating readings/kills/sessions - or both appending to the same activity log - at once.
        // Returns the outcome too (not just whether to keep running) so Program.Web.cs can report
        // it back to the browser exactly as logged.
        static (bool cont, string outcome) Dispatch(string line)
        {
            bool cont; string outcome;
            lock (commandLock)
            {
                (cont, outcome) = DispatchCore(line);
                if (!replaying) RecordActivity(line, outcome);
            }
            return (cont, outcome);
        }

        static void RecordActivity(string rawInput, string outcome)
        {
            currentSession.Entries.Add(new ActivityLogEntry { Timestamp = Clock.Now, RawInput = rawInput, Outcome = outcome });
        }

        // Returns (keep running?, short outcome for the activity log).
        static (bool cont, string outcome) DispatchCore(string line)
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return (true, "no-op");
            string head = tokens[0].ToLowerInvariant();

            // "x y z found" has to be checked before the plain 4-token reading form.
            if (tokens.Length == 4 && tokens[3].Equals("found", StringComparison.OrdinalIgnoreCase))
                return (true, HandleFound(tokens[0], tokens[1], tokens[2]));

            switch (head)
            {
                case "exit":
                case "quit":
                    return (false, "session ended");

                case "help":
                    PrintHelp();
                    return (true, "help shown");

                case "bands":
                    Seekers.PrintTable(seeker);
                    return (true, "band table shown");

                case "web":
                    {
                        string outcome = StartWeb();
                        Ui.Line(outcome.StartsWith("rejected") ? outcome : $"Web UI running - opened {outcome.Substring("web server started at ".Length)} in your browser.",
                            outcome.StartsWith("rejected") ? ConsoleColor.Yellow : ConsoleColor.Green);
                        if (!outcome.StartsWith("rejected"))
                            Ui.Line("  The terminal still works too - both stay in sync, same board, same session log.", ConsoleColor.DarkGray);
                        return (true, outcome);
                    }

                case "seeker":
                case "seek":
                case "ghost":           // "ghost seeker gi" - the leftover "seeker" word is stripped by the parser below
                case "ghostseeker":
                case "tier":
                case "relic":   // legacy alias, not advertised - keeps older exported session logs replayable
                    return (true, ChangeSeeker(tokens.Skip(1)));

                case "reset":
                    return (true, DoReset());

                case "found":
                    if (tokens.Length == 4) return (true, HandleFound(tokens[1], tokens[2], tokens[3]));
                    if (tokens.Length == 1) return (true, ClearEverythingAsFound());
                    Ui.Line("Usage: found x y z   (or 'x y z found', or bare 'found' to retire everything)");
                    return (true, "rejected: bad 'found' usage");

                case "list":
                    return (true, ListReadings());

                case "conflicts":
                    return (true, ListConflicts());

                case "history":
                    return (true, ShowHistory());

                case "output":
                case "export":
                case "save":
                    return (true, WriteReport(tokens.Skip(1)));

                case "import":
                case "load":
                    return (true, ImportFile(tokens.Skip(1)));

                case "sessions":
                    return (true, ShowSessions());

                case "simulate":
                    return (true, Simulate(tokens.Skip(1)));

                case "tracks":
                case "estimate":
                    return (true, Estimate());

                case "delete":
                case "remove":
                    return (true, DeleteReading(tokens));

                case "metric":
                    return (true, ChangeMetric(tokens));

                case "starttimer":
                case "timerstart":
                    if (replaying) return (true, "starttimer (skipped during replay)");
                    return (true, StartTimer(tokens));

                case "ping":
                case "heard":
                    if (replaying) return (true, "ping (skipped during replay)");
                    {
                        string outcome = timer.Ping();
                        Ui.Line("  " + outcome, ConsoleColor.DarkCyan);
                        return (true, outcome);
                    }

                case "stoptimer":
                case "timerstop":
                    if (replaying) return (true, "stoptimer (skipped during replay)");
                    if (!timer.Running) { Ui.Line("Timer isn't running."); return (true, "rejected: timer not running"); }
                    timer.Stop();
                    return (true, "timer stopped");

                case "timer":
                    if (replaying) return (true, "timer status (skipped during replay)");
                    {
                        string status = timer.Status();
                        Ui.Line("  " + status);
                        return (true, "timer status shown");
                    }
            }

            if (tokens.Length == 4) return (true, TryAddReading(tokens));

            Ui.Line("Didn't understand that. Type 'help' for usage.");
            return (true, "rejected: unrecognized command");
        }

        // ---------- seeker selection ----------

        // No interactive prompt at startup - it used to block here waiting on Console.ReadLine(),
        // which meant 'web' wasn't reachable until that was answered. Auto-picks the best seeker
        // (longest reach, finest bands) instead; 'seeker <spec>' still switches it any time.
        static void AutoSelectSeeker()
        {
            seeker = Seekers.Refined;
            Ui.Line($"Using {seeker.Label} by default - the longest-reaching, most precise seeker. Type 'seeker <grade>' any time to switch (e.g. 'seeker gii').", ConsoleColor.Green);
            Seekers.PrintTable(seeker);

            // Not routed through Dispatch, so it logs its own entry - synthesized into
            // Dispatch-replayable form ("seeker gi") rather than skipped, so an exported log
            // still records which seeker a session started on and 'simulate' can feed it back in.
            RecordActivity("seeker gi", $"seeker set to {seeker.Label}");
        }

        static string ChangeSeeker(IEnumerable<string> rest)
        {
            string spec = string.Join(" ", rest).Trim();
            if (spec.Length == 0)
            {
                Ui.Line($"Currently using {seeker.Label}.");
                Seekers.PrintTable(seeker);
                return $"seeker table shown ({seeker.Label})";
            }

            var parsed = Seekers.Parse(spec);
            if (parsed == null)
            {
                Ui.Line("Didn't recognise that seeker. Try: g1 / gii / t3 / makeshift / repaired / refined.", ConsoleColor.Yellow);
                return $"rejected: unrecognized seeker '{spec}'";
            }

            seeker = parsed;
            Ui.Line($"Switched to {seeker.Label}.", ConsoleColor.Green);
            int activeCount = Active().Count;
            if (activeCount > 0)
                Ui.Line($"  Existing {activeCount} reading(s) keep the distances they were entered with - only new entries use the table below.", ConsoleColor.DarkGray);
            Seekers.PrintTable(seeker);
            return $"seeker set to {seeker.Label}";
        }

        static string ChangeMetric(string[] tokens)
        {
            if (tokens.Length < 2)
            {
                Ui.Line($"Current metric: {metric}. Use 'metric chebyshev' or 'metric euclidean'.");
                return $"metric status shown ({metric})";
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
                return "rejected: unknown metric";
            }

            soloVolumeCache.Clear();   // solo volumes are metric-dependent
            if (Active().Count > 0) Estimate();
            return $"metric set to {metric}";
        }

        // ---------- readings ----------

        static string TryAddReading(string[] tokens)
        {
            if (!TryCoords(tokens[0], tokens[1], tokens[2], out double x, out double y, out double z))
            {
                Ui.Line("Couldn't parse x/y/z as numbers. Format:  x y z letter");
                return "rejected: bad coordinates";
            }

            string raw = tokens[3].Trim();
            Band? band = ResolveBand(raw);
            if (band == null)
            {
                string letters = string.Join(" ", seeker.Bands.Select(b => b.Letter));
                Ui.Line($"'{raw}' isn't a band on the {seeker.Label}. Valid: {letters} (or 'nothing'). Type 'bands' for the table.", ConsoleColor.Yellow);
                return $"rejected: '{raw}' is not a valid band on {seeker.Label}";
            }

            var reading = new Reading
            {
                Seq = nextSeq++,
                X = x, Y = y, Z = z,
                MinDist = band.Min,
                MaxDist = band.Max,
                Letter = band.Letter,
                SeekerName = seeker.Name,
                Timestamp = Clock.Now
            };
            readings.Add(reading);

            Ui.Line($"Added #{reading.Seq} @ {reading.TimeText}: {reading.PosText}  {band.Letter} -> {band.RangeText}");
            Estimate();
            return $"reading #{reading.Seq} added";
        }

        // Accepts the band letter, or 'nothing'/'none'/'silent'/'x'/'-' for out of range -
        // so you never have to remember whether silence is E, F or G on this seeker.
        static Band? ResolveBand(string raw)
        {
            string s = raw.ToLowerInvariant();
            if (s == "nothing" || s == "none" || s == "silent" || s == "silence" || s == "x" || s == "-")
                return seeker.Silent;

            if (s.Length == 1 && char.IsLetter(s[0])) return seeker.Find(s[0]);
            return null;
        }

        static bool TryCoords(string a, string b, string c, out double x, out double y, out double z)
        {
            x = y = z = 0;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                && double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        static string ListReadings()
        {
            var active = Active();
            if (active.Count == 0)
            {
                Ui.Line("No active readings. 'history' shows retired ones.");
                return "list shown (0 active)";
            }

            var conf = Analysis.Confidence(active, metric);
            var tracks = Analysis.BuildTracks(active, metric);
            var trackOf = new Dictionary<int, int>();
            foreach (var t in tracks)
                foreach (var r in t.Readings) trackOf[r.Seq] = t.Index;

            Ui.Line();
            Ui.Line($"  {"id",-4} {"time",-9} {"position",-26} {"band",-4} {"range",-15} {"track",-6} confidence");
            foreach (var r in active.OrderBy(r => r.Seq))
            {
                double c = conf[r.Seq];
                Ui.Raw($"  #{r.Seq,-3} {r.TimeText,-9} {r.PosText,-26} {r.Letter,-4} {r.BandText,-15} {trackOf[r.Seq],-6} ");
                lock (Ui.Gate)
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = Ui.ConfidenceColor(c);
                    Console.WriteLine($"{Ui.Bar(c, 10)} {c * 100,3:0}%");
                    Console.ForegroundColor = prev;
                }
            }
            Ui.Line();
            return $"list shown ({active.Count} active)";
        }

        static string ListConflicts()
        {
            var active = Active();
            if (active.Count < 2)
            {
                Ui.Line("Need at least two active readings before anything can conflict.");
                return "conflicts shown (not enough active readings)";
            }

            var pairs = Analysis.Conflicts(active, metric);
            if (pairs.Count == 0)
            {
                Ui.Line("No conflicts - every active reading is compatible with every other.", ConsoleColor.Green);
                return "conflicts shown (none)";
            }

            Ui.Line();
            Ui.Line($"{pairs.Count} conflicting pair(s) - no single point can satisfy both halves:", ConsoleColor.Yellow);
            foreach (var (a, b) in pairs)
            {
                double d = a.DistanceTo(b.X, b.Y, b.Z, metric);
                Ui.Line($"  #{a.Seq} {a.Letter} ({a.BandText})  vs  #{b.Seq} {b.Letter} ({b.BandText})   - they sit {d:0.#} blocks apart");
            }
            Ui.Line();
            return $"conflicts shown ({pairs.Count})";
        }

        static string DeleteReading(string[] tokens)
        {
            if (tokens.Length != 2 || !int.TryParse(tokens[1], out int id))
            {
                Ui.Line("Usage: delete N   (N is the #id shown by 'list'; ids are stable and never reused)");
                return "rejected: bad delete usage";
            }

            var target = readings.FirstOrDefault(r => r.Seq == id);
            if (target == null)
            {
                Ui.Line($"No reading #{id}. Use 'list' to see what's there.");
                return $"rejected: no reading #{id}";
            }

            if (target.Retired)
            {
                Ui.Line($"#{id} is already retired (kill #{target.RetiredByKillId}). Use 'reset' if you want to wipe everything instead.", ConsoleColor.Yellow);
                return $"rejected: #{id} already retired";
            }

            readings.Remove(target);
            Ui.Line($"Removed #{id}: {target.PosText} {target.Letter}.");
            if (Active().Count > 0) Estimate();
            else Ui.Line("No active readings left.");
            return $"reading #{id} deleted";
        }

        // ---------- found ----------

        static string HandleFound(string sx, string sy, string sz)
        {
            if (!TryCoords(sx, sy, sz, out double fx, out double fy, out double fz))
            {
                Ui.Line("Couldn't parse those coordinates. Usage: found x y z   (or 'x y z found')");
                return "rejected: bad coordinates for 'found'";
            }

            var active = Active();
            var tracks = Analysis.BuildTracks(active, metric);
            var matched = active.Where(r => r.Satisfies(fx, fy, fz, metric)).ToList();

            Ui.Line();
            Ui.Line($"Skeleton confirmed at ({fx:0.#}, {fy:0.#}, {fz:0.#}).", ConsoleColor.Green);

            if (matched.Count == 0)
            {
                Ui.Line("  None of your active readings are consistent with that spot, so nothing was retired.", ConsoleColor.Yellow);
                Ui.Line("  Either a coordinate is mistyped, or every reading you have describes a different skeleton.");

                var miss = new Kill { Id = nextKillId++, X = fx, Y = fy, Z = fz, Timestamp = Clock.Now };
                kills.Add(miss);
                return $"kill #{miss.Id} confirmed at ({fx:0.#},{fy:0.#},{fz:0.#}), no reading matched, nothing retired";
            }

            var owner = tracks
                .OrderByDescending(t => t.Readings.Count(r => matched.Contains(r)))
                .First();

            owner.Result = Solver.Solve(owner.Readings, metric);
            double? err = null;
            if (owner.Result.Bounded && owner.Result.Count > 0)
            {
                err = Math.Sqrt(Math.Pow(fx - owner.Result.Cx, 2)
                              + Math.Pow(fy - owner.Result.Cy, 2)
                              + Math.Pow(fz - owner.Result.Cz, 2));
                Ui.Line($"  Track {owner.Index} estimated ({owner.Result.Cx:0.#}, {owner.Result.Cy:0.#}, {owner.Result.Cz:0.#}) - off by {err:0.#} blocks.");
            }

            var kill = new Kill
            {
                Id = nextKillId++,
                X = fx, Y = fy, Z = fz,
                Timestamp = Clock.Now,
                RetiredSeqs = matched.Select(r => r.Seq).ToList(),
                TrackIndex = owner.Index,
                EstimateError = err
            };
            kills.Add(kill);
            foreach (var r in matched) { r.Retired = true; r.RetiredByKillId = kill.Id; }

            Ui.Line($"  Kill #{kill.Id}: retired {matched.Count} reading(s) that pointed here: {string.Join(" ", matched.Select(r => "#" + r.Seq))}");

            var stillActive = Active();
            string outcome = $"kill #{kill.Id} confirmed at ({fx:0.#},{fy:0.#},{fz:0.#}), retired {string.Join(" ", kill.RetiredSeqs.Select(s => "#" + s))}";

            if (stillActive.Count == 0)
            {
                Ui.Line("  Board is clear. Enter readings whenever you're ready for the next one.");
                return outcome;
            }

            Ui.Line($"  {stillActive.Count} reading(s) left over - those were describing something else.", ConsoleColor.Cyan);
            Estimate();
            return outcome;
        }

        static string ClearEverythingAsFound()
        {
            var active = Active();
            var kill = new Kill { Id = nextKillId++, Timestamp = Clock.Now, RetiredSeqs = active.Select(r => r.Seq).ToList() };
            kills.Add(kill);
            foreach (var r in active) { r.Retired = true; r.RetiredByKillId = kill.Id; }

            Ui.Line($"Kill #{kill.Id}: marked found at an unspecified location and retired {active.Count} reading(s).");
            Ui.Line("Starting fresh for the next one - 'history' still has everything retired so far.");
            return $"kill #{kill.Id} confirmed at unspecified location, retired {active.Count} reading(s): {string.Join(" ", kill.RetiredSeqs.Select(s => "#" + s))}";
        }

        static string DoReset()
        {
            readings.Clear();
            kills.Clear();
            soloVolumeCache.Clear();
            nextSeq = 1;
            nextKillId = 1;
            Ui.Line("Everything cleared - readings and kill history both.");
            return "board reset (readings and history cleared)";
        }

        // ---------- history ----------

        static string ShowHistory()
        {
            var retired = readings.Where(r => r.Retired).OrderBy(r => r.Seq).ToList();

            Ui.Line();
            if (kills.Count == 0)
            {
                Ui.Line("No kills yet.");
            }
            else
            {
                foreach (var k in kills)
                {
                    Ui.Line($"  Kill #{k.Id}  found {k.PosText}  {k.Timestamp:yyyy-MM-dd HH:mm:ss}", ConsoleColor.Cyan);
                    if (k.RetiredSeqs.Count == 0)
                    {
                        Ui.Line("    retired: none (no reading matched)");
                    }
                    else
                    {
                        string errText = k.EstimateError.HasValue ? $", estimate was off by {k.EstimateError:0.#} blocks" : "";
                        string trackText = k.TrackIndex.HasValue ? $" (track {k.TrackIndex})" : "";
                        Ui.Line($"    retired: {string.Join(" ", k.RetiredSeqs.Select(s => "#" + s))}{trackText}{errText}");
                    }
                }
            }

            Ui.Line();
            Ui.Line($"  {retired.Count} retired reading(s) total, {Active().Count} still active.", ConsoleColor.DarkGray);
            Ui.Line();
            return $"history shown ({kills.Count} kill(s), {retired.Count} retired reading(s))";
        }

        // ---------- the estimate ----------

        static string Estimate()
        {
            var active = Active();
            if (active.Count == 0)
            {
                Ui.Line("No active readings - nothing to estimate. 'history' shows what's been retired.");
                return "estimate: nothing to solve";
            }

            var tracks = Analysis.BuildTracks(active, metric);
            foreach (var t in tracks) t.Result = Solver.Solve(t.Readings, metric);

            Ui.Line();
            Ui.Line($"[{active.Count} reading(s) - {seeker.Label} - {metric}]", ConsoleColor.DarkGray);

            if (tracks.Count > 1)
            {
                Ui.Line();
                Ui.Line($"!! Your readings split into {tracks.Count} groups that cannot all describe one skeleton.", ConsoleColor.Yellow);
                Ui.Line("   Most likely you have picked up two separate spawns. Each group is solved on its own");
                Ui.Line("   below; 'conflicts' shows exactly which pairs disagree.");
            }

            foreach (var t in tracks) PrintTrack(t, tracks.Count, active);
            Ui.Line();
            return $"estimate shown ({tracks.Count} track(s))";
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

        static void PrintTrack(Track t, int trackCount, List<Reading> active)
        {
            var r = t.Result!;
            var conf = Analysis.Confidence(active, metric);
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
                Ui.Line("  Add at least one reading where you actually heard the seeker.");
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

            // Even a consistent reading set can leave two separated pockets.
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

        // ---------- session log: export / import / simulate ----------

        static string WriteReport(IEnumerable<string> rest)
        {
            string name = string.Join(" ", rest).Trim().Trim('"');
            string path;

            if (name.Length == 0)
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                path = Path.Combine(dir, $"locator-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            }
            else
            {
                if (!Path.HasExtension(name)) name += ".txt";
                try
                {
                    path = Path.GetFullPath(name);
                }
                catch (Exception ex)
                {
                    Ui.Line($"That isn't a usable filename: {ex.Message}", ConsoleColor.Yellow);
                    return "rejected: bad output filename";
                }
            }

            string report = Export.BuildLog(sessions);

            try
            {
                File.WriteAllText(path, report, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Ui.Line($"Couldn't write that file: {ex.Message}", ConsoleColor.Yellow);
                return "rejected: couldn't write file";
            }

            int totalCommands = sessions.Sum(s => s.Entries.Count);
            Ui.Line($"Wrote {sessions.Count} session(s), {totalCommands} command(s), to:", ConsoleColor.Green);
            Ui.Line($"  {path}");
            return $"exported {sessions.Count} session(s) to {path}";
        }

        static string ImportFile(IEnumerable<string> rest)
        {
            if (replaying) return "rejected: cannot import while simulating";

            string name = string.Join(" ", rest).Trim().Trim('"');
            if (name.Length == 0)
            {
                Ui.Line("Usage: import <file>");
                return "rejected: no filename given to import";
            }

            string path;
            try
            {
                path = Path.GetFullPath(name);
            }
            catch (Exception ex)
            {
                Ui.Line($"That isn't a usable path: {ex.Message}", ConsoleColor.Yellow);
                return "rejected: bad import path";
            }

            if (!File.Exists(path))
            {
                Ui.Line($"No file at: {path}", ConsoleColor.Yellow);
                return "rejected: import file not found";
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Ui.Line($"Couldn't read that file: {ex.Message}", ConsoleColor.Yellow);
                return "rejected: couldn't read import file";
            }

            if (!Import.TryParse(text, out var imported, out string error))
            {
                Ui.Line($"Couldn't read that as a Locator session log: {error}", ConsoleColor.Yellow);
                return "rejected: import parse failed";
            }

            sessions.AddRange(imported);
            Ui.Line($"Imported {imported.Count} session(s), {imported.Sum(s => s.Entries.Count)} command(s) total.", ConsoleColor.Green);
            Ui.Line("Use 'sessions' to see them, 'simulate <n>' to replay one, or 'simulate all' to replay every imported session.");
            return $"imported {imported.Count} session(s) from {path}";
        }

        static string ShowSessions()
        {
            if (sessions.Count == 0)
            {
                Ui.Line("No sessions.");
                return "sessions shown (0)";
            }

            var ordered = sessions.OrderBy(s => s.StartedAt).ToList();
            Ui.Line();
            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                string tag = ReferenceEquals(s, currentSession) ? "live" : (s.Imported ? "imported" : "");
                Ui.Line($"  [{i + 1}] started {s.StartedAt:yyyy-MM-dd HH:mm:ss}  {s.Entries.Count,4} command(s)  {FormatSpan(s.Duration)}  {tag}");
            }
            Ui.Line();
            return $"sessions shown ({ordered.Count})";
        }

        static string FormatSpan(TimeSpan span) =>
            span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";

        static string Simulate(IEnumerable<string> rest)
        {
            if (replaying) return "rejected: already simulating";

            string arg = string.Join(" ", rest).Trim().ToLowerInvariant();
            var ordered = sessions.OrderBy(s => s.StartedAt).ToList();
            var toReplay = new List<Session>();

            if (arg == "all")
            {
                toReplay = ordered.Where(s => s.Imported).ToList();
                if (toReplay.Count == 0)
                {
                    Ui.Line("No imported sessions to simulate. Use 'import <file>' first.", ConsoleColor.Yellow);
                    return "rejected: nothing imported to simulate";
                }
            }
            else if (int.TryParse(arg, out int n) && n >= 1 && n <= ordered.Count)
            {
                toReplay.Add(ordered[n - 1]);
            }
            else
            {
                Ui.Line("Usage: simulate all   |   simulate <n>   (see 'sessions' for the numbers)");
                return "rejected: bad simulate usage";
            }

            ResetBoardForSimulation();

            Ui.Line($"Replaying {toReplay.Count} session(s)...", ConsoleColor.Cyan);
            replaying = true;
            try
            {
                foreach (var s in toReplay)
                {
                    foreach (var e in s.Entries)
                    {
                        Clock.Override = e.Timestamp;
                        try { ProcessLine(e.RawInput); }
                        finally { Clock.Override = null; }
                    }
                }
            }
            finally { replaying = false; }

            Ui.Line($"Replay done. {Active().Count} active reading(s), {kills.Count} kill(s).", ConsoleColor.Green);
            return $"simulated {toReplay.Count} session(s)";
        }

        static void ResetBoardForSimulation()
        {
            readings.Clear();
            kills.Clear();
            soloVolumeCache.Clear();
            nextSeq = 1;
            nextKillId = 1;
            seeker = Seekers.Repaired;
            metric = DistanceMetric.Euclidean;
        }

        // ---------- timer ----------

        static string StartTimer(string[] tokens)
        {
            double? explicitInterval = null;
            if (tokens.Length >= 2 &&
                double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            {
                if (secs < 1 || secs > 120)
                {
                    Ui.Line("Interval should be between 1 and 120 seconds.");
                    return "rejected: interval out of range";
                }
                explicitInterval = secs;
            }

            timer.Start(explicitInterval);
            Ui.Line("Loop running - it shows at the right edge of the line and in the window title.", ConsoleColor.DarkCyan);
            Ui.Line("  The countdown refills the moment it empties and the loop counter ticks up.");
            Ui.Line("  When you hear the seeker, hit Enter on an empty line (or type 'ping') to re-align.");
            Ui.Line("  It learns the real interval from the gaps between your pings; you don't have to");
            Ui.Line("  catch every sound. 'timer' for the count, 'stoptimer' to end.");
            return "timer started";
        }

        // ---------- help ----------

        static void PrintHelp()
        {
            Ui.Line();
            Ui.Line("Readings", ConsoleColor.Cyan);
            Ui.Line("  x y z letter         add a reading      e.g.  -12 70 305 C");
            Ui.Line("  x y z nothing        no sound at all (also: none, silent, x, -)");
            Ui.Line("  list                 active readings with their track and confidence");
            Ui.Line("  conflicts            which active readings disagree, and by how much");
            Ui.Line("  delete N             drop an active reading #N (retired ones can't be deleted, only 'reset')");
            Ui.Line("  estimate / tracks    re-solve without adding anything");
            Ui.Line("  reset                wipe everything - readings AND kill history");
            Ui.Line();
            Ui.Line("Finding one", ConsoleColor.Cyan);
            Ui.Line("  found x y z          confirm a kill; retires (not deletes) the readings that pointed there");
            Ui.Line("  x y z found          same thing, other argument order");
            Ui.Line("  found                retire every active reading under an 'unspecified location' kill");
            Ui.Line("  history              every retired reading and every kill, in order");
            Ui.Line();
            Ui.Line("Session log", ConsoleColor.Cyan);
            Ui.Line("  output [file]        save every command you've typed this run to a text file");
            Ui.Line("                       (default: a timestamped file on your Desktop)");
            Ui.Line("  import <file>        load a previously saved session log");
            Ui.Line("  sessions             list every session currently held (this run + anything imported)");
            Ui.Line("  simulate all         replay every imported session, reconstructing the board");
            Ui.Line("  simulate <n>         replay just one session (see 'sessions' for the number)");
            Ui.Line();
            Ui.Line("Ghost Seeker", ConsoleColor.Cyan);
            Ui.Line("  seeker g1 / gii / refined     switch by grade - I is best (g1 > g2 > g3)");
            Ui.Line("  seeker t1 / t3                switch by tier - opposite order (t3 is best, t1 weakest)");
            Ui.Line("  bands                         show the current band table");
            Ui.Line("  metric euclidean | chebyshev  round vs blocky rings");
            Ui.Line();
            Ui.Line("Browser UI", ConsoleColor.Cyan);
            Ui.Line("  web                  start a local web UI for this session and open it in your browser -");
            Ui.Line("                       same board, same solve, works alongside the terminal");
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
