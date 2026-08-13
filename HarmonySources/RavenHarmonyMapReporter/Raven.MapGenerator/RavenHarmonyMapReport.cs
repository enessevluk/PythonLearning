using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CustomGenerator;
using UnityEngine;

namespace Raven.MapGenerator;

internal static class RavenHarmonyMapReport
{
	private static readonly object Sync = new object();

	private static readonly List<RavenMonumentReportEntry> Entries = new List<RavenMonumentReportEntry>();

	private static bool _capturing;

	private static int _imageWidth;

	private static int _imageHeight;

	private static int _mapResolution;

	private static int _oceanMargin;

	public static void BeginCapture(int imageWidth, int imageHeight, int mapResolution, int oceanMargin)
	{
		lock (Sync)
		{
			Entries.Clear();
			_imageWidth = imageWidth;
			_imageHeight = imageHeight;
			_mapResolution = mapResolution;
			_oceanMargin = oceanMargin;
			_capturing = true;
		}
	}

	public static void Capture(MonumentInfo monument, string resolvedName)
	{
		if (monument == null)
		{
			return;
		}
		lock (Sync)
		{
			if (!_capturing)
			{
				return;
			}
			string text = (resolvedName ?? string.Empty).Replace("\n", string.Empty).Trim();
			string text2 = string.Empty;
			string monumentType = string.Empty;
			Vector3 vector = Vector3.zero;
			bool renderOnMap = false;
			try
			{
				text2 = (monument.name ?? string.Empty).Trim();
			}
			catch
			{
			}
			try
			{
				Type type = monument.GetType();
				FieldInfo field = type.GetField("Type");
				if (field != null)
				{
					object value = field.GetValue(monument);
					if (value != null)
					{
						monumentType = value.ToString();
					}
				}
				else
				{
					PropertyInfo property = type.GetProperty("Type");
					if (property != null)
					{
						object value2 = property.GetValue(monument, null);
						if (value2 != null)
						{
							monumentType = value2.ToString();
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				vector = monument.transform.position;
			}
			catch
			{
			}
			try
			{
				renderOnMap = monument.shouldDisplayOnMap && monument.mapIcon == null && text.IndexOf("train", StringComparison.OrdinalIgnoreCase) < 0;
			}
			catch
			{
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				text = text2;
			}
			for (int i = 0; i < Entries.Count; i++)
			{
				RavenMonumentReportEntry ravenMonumentReportEntry = Entries[i];
				if (string.Equals(ravenMonumentReportEntry.RawName, text2, StringComparison.OrdinalIgnoreCase) && Math.Abs(ravenMonumentReportEntry.X - vector.x) < 0.01f && Math.Abs(ravenMonumentReportEntry.Z - vector.z) < 0.01f)
				{
					return;
				}
			}
			Entries.Add(new RavenMonumentReportEntry
			{
				DisplayName = text,
				RawName = text2,
				MonumentType = monumentType,
				X = vector.x,
				Y = vector.y,
				Z = vector.z,
				RenderOnMap = renderOnMap
			});
		}
	}

	public static void FinishCapture()
	{
		List<RavenMonumentReportEntry> list;
		lock (Sync)
		{
			_capturing = false;
			list = new List<RavenMonumentReportEntry>(Entries);
		}
		try
		{
			uint num = ((ExtConfig.tempData != null) ? ExtConfig.tempData.mapsize : 0u);
			uint num2 = ((ExtConfig.tempData != null) ? ExtConfig.tempData.mapseed : 0u);
			string text = Path.Combine("HarmonyConfig", "RavenMapReports");
			Directory.CreateDirectory(text);
			string content = BuildJson(list, num, num2);
			string path = string.Format(CultureInfo.InvariantCulture, "RavenMapReport_{0}_{1}.json", num, num2);
			WriteAtomic(Path.Combine(text, path), content);
			WriteAtomic(Path.Combine(text, "RavenMapReport_latest.json"), content);
			Debug.Log(string.Format(CultureInfo.InvariantCulture, "[RavenMapReporter] Monument report written: {0} entries, size={1}, seed={2}", list.Count, num, num2));
		}
		catch (Exception ex)
		{
			Debug.LogError("[RavenMapReporter] Report could not be written: " + ex);
		}
	}

	private static string BuildJson(List<RavenMonumentReportEntry> entries, uint worldSize, uint seed)
	{
		StringBuilder stringBuilder = new StringBuilder(Math.Max(1024, entries.Count * 180));
		stringBuilder.Append("{\n");
		stringBuilder.Append("  \"format\": \"raven-harmony-monument-report-v1\",\n");
		stringBuilder.Append("  \"source\": \"CustomGenerator.MapImageRender.TerrainPath.Monuments\",\n");
		stringBuilder.Append("  \"generatedAtUtc\": \"").Append(JsonEscape(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append("\",\n");
		stringBuilder.Append("  \"worldSize\": ").Append(worldSize.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"seed\": ").Append(seed.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"imageWidth\": ").Append(_imageWidth.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"imageHeight\": ").Append(_imageHeight.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"mapResolution\": ").Append(_mapResolution.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"oceanMargin\": ").Append(_oceanMargin.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"renderOffset\": ").Append((_imageWidth - (_mapResolution + _oceanMargin)).ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"entryCount\": ").Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		stringBuilder.Append("  \"entries\": [\n");
		for (int i = 0; i < entries.Count; i++)
		{
			RavenMonumentReportEntry ravenMonumentReportEntry = entries[i];
			stringBuilder.Append("    {");
			stringBuilder.Append("\"displayName\": \"").Append(JsonEscape(ravenMonumentReportEntry.DisplayName)).Append("\", ");
			stringBuilder.Append("\"rawName\": \"").Append(JsonEscape(ravenMonumentReportEntry.RawName)).Append("\", ");
			stringBuilder.Append("\"monumentType\": \"").Append(JsonEscape(ravenMonumentReportEntry.MonumentType)).Append("\", ");
			stringBuilder.Append("\"x\": ").Append(ravenMonumentReportEntry.X.ToString("R", CultureInfo.InvariantCulture)).Append(", ");
			stringBuilder.Append("\"y\": ").Append(ravenMonumentReportEntry.Y.ToString("R", CultureInfo.InvariantCulture)).Append(", ");
			stringBuilder.Append("\"z\": ").Append(ravenMonumentReportEntry.Z.ToString("R", CultureInfo.InvariantCulture));
			stringBuilder.Append(", \"renderOnMap\": ").Append(ravenMonumentReportEntry.RenderOnMap ? "true" : "false");
			stringBuilder.Append("}");
			if (i + 1 < entries.Count)
			{
				stringBuilder.Append(',');
			}
			stringBuilder.Append('\n');
		}
		stringBuilder.Append("  ]\n");
		stringBuilder.Append("}\n");
		return stringBuilder.ToString();
	}

	private static void WriteAtomic(string path, string content)
	{
		string fullPath = Path.GetFullPath(path);
		string directoryName = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		string text = fullPath + ".tmp";
		File.WriteAllText(text, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		if (File.Exists(fullPath))
		{
			File.Delete(fullPath);
		}
		File.Move(text, fullPath);
	}

	private static string JsonEscape(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length + 16);
		foreach (char c in value)
		{
			switch (c)
			{
			case '\\':
				stringBuilder.Append("\\\\");
				continue;
			case '"':
				stringBuilder.Append("\\\"");
				continue;
			case '\b':
				stringBuilder.Append("\\b");
				continue;
			case '\f':
				stringBuilder.Append("\\f");
				continue;
			case '\n':
				stringBuilder.Append("\\n");
				continue;
			case '\r':
				stringBuilder.Append("\\r");
				continue;
			case '\t':
				stringBuilder.Append("\\t");
				continue;
			}
			if (c < ' ')
			{
				StringBuilder stringBuilder2 = stringBuilder.Append("\\u");
				int num = c;
				stringBuilder2.Append(num.ToString("x4", CultureInfo.InvariantCulture));
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}
}
