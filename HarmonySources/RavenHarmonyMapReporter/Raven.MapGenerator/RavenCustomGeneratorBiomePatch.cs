using System;
using System.Reflection;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch]
internal static class RavenCustomGeneratorBiomePatch
{
	private static MethodBase TargetMethod()
	{
		Type type = AccessTools.TypeByName("CustomGenerator.Patches.Timing_Start");
		if (!(type == null))
		{
			return AccessTools.Method(type, "LoadPercentages");
		}
		return null;
	}

	[HarmonyPostfix]
	private static void Postfix()
	{
		RavenBiomePercentages.CorrectCustomGeneratorNormalization();
	}
}
