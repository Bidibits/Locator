using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SpawnLocator
{
    // Everything the browser front end needs. Deliberately thin: every request funnels through
    // the exact same Dispatch() the terminal uses, so there is exactly one implementation of the
    // app's behavior - the browser never re-derives an estimate, a confidence score, or a band
    // table on its own, it only ever displays what this process computed.
    partial class Program
    {
        static WebApplication? webApp;

        // Guards every state-mutating access, from either the terminal loop or an HTTP request -
        // without this, a browser click and a keystroke could race on the same reading list.
        static readonly object commandLock = new object();

        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        static string StartWeb()
        {
            if (webApp != null) return $"rejected: web server already running at {webApp.Urls.FirstOrDefault()}";

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();               // this is a personal tool, not a hosted service - keep the console clean
            builder.WebHost.UseUrls("http://127.0.0.1:0");   // port 0 = OS picks a free one; we read back whatever it bound

            var app = builder.Build();

            app.MapGet("/", () => Results.Content(GetIndexHtml(), "text/html; charset=utf-8"));

            app.MapGet("/api/state", () =>
            {
                lock (commandLock) return Results.Json(BuildStateSnapshot(), JsonOpts);
            });

            app.MapPost("/api/command", async (HttpRequest req) =>
            {
                var body = await JsonSerializer.DeserializeAsync<CommandRequest>(req.Body, JsonOpts) ?? new CommandRequest();
                string outcome; StateSnapshot state;
                lock (commandLock)
                {
                    // Mirrors Main()'s own blank-line handling (the console's "hit Enter to ping" shortcut) -
                    // that branch lives outside Dispatch there, so it's replicated here rather than routed
                    // through it, to avoid logging an empty line as if it were a real command. Dispatch()
                    // takes commandLock itself too; the reacquire on the same thread is harmless (Monitor
                    // is reentrant) and keeps the outcome and the snapshot taken from one atomic moment,
                    // rather than letting another request or a keystroke slip in between the two.
                    var line = (body.Line ?? "").Trim();
                    outcome = line.Length == 0
                        ? (timer.Running ? timer.Ping() : "no-op, timer not running")
                        : Dispatch(line).outcome;
                    state = BuildStateSnapshot();
                }
                return Results.Json(new CommandResponse { Outcome = outcome, State = state }, JsonOpts);
            });

            app.MapGet("/api/export", () =>
            {
                lock (commandLock)
                {
                    var text = Export.BuildLog(sessions);
                    var filename = $"locator-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    return Results.File(bytes, "text/plain; charset=utf-8", filename);
                }
            });

            app.MapPost("/api/import", async (HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                var text = await reader.ReadToEndAsync();
                lock (commandLock)
                {
                    if (!Import.TryParse(text, out var imported, out string error))
                        return Results.Json(new ImportResponse { Ok = false, Error = error }, JsonOpts);

                    sessions.AddRange(imported);
                    return Results.Json(new ImportResponse
                    {
                        Ok = true,
                        SessionCount = imported.Count,
                        CommandCount = imported.Sum(s => s.Entries.Count),
                        State = BuildStateSnapshot(),
                    }, JsonOpts);
                }
            });

            webApp = app;
            app.StartAsync().GetAwaiter().GetResult();   // brief, one-time block just to learn the bound port

            var url = app.Urls.First();
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* couldn't launch a browser automatically - the URL below still works if opened by hand */ }

            return $"web server started at {url}";
        }

        static string cachedIndexHtml = "";
        static string GetIndexHtml()
        {
            if (cachedIndexHtml.Length > 0) return cachedIndexHtml;
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("Web.index.html", StringComparison.Ordinal));
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            cachedIndexHtml = reader.ReadToEnd();
            return cachedIndexHtml;
        }

        // ---------- JSON snapshot ----------
        // Same data every console render function already computes - just captured into DTOs
        // instead of printed. No solving/scoring logic lives here; it all still runs through
        // Analysis.cs / Solver.cs exactly as the terminal path does.

        static StateSnapshot BuildStateSnapshot()
        {
            var snap = new StateSnapshot
            {
                Seeker = new SeekerDto
                {
                    Label = seeker.Label,
                    Grade = seeker.Grade,
                    Token = seeker.Grade == "III" ? "giii" : seeker.Grade == "II" ? "gii" : "gi",
                    Reach = seeker.Reach,
                    Bands = seeker.Bands.Select(b => new BandDto
                    {
                        Letter = b.Letter, Min = b.Min, Max = b.IsSilent ? (double?)null : b.Max, RangeText = b.RangeText,
                    }).ToList(),
                },
                Metric = metric.ToString(),
                TimerStatus = timer.Status(),
                TimerRunning = timer.Running,
                CoordsMode = coordsMode.ToString(),
                AbyssYOffset = abyssYOffset,
                AbyssYOffsetFromSeq = abyssYOffsetSetBySeq,
            };
            (snap.EffectiveYMin, snap.EffectiveYMax) = EffectiveAbyssYBand();

            var active = Active();
            var conf = Analysis.Confidence(active, metric);
            var tracks = Analysis.BuildTracks(active, metric);
            var trackOf = new Dictionary<int, int>();
            foreach (var t in tracks) foreach (var r in t.Readings) trackOf[r.Seq] = t.Index;

            foreach (var r in active.OrderBy(r => r.Seq))
            {
                snap.Active.Add(new ReadingDto
                {
                    Seq = r.Seq, Time = r.TimeText, X = r.X, Y = r.Y, Z = r.Z,
                    Letter = r.Letter.ToString(), BandText = r.BandText,
                    Track = trackOf[r.Seq], Confidence = conf[r.Seq],
                });
            }

            foreach (var t in tracks)
            {
                var result = Solver.Solve(t.Readings, metric, snap.EffectiveYMin, snap.EffectiveYMax);
                var avgConf = t.Readings.Average(x => conf[x.Seq]);
                var dto = new TrackDto { Index = t.Index, Ids = t.Readings.Select(r => r.Seq).ToList(), Confidence = avgConf };

                if (result.BoxEmpty) dto.Status = "boxEmpty";
                else if (!result.Bounded) dto.Status = "unbounded";
                else if (result.Count == 0) dto.Status = "noCandidates";
                else
                {
                    dto.Status = "ok";
                    dto.Cx = result.Cx; dto.Cy = result.Cy; dto.Cz = result.Cz;
                    dto.MinX = result.MinX; dto.MaxX = result.MaxX;
                    dto.MinY = result.MinY; dto.MaxY = result.MaxY;
                    dto.MinZ = result.MinZ; dto.MaxZ = result.MaxZ;
                    dto.Volume = result.Volume; dto.Count = result.Count; dto.Step = result.Step;

                    double baseline = BaselineFor(t);
                    if (baseline > 0) dto.BaselinePct = result.Volume / baseline * 100;

                    foreach (var b in result.Blobs.Take(4))
                    {
                        dto.Blobs.Add(new BlobDto
                        {
                            Cx = b.Cx, Cy = b.Cy, Cz = b.Cz,
                            MinX = b.MinX, MaxX = b.MaxX, MinY = b.MinY, MaxY = b.MaxY, MinZ = b.MinZ, MaxZ = b.MaxZ,
                            Count = b.Count, SharePct = (double)b.Count / result.Count * 100,
                        });
                    }
                    if (result.Blobs.Count > 1) dto.PocketGapBlocks = Solver.Separation(result.Blobs[0], result.Blobs[1]);
                }
                snap.Tracks.Add(dto);
            }

            foreach (var (a, b) in Analysis.Conflicts(active, metric))
            {
                snap.Conflicts.Add(new ConflictDto
                {
                    A = $"#{a.Seq} {a.Letter} ({a.BandText})", B = $"#{b.Seq} {b.Letter} ({b.BandText})",
                    Distance = a.DistanceTo(b.X, b.Y, b.Z, metric),
                });
            }

            foreach (var k in kills)
            {
                snap.Kills.Add(new KillDto
                {
                    Id = k.Id, Pos = k.PosText, Time = k.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    RetiredSeqs = k.RetiredSeqs, TrackIndex = k.TrackIndex, EstimateError = k.EstimateError,
                });
            }
            snap.RetiredCount = readings.Count(r => r.Retired);

            var orderedSessions = sessions.OrderBy(s => s.StartedAt).ToList();
            for (int i = 0; i < orderedSessions.Count; i++)
            {
                var s = orderedSessions[i];
                snap.Sessions.Add(new SessionDto
                {
                    Index = i + 1, StartedAt = s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"), Commands = s.Entries.Count,
                    Tag = ReferenceEquals(s, currentSession) ? "live" : (s.Imported ? "imported" : ""),
                });
            }

            return snap;
        }
    }

    // ---------- DTOs ----------

    class CommandRequest { public string? Line { get; set; } }
    class CommandResponse { public string Outcome { get; set; } = ""; public StateSnapshot State { get; set; } = new(); }
    class ImportResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public int SessionCount { get; set; }
        public int CommandCount { get; set; }
        public StateSnapshot? State { get; set; }
    }

    class BandDto { public char Letter { get; set; } public double Min { get; set; } public double? Max { get; set; } public string RangeText { get; set; } = ""; }
    class SeekerDto { public string Label { get; set; } = ""; public string Grade { get; set; } = ""; public string Token { get; set; } = ""; public double Reach { get; set; } public List<BandDto> Bands { get; set; } = new(); }
    class ReadingDto { public int Seq { get; set; } public string Time { get; set; } = ""; public double X { get; set; } public double Y { get; set; } public double Z { get; set; } public string Letter { get; set; } = ""; public string BandText { get; set; } = ""; public int Track { get; set; } public double Confidence { get; set; } }
    class BlobDto { public double Cx { get; set; } public double Cy { get; set; } public double Cz { get; set; } public double MinX { get; set; } public double MaxX { get; set; } public double MinY { get; set; } public double MaxY { get; set; } public double MinZ { get; set; } public double MaxZ { get; set; } public long Count { get; set; } public double SharePct { get; set; } }
    class TrackDto
    {
        public int Index { get; set; }
        public List<int> Ids { get; set; } = new();
        public double Confidence { get; set; }
        public string Status { get; set; } = "";
        public double? Cx { get; set; } public double? Cy { get; set; } public double? Cz { get; set; }
        public double? MinX { get; set; } public double? MaxX { get; set; }
        public double? MinY { get; set; } public double? MaxY { get; set; }
        public double? MinZ { get; set; } public double? MaxZ { get; set; }
        public double? Volume { get; set; } public long? Count { get; set; } public double? Step { get; set; }
        public double? BaselinePct { get; set; }
        public double? PocketGapBlocks { get; set; }
        public List<BlobDto> Blobs { get; set; } = new();
    }
    class ConflictDto { public string A { get; set; } = ""; public string B { get; set; } = ""; public double Distance { get; set; } }
    class KillDto { public int Id { get; set; } public string Pos { get; set; } = ""; public string Time { get; set; } = ""; public List<int> RetiredSeqs { get; set; } = new(); public int? TrackIndex { get; set; } public double? EstimateError { get; set; } }
    class SessionDto { public int Index { get; set; } public string StartedAt { get; set; } = ""; public int Commands { get; set; } public string Tag { get; set; } = ""; }

    class StateSnapshot
    {
        public SeekerDto Seeker { get; set; } = new();
        public string Metric { get; set; } = "";
        public List<ReadingDto> Active { get; set; } = new();
        public List<TrackDto> Tracks { get; set; } = new();
        public List<ConflictDto> Conflicts { get; set; } = new();
        public List<KillDto> Kills { get; set; } = new();
        public int RetiredCount { get; set; }
        public List<SessionDto> Sessions { get; set; } = new();
        public string TimerStatus { get; set; } = "";
        public bool TimerRunning { get; set; }
        public string CoordsMode { get; set; } = "";
        public double? AbyssYOffset { get; set; }
        public int? AbyssYOffsetFromSeq { get; set; }
        public double? EffectiveYMin { get; set; }
        public double? EffectiveYMax { get; set; }
    }
}
