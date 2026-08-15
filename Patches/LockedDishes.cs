using HarmonyLib;
using Kitchen;
using KitchenData;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace KitchenPlateupAP
{
    internal static class LockedDishes
    {
        private static HashSet<int> _unlockedDishIDs = new HashSet<int>();
        private static bool _lockingEnabled = false;

        // Dish IDs that are always kept regardless of the locked set (e.g. Steak = -959076098).
        private static readonly HashSet<int> _permanentDishIDs = new HashSet<int>
        {
            -959076098  // Steak — always present, cannot be removed
        };

        public static void EnableLocking()
        {
            _lockingEnabled = true;
            Mod.Logger?.LogInfo("[LockedDishes] Locking ENABLED");
        }

        public static void DisableLocking()
        {
            _lockingEnabled = false;
            _unlockedDishIDs.Clear();
            Mod.Logger?.LogInfo("[LockedDishes] Locking DISABLED and cleared");
        }

        public static bool IsLockingEnabled() => _lockingEnabled;

        public static void SetUnlockedDishes(IEnumerable<int> dishIDs)
        {
            _unlockedDishIDs = new HashSet<int>(dishIDs ?? Enumerable.Empty<int>());
            Mod.Logger?.LogInfo($"[LockedDishes] Set -> {string.Join(", ", _unlockedDishIDs)}");
        }

        public static void AddUnlockedDishes(IEnumerable<int> dishIDs)
        {
            if (dishIDs == null)
                return;
            foreach (int id in dishIDs)
                _unlockedDishIDs.Add(id);
            Mod.Logger?.LogInfo($"[LockedDishes] Add -> {string.Join(", ", _unlockedDishIDs)}");
        }

        public static IEnumerable<int> GetAvailableDishes() => _unlockedDishIDs;

        /// <summary>Returns true for dishes that must never be destroyed regardless of lock state.</summary>
        public static bool IsPermanent(int dishID) => _permanentDishIDs.Contains(dishID);
    }

    // CreateDishOptions is a one-shot FranchiseFirstFrameSystem that randomly shuffles
    // and physically places food pedestals from whatever CDishUpgrade entities exist at
    // that moment — it never re-runs and its pedestal entities are never touched by this
    // filter. Running after it (as this used to) meant disallowed dishes could already be
    // placed and visible in the hub before we ever destroyed the underlying CDishUpgrade
    // entity. Must run after GrantUpgrades (which creates the CDishUpgrade entities) but
    // before CreateDishOptions (which consumes them) to actually prevent this.
    [UpdateInGroup(typeof(SimulationSystemGroup))]
     [UpdateAfter(typeof(GrantUpgrades))]
     [UpdateBefore(typeof(CreateDishOptions))]
    public class FilterDishUpgradesSystem : SystemBase
    {
        private bool _loggedThisFrame = false;

        protected override void OnUpdate()
        {
            if (!ArchipelagoConnectionManager.ConnectionSuccessful || ArchipelagoConnectionManager.Session == null)
                return;

            if (!LockedDishes.IsLockingEnabled())
                return;

            HashSet<int> allowed = LockedDishes.GetAvailableDishes()?.ToHashSet() ?? new HashSet<int>();
            if (allowed.Count == 0)
                return;

            EntityManager entityManager = EntityManager;

            EntityQuery dishUpgradeQuery = GetEntityQuery(ComponentType.ReadOnly<CDishUpgrade>());
            using (NativeArray<Entity> entities = dishUpgradeQuery.ToEntityArray(Allocator.Temp))
            {
                if (!_loggedThisFrame && entities.Length > 0)
                {
                    var ids = new System.Text.StringBuilder("[LockedDishes] CDishUpgrade DishIDs present: ");
                    foreach (Entity e in entities)
                    {
                        if (!entityManager.Exists(e) || !entityManager.HasComponent<CDishUpgrade>(e)) continue;
                        int id = entityManager.GetComponentData<CDishUpgrade>(e).DishID;
                        // Omit permanent dishes from the diagnostic log entirely.
                        if (LockedDishes.IsPermanent(id)) continue;
                        ids.Append(id).Append(allowed.Contains(id) ? "(OK)" : "(BLOCKED)").Append(", ");
                    }
                    Mod.Logger?.LogInfo(ids.ToString());
                    _loggedThisFrame = true;
                }

                foreach (Entity entity in entities)
                {
                    if (!entityManager.Exists(entity) || !entityManager.HasComponent<CDishUpgrade>(entity))
                        continue;

                    CDishUpgrade data = entityManager.GetComponentData<CDishUpgrade>(entity);

                    // Never destroy permanent dishes — they exist outside the AP unlock pool.
                    if (LockedDishes.IsPermanent(data.DishID))
                        continue;

                    if (!allowed.Contains(data.DishID))
                    {
                        Mod.Logger?.LogInfo($"[LockedDishes] Destroying dish card DishID={data.DishID} (not in allowed set).");
                        entityManager.DestroyEntity(entity);
                    }
                }
            }
        }

        protected override void OnStartRunning() => _loggedThisFrame = false;
    }

    // The ECS UpdateBefore/UpdateAfter ordering on FilterDishUpgradesSystem above proved
    // insufficient in practice — Unity's automatic system-ordering solver doesn't
    // guarantee it runs before CreateDishOptions's one-shot placement. A Harmony prefix
    // is deterministic instead: it always executes synchronously immediately before
    // CreateDishOptions.OnUpdate()'s body, with no scheduling ambiguity. This mirrors
    // FilterDishUpgradesSystem's filter logic exactly, applied at the one moment it
    // actually needs to be guaranteed.
    [HarmonyPatch(typeof(CreateDishOptions), "OnUpdate")]
    internal static class CreateDishOptionsFilterPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CreateDishOptions __instance)
        {
            if (!ArchipelagoConnectionManager.ConnectionSuccessful || ArchipelagoConnectionManager.Session == null)
                return;

            if (!LockedDishes.IsLockingEnabled())
                return;

            HashSet<int> allowed = LockedDishes.GetAvailableDishes()?.ToHashSet() ?? new HashSet<int>();
            if (allowed.Count == 0)
                return;

            EntityManager entityManager = __instance.World.EntityManager;
            EntityQuery dishUpgradeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CDishUpgrade>());
            using (NativeArray<Entity> entities = dishUpgradeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!entityManager.Exists(entity) || !entityManager.HasComponent<CDishUpgrade>(entity))
                        continue;

                    CDishUpgrade data = entityManager.GetComponentData<CDishUpgrade>(entity);

                    if (LockedDishes.IsPermanent(data.DishID))
                        continue;

                    if (!allowed.Contains(data.DishID))
                    {
                        Mod.Logger?.LogInfo($"[LockedDishes] (Prefix) Destroying dish card DishID={data.DishID} before CreateDishOptions consumes it.");
                        entityManager.DestroyEntity(entity);
                    }
                }
            }

            // CreateDishOptions caps how many physical food pedestals it places at
            // 1 + (CUpgradeExtraDish entity count) — vanilla's "Dish Size Upgrade" franchise
            // upgrade. This apworld never grants that upgrade type, so the cap is always
            // exactly 1: whichever single CDishUpgrade entity happens to win Shuffle() gets
            // placed, and everything else (including the player's actual allowed dish) is
            // silently dropped. Raise the cap to fit every CDishUpgrade entity that survived
            // filtering above (the allowed dish(es) plus any permanent exemption like Steak),
            // capped naturally by CreateDishOptions's own position-list size (4-5), so nothing
            // meaningful ever gets left off again.
            EntityQuery remainingQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CDishUpgrade>());
            using (NativeArray<Entity> remainingEntities = remainingQuery.ToEntityArray(Allocator.Temp))
            {
                var ids = new System.Text.StringBuilder();
                foreach (Entity e in remainingEntities)
                {
                    if (!entityManager.Exists(e) || !entityManager.HasComponent<CDishUpgrade>(e))
                        continue;
                    ids.Append(entityManager.GetComponentData<CDishUpgrade>(e).DishID).Append(", ");
                }
                Mod.Logger?.LogWarning($"[LockedDishes] (Diag) Surviving CDishUpgrade entities right before CreateDishOptions: count={remainingEntities.Length}, dishes=[{ids}], allowedSet=[{string.Join(",", allowed)}]");
            }

            int remainingCandidates = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CDishUpgrade>()).CalculateEntityCount();
            int currentExtraDishSlots = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CUpgradeExtraDish>()).CalculateEntityCount();
            int neededExtraDishSlots = Math.Max(0, remainingCandidates - 1 - currentExtraDishSlots);
            for (int i = 0; i < neededExtraDishSlots; i++)
            {
                entityManager.CreateEntity(typeof(CUpgradeExtraDish));
            }
            if (neededExtraDishSlots > 0)
            {
                Mod.Logger?.LogInfo($"[LockedDishes] (Prefix) Added {neededExtraDishSlots} CUpgradeExtraDish slot(s) so CreateDishOptions can place all {remainingCandidates} surviving dish(es).");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(CreateDishOptions __instance)
        {
            EntityManager entityManager = __instance.World.EntityManager;

            EntityQuery choiceQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<CDishChoice>());
            using (NativeArray<Entity> choiceEntities = choiceQuery.ToEntityArray(Allocator.Temp))
            {
                var ids = new System.Text.StringBuilder();
                foreach (Entity e in choiceEntities)
                {
                    if (!entityManager.Exists(e) || !entityManager.HasComponent<CDishChoice>(e))
                        continue;
                    ids.Append(entityManager.GetComponentData<CDishChoice>(e).Dish).Append(", ");
                }
                Mod.Logger?.LogWarning($"[LockedDishes] (Diag) After CreateDishOptions: CDishChoice count={choiceEntities.Length}, dishes=[{ids}]");
            }
        }
    }

    // Both fixes above still race against CreateDishOptions's one-shot first-frame timing:
    // if it runs before the Archipelago connection handshake finishes (OnSuccessfulConnect
    // is what calls LockedDishes.EnableLocking()), ConnectionSuccessful/IsLockingEnabled
    // are still false at that exact moment and neither fix has anything to filter yet.
    // CreateDishOptions never runs again, so a missed frame is permanent — for its own
    // CDishUpgrade-based query.
    //
    // CDishChoice, however, lives on the actual placed food item in the hub (confirmed via
    // Kitchen.DishChoiceView.UpdateView, which continuously queries it every frame to drive
    // the visible item) rather than being a one-shot creation request. So instead of trying
    // to win the race against CreateDishOptions, this system reactively removes already-
    // placed disallowed items every frame — it naturally self-corrects on whichever frame
    // LockedDishes actually becomes ready, no matter when CreateDishOptions fired relative
    // to the AP connection.
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public class FilterPlacedDishChoicesSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            if (!ArchipelagoConnectionManager.ConnectionSuccessful || ArchipelagoConnectionManager.Session == null)
                return;

            if (!LockedDishes.IsLockingEnabled())
                return;

            HashSet<int> allowed = LockedDishes.GetAvailableDishes()?.ToHashSet() ?? new HashSet<int>();
            if (allowed.Count == 0)
                return;

            EntityManager entityManager = EntityManager;
            EntityQuery dishChoiceQuery = GetEntityQuery(ComponentType.ReadOnly<CDishChoice>());
            using (NativeArray<Entity> entities = dishChoiceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!entityManager.Exists(entity) || !entityManager.HasComponent<CDishChoice>(entity))
                        continue;

                    CDishChoice choice = entityManager.GetComponentData<CDishChoice>(entity);

                    if (LockedDishes.IsPermanent(choice.Dish))
                        continue;

                    if (!allowed.Contains(choice.Dish))
                    {
                        Mod.Logger?.LogInfo($"[LockedDishes] Removing placed dish item DishID={choice.Dish} (not in allowed set).");
                        entityManager.DestroyEntity(entity);
                    }
                }
            }
        }
    }
}