# Locator

Triangulates a **Praying Skeleton** from Ghost Seek relic readings.

The relic only tells you roughly how far away something is — a different sound per
distance ring. Stand somewhere, note your coordinates and which ring you heard, and
this narrows down where the skeleton actually is.

## Usage

```
dotnet run --project Locator
```

It asks which relic you're holding first. Then enter readings as `x y z letter`:

```
> -119 -1319 -241 D
> -63 -1319 -285 C
```

## Relics and bands

**A is always the closest ring.** Letters step outward from there, and the letter right
after the last audible ring means silence — though you can always just type `nothing`
(or `none`, `silent`, `x`, `-`) instead of remembering which letter that is.

| | 0–25 | 26–50 | 51–100 | 101–150 | 151–200 | 201–250 | no sound |
|---|---|---|---|---|---|---|---|
| **Grade III** — Makeshift Ghost Seek | A | B | C | D | — | — | **E** (151+) |
| **Grade II** — Repaired Ghost Seek | A | B | C | D | E | — | **F** (201+) |
| **Grade I** — Refined Ghost Seek | A | B | C | D | E | F | **G** (251+) |

Pick a relic at startup or with `relic` — it accepts `g1`, `gii`, `giii`, `grade 2`,
`t3`, `tier1`, a bare `2`, or a name like `refined`. The number is always the grade
number, so `t1` and `g1` both mean Grade I.

Switching relics mid-hunt is safe: existing readings keep the distances they were
entered with, so an old `D` never silently changes meaning.

## Commands

| Command | What it does |
|---|---|
| `x y z letter` | Add a reading |
| `list` | Every reading with its track and confidence |
| `conflicts` | Which pairs of readings disagree, and by how much |
| `delete N` | Drop reading #N — ids are stable and never shift |
| `estimate` / `tracks` | Re-solve without adding anything |
| `found x y z` / `x y z found` | Confirm a kill (see below) |
| `reset` | Wipe everything |
| `relic <spec>` / `bands` | Change relic / show the band table |
| `metric euclidean｜chebyshev` | Round vs blocky rings |
| `starttimer [secs]` / `ping` / `timer` / `stoptimer` | Sound countdown |

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

### Found

`found x y z` retires **only** the readings consistent with that spot, and reports how
far off the estimate was. Anything left over was describing a different skeleton, so it
stays on the board and re-solves on its own. Bare `found` wipes everything.

### Sound timer

The relic re-announces on a fixed cadence. `starttimer` runs a countdown at the right
edge of the line and in the window title; hit Enter on an empty line (or type `ping`)
the moment you hear it to re-sync. It learns the real interval from the median gap
between your pings rather than trusting the 20s default.

## How the solve works

1. Each reading's max distance implies an axis-aligned box around that position; all the
   boxes in a track are intersected to bound the search area.
2. That box is sampled on a grid sized to land near ~2M sample points.
3. A point survives only if its rounded distance to *every* reading in the track falls
   inside that reading's band. Rounding matters — the relic reports a rounded distance,
   so a true 100.04 still counts as 100.
4. Survivors are accumulated into coarse cells (bounded memory, no million-point lists),
   then flood-filled to find disconnected pockets.

Contradictory readings are reported but never discarded automatically — use `list` and
`delete N` to remove whichever one was wrong.

Euclidean is the default; it was confirmed against real in-game readings, as were the
band edges (a `B` landing at ~26–28, a `C` at ~100.04, a `D` at ~101–105).

## Note

`Locator/Data.txt` is a captured console session from before the relic/letter rework —
it uses the old `near`/`far` keywords. Kept as a log; nothing reads it.
