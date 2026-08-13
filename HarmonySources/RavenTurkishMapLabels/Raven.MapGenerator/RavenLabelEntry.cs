using System.Collections.Generic;

namespace Raven.MapGenerator;

internal sealed class RavenLabelEntry
{
	public string Label;

	public readonly List<string> Matches = new List<string>();
}
