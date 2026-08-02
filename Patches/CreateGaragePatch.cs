using HarmonyLib;
using Kitchen;
using KitchenData;
using KitchenPlateupAP;
using Unity.Collections;
using Unity.Entities;

namespace KitchenPlateupAP.Patches
{
    [HarmonyPatch(typeof(CreateGarage), "OnUpdate")]
    internal static class CreateGaragePatch
    {
        // True on identity change — clears ALL CCrateAppliance before refilling
        private static bool _garageDirty = true;

        // Prevents re-injection on subsequent CreateGarage.OnUpdate calls within the same lobby entry
        private static bool _injectedThisEntry = false;

        /// <summary>
        /// Call when the player leaves the lobby and starts a run.
        /// Allows the next lobby entry to re-inject crates.
        /// </summary>
        public static void ResetForNextLobby() => _injectedThisEntry = false;

        /// <summary>
        /// Call on identity change — forces a full clear + re-inject on next lobby entry.
        /// </summary>
        public static void MarkDirty()
        {
            _garageDirty = true;
            _injectedThisEntry = false;
        }

        [HarmonyPrefix]
        private static void Prefix(CreateGarage __instance)
        {
            if (!Mod.AllowSaveFileEditing || !Mod.ApplianceUnlocksEnabled)
                return;

            if (GameData.Main == null)
                return;

            EntityManager em = __instance.EntityManager;

            // On identity change: destroy ALL CCrateAppliance entities (vanilla + AP)
            // so the garage rebuilds cleanly from both vanilla upgrades and our file.
            if (_garageDirty)
            {
                EntityQuery allCratesQuery = em.CreateEntityQuery(new ComponentType[] { typeof(CCrateAppliance) });
                using (NativeArray<Entity> all = allCratesQuery.ToEntityArray(Allocator.Temp))
                {
                    Mod.Logger?.LogInfo($"[CreateGaragePatch] Identity changed — clearing {all.Length} crate(s).");
                    foreach (Entity e in all)
                        em.DestroyEntity(e);
                }
                _garageDirty = false;
                _injectedThisEntry = false;
            }

            // CreateGarage.OnUpdate fires more than once per lobby entry.
            // Only inject our crates on the first call; subsequent calls are no-ops.
            if (_injectedThisEntry)
                return;

            var identity = Mod.CurrentIdentity;
            if (identity == null)
            {
                _injectedThisEntry = true;
                return;
            }

            GarageState garage = PersistenceManager.LoadGarage(identity);
            if (garage.ApplianceGDOs.Count == 0)
            {
                Mod.Logger?.LogInfo("[CreateGaragePatch] No AP appliances saved; skipping.");
                _injectedThisEntry = true;
                return;
            }

            int injected = 0;
            var injectedGdoIds = new System.Collections.Generic.List<int>();

            foreach (int gdoId in garage.ApplianceGDOs)
            {
                if (!GameData.Main.TryGet<Appliance>(gdoId, out var appliance, warn_if_fail: false))
                    continue;

                int itemID = (appliance.CrateItem != null) ? appliance.CrateItem.ID : AssetReference.ApplianceCrate;

                Entity crate = em.CreateEntity();
                em.AddComponent<CCrate>(crate);
                em.AddComponentData(crate, new CPersistentItem
                {
                    Type = PersistentStorageType.Crate,
                    ItemID = itemID
                });
                em.AddComponentData(crate, new CCrateAppliance { Appliance = gdoId });
                em.AddComponent<CGranted>(crate);       // prevents GrantUpgrades from reprocessing
                em.AddComponent<CAPGarageCrate>(crate); // our tag for identity-change cleanup

                injectedGdoIds.Add(gdoId);
                injected++;
            }

            // Remove every successfully injected GDO from the persisted file.
            // Vanilla preserves crate entities across runs, so these never need
            // re-injecting — keeping them in the file would cause duplicates on
            // the next lobby entry.
            if (injectedGdoIds.Count > 0)
            {
                foreach (int gdoId in injectedGdoIds)
                    garage.ApplianceGDOs.Remove(gdoId);

                PersistenceManager.SaveGarage(identity, garage);
                Mod.Logger?.LogInfo($"[CreateGaragePatch] Removed {injectedGdoIds.Count} injected GDO(s) from garage file.");
            }

            _injectedThisEntry = true;
            Mod.Logger?.LogInfo($"[CreateGaragePatch] Injected {injected} AP crate(s) before garage shelf creation.");
        }
    }
}

namespace KitchenPlateupAP
{
    /// <summary>Tags AP-managed garage crate entities for targeted cleanup on identity change.</summary>
    public struct CAPGarageCrate : IComponentData { }
}