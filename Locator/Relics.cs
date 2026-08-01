using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnLocator
{
    // One audible sound tier of a Ghost Seek relic, or the silence past its reach.
    class Band
    {
        public char Letter;
        public double Min;
        public double Max;

        public bool IsSilent => double.IsPositiveInfinity(Max);
        public string RangeText => IsSilent ? $"{Min:0}+ blocks (no sound)" : $"{Min:0}-{Max:0} blocks";
    }

    class RelicTier
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

    static class Relics
    {
        // Thresholds straight off the wiki entry for each relic's passive ability.
        public static readonly RelicTier Makeshift =
            Build("Makeshift Ghost Seek", "III", 3, new double[] { 150, 100, 50, 25 });

        public static readonly RelicTier Repaired =
            Build("Repaired Ghost Seek", "II", 2, new double[] { 200, 150, 100, 50, 25 });

        public static readonly RelicTier Refined =
            Build("Refined Ghost Seek", "I", 1, new double[] { 250, 200, 150, 100, 50, 25 });

        public static readonly RelicTier[] All = { Makeshift, Repaired, Refined };

        // A is the innermost ring (0 .. smallest threshold); each following letter steps
        // one ring outward, and the letter right after the last audible ring means silence.
        // Grade II therefore comes out A=0-25 .. E=151-200, F=nothing, which is the
        // lettering the bands were originally calibrated against.
        static RelicTier Build(string name, string grade, int gradeNumber, double[] thresholds)
        {
            var tier = new RelicTier
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

        // Accepts g1 / gi / giii / grade 2 / t3 / tier III / 2 / "refined" / "refined ghost seek".
        // The number is always the grade number, so t1 and g1 both mean Grade I (Refined).
        public static RelicTier? Parse(string input)
        {
            string s = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (s.Length == 0) return null;

            if (s.Contains("makeshift")) return Makeshift;
            if (s.Contains("repaired")) return Repaired;
            if (s.Contains("refined")) return Refined;

            s = s.Replace("ghostseek", "").Replace("ghost", "").Replace("seek", "").Replace("relic", "");

            // Longest prefixes first so "grade1" doesn't get eaten by the bare "g" rule.
            foreach (var prefix in new[] { "grade", "tier", "g", "t" })
            {
                if (s.StartsWith(prefix, StringComparison.Ordinal))
                {
                    s = s.Substring(prefix.Length);
                    break;
                }
            }

            switch (s)
            {
                case "1":
                case "i": return Refined;
                case "2":
                case "ii": return Repaired;
                case "3":
                case "iii": return Makeshift;
                default: return null;
            }
        }

        public static void PrintTable(RelicTier tier)
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

        public static void PrintChoices()
        {
            foreach (var t in All.OrderByDescending(t => t.GradeNumber))
                Ui.Line($"    Grade {t.Grade,-4} {t.Name,-24} reaches {t.Reach:0} blocks, {t.Thresholds.Length} sounds");
        }
    }
}
