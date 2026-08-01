# Locator

A console tool that triangulates a hidden point in 3D from a series of coarse
"how far away are you" readings.

You feed it positions you've stood at plus the distance band the game reported
there, and it narrows down where the thing actually is.

## Usage

```
dotnet run --project Locator
```

Then enter readings as `x y z range`:

```
> 120 64 -30 near
> -12 70 305 far
```

### Distance bands

| Range     | Distance   |
|-----------|------------|
| `closest` | 0–25       |
| `close`   | 26–50      |
| `near`    | 51–100     |
| `far`     | 101–150    |
| `farfar`  | 151–200    |
| `nothing` | 201+ (out of detection range) |

### Commands

| Command | What it does |
|---------|--------------|
| `list` | Show every reading entered so far |
| `estimate` | Force a re-estimate |
| `delete N` | Drop reading N (numbers shift afterwards) |
| `found [x y z]` | Mark it found; with coords, reports how far off the estimate was |
| `reset` | Clear all readings |
| `metric euclidean` / `metric chebyshev` | Switch between round (straight-line) and blocky (cube-shell) rings |
| `help` | Usage summary |
| `exit` | Quit |

## How it works

1. Each reading's max distance implies an axis-aligned box around that position;
   all the boxes are intersected to bound the search area.
2. That box is sampled on a grid sized to land near ~2M sample points.
3. A point survives only if its rounded distance to *every* reading falls inside
   that reading's band. Rounding matters — the game reports a rounded distance,
   so a true 100.04 still counts as 100.
4. The surviving points are averaged for the estimate, and their extent is
   printed as the possible region.

Contradictory readings are reported but never discarded automatically — use
`list` and `delete N` to remove whichever one was wrong.

Euclidean is the default; it was confirmed against real in-game readings.
