using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "GetMonumentName")]
internal static class TurkishMonumentLabelPatch
{
	[HarmonyPostfix]
	private static void TranslateLabel(MonumentInfo monument, ref string __result)
	{
		if (!string.IsNullOrWhiteSpace(__result))
		{
			string rawName = string.Empty;
			try
			{
				rawName = ((monument == null) ? string.Empty : monument.name);
			}
			catch
			{
			}
			__result = RavenMonumentLabels.Translate(__result, rawName);
		}
	}
}
