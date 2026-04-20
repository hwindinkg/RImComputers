using Verse;

namespace RimComputers
{
    /// <summary>
    /// The physical computer building.
    /// Most logic lives in Comp_Computer; this class is kept thin.
    /// </summary>
    public class Building_Computer : Building
    {
        protected override void Tick()
        {
            base.Tick();
        }
        public Comp_Computer ComputerComp => GetComp<Comp_Computer>();

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // Force shutdown before the building is destroyed
            ComputerComp?.TryPowerOff();
            base.Destroy(mode);
        }
    }
}
