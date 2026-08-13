using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Raven.MapGenerator;

internal static class RavenBiomePercentages
{
	private static float ReadConfiguredJungleRatio(float fallbackRatio)
	{
		try
		{
			Type type = AccessTools.TypeByName("CustomGenerator.ExtConfig");
			FieldInfo fieldInfo = AccessTools.Field(type, "Config");
			object obj = ((fieldInfo == null) ? null : fieldInfo.GetValue(null));
			FieldInfo fieldInfo2 = ((obj == null) ? null : AccessTools.Field(obj.GetType(), "Generator"));
			object obj2 = ((fieldInfo2 == null) ? null : fieldInfo2.GetValue(obj));
			FieldInfo fieldInfo3 = ((obj2 == null) ? null : AccessTools.Field(obj2.GetType(), "Biom"));
			object obj3 = ((fieldInfo3 == null) ? null : fieldInfo3.GetValue(obj2));
			FieldInfo fieldInfo4 = ((obj3 == null) ? null : AccessTools.Field(obj3.GetType(), "Jungle"));
			object obj4 = ((fieldInfo4 == null) ? null : fieldInfo4.GetValue(obj3));
			if (obj4 == null)
			{
				return Mathf.Clamp01(fallbackRatio);
			}
			float num = Convert.ToSingle(obj4, CultureInfo.InvariantCulture);
			return Mathf.Clamp01(num / 100f);
		}
		catch
		{
			return Mathf.Clamp01(fallbackRatio);
		}
	}

	public static void CorrectCustomGeneratorNormalization()
	{
		try
		{
			WorldConfig config = World.Config;
			if (config != null)
			{
				float num = config.PercentageBiomeArid + config.PercentageBiomeTemperate + config.PercentageBiomeTundra + config.PercentageBiomeArctic;
				if (num <= 0.0001f)
				{
					Debug.LogError("[RavenBiomeFix] Ana biyom toplamı sıfır; CustomGenerator düzeltmesi uygulanamadı.");
					return;
				}
				float percentageBiomeJungle = ReadConfiguredJungleRatio(config.PercentageBiomeJungle / num);
				config.PercentageBiomeArid /= num;
				config.PercentageBiomeTemperate /= num;
				config.PercentageBiomeTundra /= num;
				config.PercentageBiomeArctic /= num;
				config.PercentageBiomeJungle = percentageBiomeJungle;
				Debug.Log(string.Format(CultureInfo.InvariantCulture, "[RavenBiomeFix] Ana biyomlar bağımsız normalize edildi. Arid={0:P1}, Temperate={1:P1}, Tundra={2:P1}, Arctic={3:P1}, Jungle dönüşümü={4:P1}", config.PercentageBiomeArid, config.PercentageBiomeTemperate, config.PercentageBiomeTundra, config.PercentageBiomeArctic, config.PercentageBiomeJungle));
			}
		}
		catch (Exception ex)
		{
			Debug.LogError("[RavenBiomeFix] Biyom yüzdeleri düzeltilemedi: " + ex);
		}
	}
}
