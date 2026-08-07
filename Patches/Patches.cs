using HarmonyLib;
using Kitchen;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Archipelago.MultiClient.Net.Enums;

namespace KitchenPlateupAP
{

    [HarmonyPatch(typeof(Archipelago.MultiClient.Net.Converters.PermissionsEnumConverter), "ReadJson")]
    internal static class Patch_PermissionsEnumConverter_ReadJson
    {
        static bool Prefix(
            ref object __result,
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            bool consumedReader = false;

            try
            {
                // Let original run for native Permissions targets only.
                if (objectType == typeof(Permissions) ||
                    objectType == typeof(Permissions?) ||
                    IsPermissionsCollection(objectType))
                {
                    return true;
                }

                JToken token = JToken.Load(reader);
                consumedReader = true;

                if (IsStringList(objectType))
                {
                    var strings = token.Type == JTokenType.Array
                        ? token.Children()
                            .Select(t => (string)t)
                            .Where(s => !string.Equals(s, "Disabled", StringComparison.OrdinalIgnoreCase))
                            .ToList()
                        : new List<string>();

                    if (objectType.IsArray)
                    {
                        __result = strings.ToArray();
                    }
                    else
                    {
                        // Works for List<string>, IList<string>, IEnumerable<string>
                        __result = strings;
                    }

                    return false;
                }

                var cleanSerializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    Converters = new List<JsonConverter>()
                });

                __result = token.ToObject(objectType, cleanSerializer);
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlateupAP][PermPatch] Failed for {objectType}: {ex.Message}");

                // Critical: if we already consumed the reader, DO NOT run original.
                if (consumedReader)
                {
                    __result = GetDefaultValue(objectType);
                    return false;
                }

                return true;
            }
        }

        private static object GetDefaultValue(Type t)
        {
            if (t == typeof(string))
                return string.Empty;

            if (t.IsValueType)
                return Activator.CreateInstance(t);

            if (t.IsArray)
                return Array.CreateInstance(t.GetElementType(), 0);

            if (typeof(IEnumerable<string>).IsAssignableFrom(t))
                return new List<string>();

            return null;
        }

        private static bool IsPermissionsCollection(Type t)
        {
            if (!typeof(IEnumerable).IsAssignableFrom(t))
                return false;

            if (t.IsArray)
                return t.GetElementType() == typeof(Permissions);

            if (t.IsGenericType)
            {
                var arg = t.GetGenericArguments().FirstOrDefault();
                return arg == typeof(Permissions);
            }

            return false;
        }

        private static bool IsStringList(Type t)
        {
            if (t == typeof(List<string>) || t == typeof(IList<string>) || t == typeof(IEnumerable<string>))
                return true;

            if (t.IsArray && t.GetElementType() == typeof(string))
                return true;

            return false;
        }
    }

    [HarmonyPatch(typeof(DeterminePlayerSpeed), "OnUpdate")]
    public static class Patch_DeterminePlayerSpeed_OnUpdate
    {
        // Let vanilla run always.
        [HarmonyPrefix]
        static bool Prefix()
        {
            return true;
        }

        [HarmonyPostfix]
        static void Postfix(DeterminePlayerSpeed __instance)
        {
            if (Mod.Instance == null)
                return;

            if (!Mod.IsSessionActive)
                return;

            // Apply AP movement modifiers only during day/cooking.
            if (!__instance.HasSingleton<SIsDayTime>())
                return;

            var em = __instance.EntityManager;
            using var query = em.CreateEntityQuery(ComponentType.ReadWrite<CPlayer>());
            using var playerEntities = query.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < playerEntities.Length; i++)
            {
                var playerEntity = playerEntities[i];

                if (!em.Exists(playerEntity) || !em.HasComponent<CPlayer>(playerEntity))
                    continue;

                var player = em.GetComponentData<CPlayer>(playerEntity);
                int entityKey = playerEntity.Index;

                if (!Mod.playerBaseSpeeds.ContainsKey(entityKey) || Mod.playerBaseSpeeds[entityKey] <= 0f)
                    Mod.playerBaseSpeeds[entityKey] = player.Speed;

                float baseSpeed = Mod.playerBaseSpeeds[entityKey];
                
                // ADD NULL SAFETY HERE:
                float slowMultiplier = 1.0f;
                try
                {
                    slowMultiplier = Mod.Instance.GetPlayerSpeedMultiplier(playerEntity);
                }
                catch (Exception ex)
                {
                    // Log once to avoid spam, then use default multiplier
                    UnityEngine.Debug.LogWarning($"[PlateupAP] GetPlayerSpeedMultiplier error: {ex.Message}");
                }
                
                player.Speed = baseSpeed * Mod.movementSpeedMod * slowMultiplier;
                em.SetComponentData(playerEntity, player);
            }
        }
    }
}