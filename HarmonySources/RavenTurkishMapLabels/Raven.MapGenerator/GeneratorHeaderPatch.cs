using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "RenderGithub")]
internal static class GeneratorHeaderPatch
{
	[HarmonyPrefix]
	private static bool SkipGeneratorHeader()
	{
		return false;
	}
}
