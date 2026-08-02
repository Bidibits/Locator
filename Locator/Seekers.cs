using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnLocator
{
    // One audible sound tier of a Ghost Seeker, or the silence past its reach.
    class Band
    {
        public char Letter;
        public double Min;
        public double Max;

        public bool IsSilent => double.IsPositiveInfinity(Max);
        public string RangeText => IsSilent ? $"{Min:0}+ blocks (no sound)" : $"{Min:0}-{Max:0} blocks";
    }

    class SeekerTier
    {
        public string Name = "";
        public string Grade = "";
        public int GradeNumber;

        // As the wiki lists them: largest first.
        public double[] Thresholds = Array.Empty<double>();
        public List<Band> Bands = new List<Band>();

        public Band Silent => Bands[Bands.Count - 1];
        public double Reach => Thresholds[0];
        public string Label => $"Grade {Grade} - {Name}";

        public Band? Find(char letter)
        {
            char up = char.ToUpperInvariant(letter);
            return Bands.FirstOrDefault(b => b.Letter == up);
        }
    }

    static class Seekers
    {
        // Thresholds straight off the wiki entry for each item's passive ability. The item's
        // own in-game name is "Ghost Seek" (not "Seeker") - that part is unchanged; only the
        // generic term the app itself uses for "which one is equipped" moves from relic->seeker.
        public static readonly SeekerTier Makeshift =
            Build("Makeshift Ghost Seek", "III", 3, new double[] { 150, 100, 50, 25 });

        public static readonly SeekerTier Repaired =
            Build("Repaired Ghost Seek", "II", 2, new double[] { 200, 150, 100, 50, 25 });

        public static readonly SeekerTier Refined =
            Build("Refined Ghost Seek", "I", 1, new double[] { 250, 200, 150, 100, 50, 25 });

        public static readonly SeekerTier[] All = { Makeshift, Repaired, Refined };

        // A is the innermost ring (0 .. smallest threshold); each following letter steps
        // one ring outward, and the letter right after the last audible ring means silence.
        // Grade II therefore comes out A=0-25 .. E=151-200, F=nothing, which is the
        // lettering the bands were originally calibrated against.
        static SeekerTier Build(string name, string grade, int gradeNumber, double[] thresholds)
        {
            var tier = new SeekerTier
            {
                Name = name,
                Grade = grade,
                GradeNumber = gradeNumber,
                Thresholds = thresholds
            };

            int n = thresholds.Length;
            tier.Bands.Add(new Band { Letter = 'A', Min = 0, Max = thresholds[n - 1] });
            for (int k = 1; k < n; k++)
            {
                tier.Bands.Add(new Band
                {
                    Letter = (char)('A' + k),
                    Min = thresholds[n - k] + 1,
                    Max = thresholds[n - k - 1]
                });
            }
            tier.Bands.Add(new Band
            {
                Letter = (char)('A' + n),
                Min = thresholds[0] + 1,
                Max = double.PositiveInfinity
            });

            return tier;
        }

        // Grade and Tier are NOT the same number scheme, despite both being "1/2/3" - Grade
        // counts down from the best (I, Refined) to the worst (III, Makeshift); Tier counts up
        // from the first one you'd own (1, Makeshift) to the last (3, Refined). So g1 and t1
        // mean opposite ends of the same list:
        //   g1 / grade1  -> Refined  (Grade I  - best)      t1 / tier1  -> Makeshift (weakest)
        //   g2 / grade2  -> Repaired (Grade II)              t2 / tier2  -> Repaired
        //   g3 / grade3  -> Makeshift (Grade III - weakest)  t3 / tier3  -> Refined   (best)
        // Also accepts a bare name ("refined"), "ghost seeker"/"seeker"/"seek" noise words, and
        // "relic" as a silent legacy alias so an older exported session log still replays.
        public static SeekerTier? Parse(string input)
        {
            string s = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (s.Length == 0) return null;

            if (s.Contains("makeshift")) return Makeshift;
            if (s.Contains("repaired")) return Repaired;
            if (s.Contains("refined")) return Refined;

            s = s.Replace("ghostseeker", "").Replace("ghostseek", "").Replace("ghost", "")
                 .Replace("seeker", "").Replace("seek", "").Replace("relic", "");

            if (s.StartsWith("grade", StringComparison.Ordinal)) return ParseGrade(s.Substring("grade".Length));
            if (s.StartsWith("tier", StringComparison.Ordinal)) return ParseTier(s.Substring("tier".Length));
            if (s.StartsWith("g", StringComparison.Ordinal)) return ParseGrade(s.Substring(1));
            if (s.StartsWith("t", StringComparison.Ordinal)) return ParseTier(s.Substring(1));

            return null;
        }

        static SeekerTier? ParseGrade(string s) => s switch
        {
            "1" or "i" => Refined,
            "2" or "ii" => Repaired,
            "3" or "iii" => Makeshift,
            _ => null,
        };

        static SeekerTier? ParseTier(string s) => s switch
        {
            "1" => Makeshift,
            "2" => Repaired,
            "3" => Refined,
            _ => null,
        };

        public static void PrintTable(SeekerTier tier)
        {
            Ui.Line();
            Ui.Line($"  {tier.Label}  -  reaches {tier.Reach:0} blocks", ConsoleColor.Cyan);
            foreach (var b in tier.Bands)
            {
                string note = b.IsSilent
                    ? "  (also accepted: nothing / none / x)"
                    : b.Letter == 'A' ? "  <- closest" : "";
                Ui.Line($"    {b.Letter}   {b.RangeText,-24}{note}");
            }
            Ui.Line();
        }
    }
}
