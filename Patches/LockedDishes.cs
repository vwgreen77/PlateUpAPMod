using Kitchen;
using KitchenData;
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

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CreateDishOptions))]
    [UpdateAfter(typeof(GrantUpgrades))]
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
}