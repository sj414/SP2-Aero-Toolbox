using BepInEx;
using UnityEngine;

namespace VesselSwitcher
{
    // Aero Toolbox — the in-editor aerodynamics readout panel. Distinct BepInEx plugin (own DLL).
    [BepInPlugin("samja.sp2.aerotoolbox", "Aero Toolbox", "1.0.0")]
    public class AeroToolboxPlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            try { gameObject.AddComponent<AeroReadout>(); Logger.LogInfo("Aero Toolbox loaded."); }
            catch (System.Exception e) { Logger.LogError($"Aero Toolbox failed to start: {e}"); }
        }
    }
}
