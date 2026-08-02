# Locator

Triangulates a **Praying Skeleton** from Ghost Seek readings, using whichever Ghost Seeker
you're holding.

The seeker only tells you roughly how far away something is — a different sound per
distance ring. Stand somewhere, note your coordinates and which ring you heard, and
this narrows down where the skeleton actually is.

## Usage

```
dotnet run --project Locator
```

It starts straight at the `>` prompt on Grade I (Refined) by default — the longest-reaching,
most precise seeker. Switch with `seeker <spec>` if you're actually holding something else. Then
enter readings as `x y z letter`:

```
> -119 -1319 -241 D
> -63 -1319 -285 C
```

## Ghost Seekers and bands

**A is always the closest ring.** Letters step outward from there, and the letter right
after the last audible ring means silence — though you can always just type `nothing`
(or `none`, `silent`, `x`, `-`) instead of remembering which letter that is.

| | 0–25 | 26–50 | 51–100 | 101–150 | 151–200 | 201–250 | no sound |
|---|---|---|---|---|---|---|---|
| **Grade III** — Makeshift Ghost Seek | A | B | C | D | — | — | **E** (151+) |
| **Grade II** — Repaired Ghost Seek | A | B | C | D | E | — | **F** (201+) |
| **Grade I** — Refined Ghost Seek | A | B | C | D | E | F | **G** (251+) |

Switch with `seeker` — and it matters *which* number scheme you use, because Grade and
Tier count in opposite directions:

| | Grade | Tier |
|---|---|---|
| Makeshift (weakest) | III (`g3`) | 1 (`t1`) |
| Repaired | II (`g2`) | 2 (`t2`) |
| Refined (best) | I (`g1`) | 3 (`t3`) |

So `seeker g1` and `seeker t3` do the same thing; `seeker g1` and `seeker t1` do the
*opposite*. Full names work too — `makeshift`, `repaired`, `refined` — as does `grade 2`,
`tier3`, `ghostseeker gii`, or a bare number if you already know which scheme you mean.

Switching seekers mid-hunt is safe: existing readings keep the distances they were
entered with, so an old `D` never silently changes meaning.

## Commands

| Command | What it does |
|---|---|
| `x y z letter` | Add a reading |
| `list` | Every **active** reading with its track and confidence |
| `conflicts` | Which active readings disagree, and by how much |
| `delete N` | Drop an active reading #N — refuses if it's already retired |
| `estimate` / `tracks` | Re-solve without adding anything |
| `found x y z` / `x y z found` | Confirm a kill — retires, never deletes (see below) |
| `history` | Every retired reading and every kill, in order |
| `reset` | Wipe everything — readings **and** kill history |
| `output [file]` | Save this run's command log to a text file (default: your Desktop) |
| `import <file>` | Load a previously saved session log |
| `sessions` | List every session currently held (this run + anything imported) |
| `simulate all` / `simulate <n>` | Replay imported sessions, reconstructing the board |
| `seeker <spec>` / `bands` | Change Ghost Seeker / show the band table |
| `metric euclidean｜chebyshev` | Round vs blocky rings |
| `starttimer [secs]` / `ping` / `timer` / `stoptimer` | Looping sound countdown + cycle count |
| `web` | Start a local browser UI for this session (see below) |

## Browser UI

Type `web` at any time and the app starts a small local web server and opens your default
browser to it. This isn't a second implementation — every click in the browser runs through
the exact same command dispatcher the terminal uses, on the same running process, so there's
one engine and one set of state. The terminal keeps working the whole time; add a reading in
one, refresh the other, they're both looking at the same board.

Nothing is exposed outside this machine — the server only listens on `127.0.0.1`, on a random
free port chosen each time. Closing the browser tab doesn't stop the terminal, and vice versa;
the process keeps running (and the server with it) until you `exit` the terminal.

The browser page also has an always-available command box wired to the same dispatcher, so
anything the terminal understands is reachable there too, not just what has a dedicated button.

## What it does beyond averaging

### Search-space collapse

Every solve reports how much of the original search volume survives, measured against
what that track's oldest reading allowed on its own:

```
Search space      ████░░░░░░░░░░░░░░░░ 17.74% of reading #1 alone
                  ~1,779,752 blocks³   (458,699 candidates @ step 1.57)
```

Volume, not raw candidate count — the grid step shrinks as the box does, so counts
across two solves aren't comparable but `count × step³` is.

### Multiple skeletons

Two spawns can sit far apart in altitude or on separate platforms, and readings taken
near each will flatly contradict. Rather than declaring the whole set broken, readings
are partitioned into **tracks** — groups that are each internally consistent — and every
track is solved separately.

Two shells around `p1` and `p2` can hold a common point exactly when
`max(0, a₁−b₂, a₂−b₁) ≤ dist(p1,p2) ≤ b₁+b₂`, which makes the pairwise test O(1).
Three readings can still be pairwise fine yet jointly impossible; the grid solve catches
that and says so.

A single consistent track can also leave two disconnected pockets of valid space — the
mirror ambiguity you get before you have enough readings. Those are reported too, with
the share of candidates in each.

### Confidence

Each reading is scored 0–100%. Every other reading votes on it, weighted by recency:
agreement pulls toward 100%, contradiction toward 0%.

