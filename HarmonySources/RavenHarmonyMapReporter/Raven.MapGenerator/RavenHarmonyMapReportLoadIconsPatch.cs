using System;
using System.Globalization;
using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "LoadIcons")]
internal static class RavenHarmonyMapReportLoadIconsPatch
{
	[HarmonyPrefix]
	private static void Prefix(object[] __args)
	{
		int imageWidth = 0;
		int imageHeight = 0;
		int mapResolution = 0;
		int oceanMargin = 0;
		try
		{
			if (__args != null && __args.Length >= 5)
			{
				imageWidth = Convert.ToInt32(__args[1], CultureInfo.InvariantCulture);
				imageHeight = Convert.ToInt32(__args[2], CultureInfo.InvariantCulture);
				mapResolution = Convert.ToInt32(__args[3], CultureInfo.InvariantCulture);
				oceanMargin = Convert.ToInt32(__args[4], CultureInfo.InvariantCulture);
			}
		}
		catch
		{
		}
		RavenHarmonyMapReport.BeginCapture(imageWidth, imageHeight, mapResolution, oceanMargin);
	}

	[HarmonyPostfix]
	private static void Postfix()
	{
		RavenHarmonyMapReport.FinishCapture();
	}
}
