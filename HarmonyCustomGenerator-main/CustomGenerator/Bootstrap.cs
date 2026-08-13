using HarmonyLib;
using UnityEngine;
using CustomGenerator.Utility;

using static CustomGenerator.ExtConfig;
namespace CustomGenerator {
    [HarmonyPatch(typeof(Bootstrap), "StartupShared")]
    internal static class Bootstrap_StartupShared {
        [HarmonyPrefix]
        private static void Prefix() {

            Logging.StartingMessage();

            Rust.Ai.AiManager.nav_disable = true;
            Rust.Ai.AiManager.nav_wait = false;

            Logging.ClearOldLogs();
        }
    }
}
