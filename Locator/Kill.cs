using System;
using System.Collections.Generic;

namespace SpawnLocator
{
    // A confirmed 'found'. Kept forever instead of deleting the readings that led to it,
    // so a mistaken match is recoverable and every kill's calibration data survives.
    class Kill
    {
        public int Id;
        public double? X, Y, Z;      // null => bare 'found', no coordinates given
        public DateTime Timestamp;
        public List<int> RetiredSeqs = new List<int>();
        public int? TrackIndex;
        public double? EstimateError;

        public bool IsBareFound => X == null;
        public string PosText => IsBareFound ? "(unspecified)" : $"({X:0.#}, {Y:0.#}, {Z:0.#})";
    }
}
