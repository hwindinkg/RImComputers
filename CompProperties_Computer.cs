using RimWorld;
using Verse;

namespace RimComputers
{
    public class CompProperties_Computer : CompProperties
    {
        // Storage
        public long romBytes  = 52428800; // 50 MB default
        public long ramBytes  = 5242880;  // 5 MB default

        // Screen
        public int screenWidth  = 160;
        public int screenHeight = 50;

        // Tier (1/2/3)
        public int tier = 1;

        public CompProperties_Computer()
        {
            compClass = typeof(Comp_Computer);
        }

        // Convenience helpers
        public float RomMB => romBytes / 1048576f;
        public float RamMB => ramBytes / 1048576f;
    }
}
