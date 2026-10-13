using System;
using HarmonyLib;
using UnityEngine;

namespace UniversalReplacer
{
    public static class HarmonyPatcher
    {
        private static void TryProcess(GameObject g)
        {
            Plugin plugin = Plugin.Instance;
            if (plugin == null || plugin.IsBursting || g == null) return;

            if (plugin.HasProcessed(g)) return;
            plugin.MarkProcessed(g);

            plugin.HandleNewObjectInstant(g);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
        static void PostInstantiate1(UnityEngine.Object __result) => TryProcess(__result as GameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object), typeof(Transform) })]
        static void PostInstantiate2(UnityEngine.Object __result) => TryProcess(__result as GameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object), typeof(Transform), typeof(bool) })]
        static void PostInstantiate3(UnityEngine.Object __result) => TryProcess(__result as GameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) })]
        static void PostInstantiate4(UnityEngine.Object __result) => TryProcess(__result as GameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion), typeof(Transform) })]
        static void PostInstantiate5(UnityEngine.Object __result) => TryProcess(__result as GameObject);

        [HarmonyPostfix, HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
        static void PostSetActive(GameObject __instance, bool value) { if (value) TryProcess(__instance); }
    }
}