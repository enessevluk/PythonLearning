using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using CustomGenerator.Utility;
using HarmonyLib;

namespace Raven.MapGenerator;

[HarmonyPatch(typeof(MapImageRender), "RenderGrid")]
internal static class CorrectRustGridNumbersPatch
{
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> UseZeroBasedGrid(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
		int fixedBranches = 0;
		for (int i = 0; i < codes.Count; i++)
		{
			CodeInstruction current = codes[i];
			yield return current;
			if (fixedBranches < 2 && !(current.opcode != OpCodes.Sub) && i + 1 < codes.Count)
			{
				CodeInstruction next = codes[i + 1];
				if (next.opcode == OpCodes.Box && next.operand as Type == typeof(int))
				{
					yield return new CodeInstruction(OpCodes.Ldc_I4_1);
					yield return new CodeInstruction(OpCodes.Sub);
					fixedBranches++;
				}
			}
		}
	}
}
