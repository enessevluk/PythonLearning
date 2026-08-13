using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Raven.MapGenerator;

internal static class RavenMonumentLabels
{
	private static readonly object Sync = new object();

	private static readonly List<RavenLabelEntry> Entries = new List<RavenLabelEntry>();

	private static readonly string ConfigPath = Path.Combine("HarmonyConfig", "RavenMonuments.json");

	private static DateTime _loadedWriteTimeUtc = DateTime.MinValue;

	private static bool _useIcons;

	private static string Clean(string value)
	{
		return (value ?? string.Empty).Replace("\n", string.Empty).Trim();
	}

	private static void EnsureLoaded()
	{
		lock (Sync)
		{
			if (!File.Exists(ConfigPath))
			{
				Entries.Clear();
				_useIcons = false;
				_loadedWriteTimeUtc = DateTime.MinValue;
				return;
			}
			DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(ConfigPath);
			if (Entries.Count > 0 && lastWriteTimeUtc == _loadedWriteTimeUtc)
			{
				return;
			}
			try
			{
				JObject jObject = JObject.Parse(File.ReadAllText(ConfigPath));
				_useIcons = string.Equals(Clean((string)jObject["renderMode"]), "icons", StringComparison.OrdinalIgnoreCase);
				JArray jArray = jObject["entries"] as JArray;
				List<RavenLabelEntry> list = new List<RavenLabelEntry>();
				if (jArray != null)
				{
					foreach (JToken item in jArray)
					{
						string text = Clean((string)item["label"]);
						JArray jArray2 = item["matches"] as JArray;
						if (string.IsNullOrWhiteSpace(text) || jArray2 == null)
						{
							continue;
						}
						RavenLabelEntry ravenLabelEntry = new RavenLabelEntry();
						ravenLabelEntry.Label = text;
						RavenLabelEntry ravenLabelEntry2 = ravenLabelEntry;
						foreach (JToken item2 in jArray2)
						{
							string text2 = Clean((string)item2);
							if (!string.IsNullOrWhiteSpace(text2))
							{
								ravenLabelEntry2.Matches.Add(text2);
							}
						}
						if (ravenLabelEntry2.Matches.Count > 0)
						{
							list.Add(ravenLabelEntry2);
						}
					}
				}
				Entries.Clear();
				Entries.AddRange(list);
				_loadedWriteTimeUtc = lastWriteTimeUtc;
				Debug.Log("[RavenLabels] Monument config loaded: " + Entries.Count);
			}
			catch (Exception ex)
			{
				Debug.LogError("[RavenLabels] Monument config could not be loaded: " + ex.Message);
			}
		}
	}

	public static string Translate(string displayName, string rawName)
	{
		EnsureLoaded();
		string text = Clean(displayName);
		string text2 = Clean(rawName);
		lock (Sync)
		{
			foreach (RavenLabelEntry entry in Entries)
			{
				foreach (string match in entry.Matches)
				{
					if (text.Equals(match, StringComparison.OrdinalIgnoreCase) || text2.Equals(match, StringComparison.OrdinalIgnoreCase) || text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return entry.Label;
					}
				}
			}
			return displayName;
		}
	}

	public static bool UseIcons()
	{
		EnsureLoaded();
		lock (Sync)
		{
			return _useIcons;
		}
	}
}
