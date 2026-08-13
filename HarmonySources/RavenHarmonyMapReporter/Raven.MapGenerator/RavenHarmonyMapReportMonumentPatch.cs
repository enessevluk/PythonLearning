using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "GetMonumentName")]
internal static class RavenHarmonyMapReportMonumentPatch
{
	[HarmonyPostfix]
	private static void Postfix(MonumentInfo monument, string __result)
	{
		RavenHarmonyMapReport.Capture(monument, __result);
	}
}
