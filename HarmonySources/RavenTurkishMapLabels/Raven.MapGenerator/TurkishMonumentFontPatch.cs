using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "RenderMonument")]
internal static class TurkishMonumentFontPatch
{
	[HarmonyPrefix]
	private static bool UseIconModeOrTurkishFont(ref string fontPath)
	{
		fontPath = "mapimages/resources/dinprobold.otf";
		return !RavenMonumentLabels.UseIcons();
	}
}