A fresh reading that contradicts four old ones lands near the bottom on its own. Add a
second fresh reading that agrees with it and both climb while the four older ones slide,
because the two newest weights dominate the sum. That's deliberate — if you've walked to
a different skeleton, the newest readings are the ones describing where you actually are.

### Found, and retiring instead of deleting

`found x y z` used to delete the matched readings outright. It doesn't anymore — a
mistaken match (a near-miss typo that still happens to land in the valid region) used
to be unrecoverable, and every confirmed kill's calibration data (the real position,
plus the readings that led to it) was thrown away the instant it was produced.

Instead, `found` **retires** the readings consistent with that spot — they drop out of
`list`/`estimate`/`conflicts` and stop influencing the live solve, but they aren't gone.
A `history` command shows every retired reading and every kill: position (or
`(unspecified)` for a bare `found`), timestamp, which readings it retired, and how far
off the estimate was. Ids are never reused, and `delete N` refuses to touch an already-
retired reading — `reset` is the only way to wipe history, and it wipes everything.

Anything left un-matched after a `found` was describing a different skeleton, so it
stays active and re-solves on its own.

### Session log

`output` no longer writes a results report — it writes a **replayable command log**:
every command you typed this run, verbatim, with a timestamp and a short outcome.
Nothing computed goes in it — no estimate, no region, no candidate counts, no pockets —
because all of that is 100% reproducible by replaying the raw commands back through the
app's own parser. Storing it would just be stale duplication the moment another reading
comes in.

```
=== Ghost Seek Locator session log ===
Format-Version 1
Generated       2026-08-02 14:03:11
Sessions        1
Commands        3

--- Session 1 ---
Started   2026-08-01 20:15:03.112
Duration  00:42:17
Commands  3
2026-08-01 20:15:03.112	seeker gii	seeker set to Grade II - Repaired Ghost Seek
2026-08-01 20:15:41.900	120 64 -30 C	reading #1 added
2026-08-01 20:57:20.331	found 0 -1319 -300	kill #1 confirmed at (0,-1319,-300), retired #1
--- end session 1 ---
```

Every command is recorded — successful, rejected, read-only or not — so the file doubles
as an honest usage log if you hand the app to someone else: what they typed, what got
rejected, whether they used `found` correctly. **The app says so on startup**, before
anything is entered — nothing is collected covertly, and nothing ever leaves the machine
unless `output` is run explicitly.

`output` with no filename saves a timestamped file to your **Desktop**; `output myfile`
saves to a specific path exactly as before. The file is plain ASCII, invariant-culture,
UTF-8 without a BOM — tab-separated entry lines so it round-trips reliably, but still
readable if you just open it.

### Import + simulate

Take a saved log back into the app and replay it:

```
> import myfile.txt
Imported 1 session(s), 3 command(s) total.
> sessions
  [1] started 2026-08-01 20:15:03   3 command(s)  0m 42s  imported
> simulate 1
```

`simulate <n>` replays one session; `simulate all` replays every imported session in
order, merged into one board. Either way it resets the board first, then feeds each
logged line back through the exact same command parser live typing uses — so the
reconstructed state (readings, tracks, kills, everything) comes out identical to what
was live at export time. Replayed lines keep their **original** timestamps rather than
being stamped "now," so historical chronology survives the round trip, and they aren't
re-logged into your current session — they already live permanently in the session they
came from.

### Sound loop

The seeker re-announces on a fixed cadence. `starttimer` runs that cadence as a loop —
the countdown refills the instant it empties and the loop counter ticks up, so you can
see both how long until the next sound and how many have gone by:

```
[███████░░░  12.4s  loop 7]
```

Drawn at the right edge of whatever line the cursor is on, then the cursor is put back,
so it never disturbs typing. The window title mirrors it, which keeps working when the
console is too narrow or output is redirected.

Hit Enter on an empty line (or type `ping`) the moment you hear a sound to re-align the
loop. You don't have to catch every one — a gap spanning several cycles is folded down
before it's used, so pinging every second or third sound still calibrates correctly. The
interval is the median of recent measurements rather than the 20s default, and a
measurement is only believed if it lands within 0.4×–2.5× of the interval already held.

Rolling over keeps the phase rather than resetting it, so the loop stays aligned to the
sound even after a long pause. If three or more loops pass without a confirming ping the
counter turns amber — the phase may have drifted, or you may have walked out of range.
`timer` reports the count, `stoptimer` the total.

## How the solve works

1. Each reading's max distance implies an axis-aligned box around that position; all the
   boxes in a track are intersected to bound the search area.
2. That box is sampled on a grid sized to land near ~2M sample points.
3. A point survives only if its rounded distance to *every* reading in the track falls
   inside that reading's band. Rounding matters — the seeker reports a rounded distance,
   so a true 100.04 still counts as 100.
4. Survivors are accumulated into coarse cells (bounded memory, no million-point lists),
   then flood-filled to find disconnected pockets.

Contradictory readings are reported but never discarded automatically — use `list` and
`delete N` to remove whichever one was wrong.

Euclidean is the default; it was confirmed against real in-game readings, as were the
band edges (a `B` landing at ~26–28, a `C` at ~100.04, a `D` at ~101–105).

## Note

`Locator/Data.txt` is a captured console session from before the seeker/letter rework —
it uses the old `near`/`far` keywords. Kept as a log; nothing reads it.
