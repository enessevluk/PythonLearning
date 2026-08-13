using System;
using System.Collections;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UDebug = UnityEngine.Debug;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

[assembly: AssemblyTitle("RavenWorldObjectReporterV43")]
[assembly: AssemblyDescription("Raven Map Panel authoritative saved-map monument prefab + runtime/path/topology reporter")]
[assembly: AssemblyVersion("5.1.0.0")]
[assembly: AssemblyFileVersion("5.1.0.0")]

namespace Raven.MapGenerator
{
    // CustomGenerator, UI_LoadingScreen.Update("DONE") içinde önce haritayı
    // kaydeder, ardından PNG renderını başlatır. Raporu burada, CustomGenerator
    // prefixinden ÖNCE hazırlamak saved .map + TerrainPath + Topology verisinin
    // aynı anda erişilebilir olduğu en güvenilir noktadır.
    [HarmonyPatch]
    [HarmonyBefore("com.facepunch.rust_dedicated.CustomGenerator")]
    public static class RavenWorldDataDonePatch
    {
        private static bool _ran;

        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UI_LoadingScreen");
            return type == null ? null : AccessTools.Method(type, "Update", new Type[] { typeof(string) });
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static void Prefix(ref string strType)
        {
            if (_ran || !String.Equals(strType, "DONE", StringComparison.Ordinal)) return;
            _ran = true;
            UDebug.Log("[RavenWorldDataReporterV43] DONE detected; saved-map report starting before PNG render.");
            try { RavenWorldDataReporter.WriteReport("loading_done_before_png", true); }
            catch (Exception ex) { UDebug.LogError("[RavenWorldDataReporterV43] DONE report failed: " + ex); }
        }
    }

    // DONE patchi farklı CustomGenerator sürümünde hedeflenemezse PNG sonrası
    // ikinci şans. Başarılı final rapor zaten yazıldıysa WriteReport hemen döner.
    [HarmonyPatch]
    public static class RavenWorldDataRenderFallbackPatch
    {
        private static MethodBase TargetMethod()
        {
            Type mapImageType = AccessTools.TypeByName("CustomGenerator.Utility.MapImage");
            return mapImageType == null ? null : AccessTools.Method(mapImageType, "RenderMap");
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            UDebug.Log("[RavenWorldDataReporterV43] PNG render finished; fallback report check.");
            try { RavenWorldDataReporter.WriteReport("after_map_png", false); }
            catch (Exception ex) { UDebug.LogError("[RavenWorldDataReporterV43] PNG fallback report failed: " + ex); }
        }
    }

    internal sealed class RavenWorldEntry
    {
        public string Category;
        public string DisplayName;
        public string RawName;
        public string Source;
        public float X;
        public float Y;
        public float Z;
    }

    internal sealed class RavenComponent
    {
        public int Cells;
        public double SumX;
        public double SumZ;
    }

    internal sealed class RavenGodRockCandidate
    {
        public string Path;
        public string Category;
        public Vector3 Position;
        public bool StrongIdentity;
        public bool HugeIdentity;
    }

    internal static class RavenWorldDataReporter
    {
        private static readonly string[] Categories = {
            // Authoritative root monument prefabs read directly from the generated .map.
            "launch_site", "military_tunnels", "airfield", "trainyard", "powerplant",
            "water_treatment", "excavator", "junkyard", "nuclear_silo",
            "arctic_research_base", "desert_military_base", "ferry_terminal",
            "satellite_dish", "dome", "apartment_complex", "ziggurat", "radtown",
            "supermarket", "gas_station", "sewer_branch", "warehouse",
            "abandoned_cabins", "outpost", "bandit_camp", "stables", "harbor",
            "fishing_village", "lighthouse", "oilrig_large", "oilrig_small",
            "underwater_labs", "caves", "water_wells", "stone_quarry",
            "sulfur_quarry", "hqm_quarry",
            // World/environment and infrastructure categories.
            "icebergs", "swamps", "ruins", "power_substations",
            "powerlines", "god_rocks", "metro_entrances", "lakes", "canyons",
            "oases"
        };

        private static readonly object Sync = new object();
        private static readonly HashSet<string> FinalWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, string> PrefabPathCache = new Dictionary<uint, string>();
        private static readonly Dictionary<string, MemberInfo> MemberCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        private static MethodInfo CachedStringPoolResolver;
        private static bool StringPoolResolverResolved;

        private static string Normalize(string value)
        {
            return String.IsNullOrEmpty(value) ? String.Empty : value.Replace('\\', '/').ToLowerInvariant();
        }

        private static string LargePowerlineVariant(string path, string category)
        {
            string p = Normalize(path).Trim();
            string c = Normalize(category).Trim();

            // The map icon represents the large Rust transmission pylons exposed
            // by RustEdit/RustMaps as Powerline A/B/C/D. Never confuse these with
            // /decor/powerline-small/powerline_pole_a (roadside utility poles) or
            // powerlineplatform_a/b/c (zipline platforms).
            if (p.IndexOf("/powerline-small/", StringComparison.Ordinal) >= 0) return String.Empty;
            if (p.IndexOf("powerlineplatform", StringComparison.Ordinal) >= 0) return String.Empty;

            string[] letters = { "a", "b", "c", "d" };
            for (int i = 0; i < letters.Length; i++)
            {
                string l = letters[i];
                if (c == "powerline " + l || c == "powerline_" + l || c == "powerline-" + l || c == "powerline" + l)
                    return l.ToUpperInvariant();
            }

            int slash = p.LastIndexOf('/');
            string file = slash >= 0 ? p.Substring(slash + 1) : p;
            bool largeFamily = p.IndexOf("/decor/powerline/", StringComparison.Ordinal) >= 0;

            for (int i = 0; i < letters.Length; i++)
            {
                string l = letters[i];
                if (file == "powerline_" + l + ".prefab"
                    || file == "powerline-" + l + ".prefab"
                    || file == "powerline " + l + ".prefab"
                    || file == "powerline" + l + ".prefab")
                    return l.ToUpperInvariant();

                // Some Rust builds expose the same large tower family with a
                // powerline_pole_* basename. Only accept those in /decor/powerline/.
                if (largeFamily && (file == "powerline_pole_" + l + ".prefab"
                    || file == "powerline-pole-" + l + ".prefab"))
                    return l.ToUpperInvariant();
            }

            return String.Empty;
        }

        private static bool IsLargePowerlineIdentity(string path, string category)
        {
            return !String.IsNullOrEmpty(LargePowerlineVariant(path, category));
        }

        private static string RuntimePowerlineVariant(string value)
        {
            string n = Normalize(value).Trim();
            if (n.EndsWith("(clone)", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 7).Trim();

            string[] letters = { "a", "b", "c", "d" };
            for (int i = 0; i < letters.Length; i++)
            {
                string l = letters[i];
                if (n == "powerline " + l || n == "powerline_" + l || n == "powerline-" + l || n == "powerline" + l
                    || n == "powerline_pole_" + l || n == "powerline-pole-" + l)
                    return l.ToUpperInvariant();
            }
            return String.Empty;
        }

        private static bool IsRuntimeLargePowerlineName(string value)
        {
            return !String.IsNullOrEmpty(RuntimePowerlineVariant(value));
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            string normalized = Normalize(value);
            for (int i = 0; i < tokens.Length; i++)
                if (normalized.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static MemberInfo FindMember(Type type, params string[] names)
        {
            if (type == null) return null;
            string key = (type.AssemblyQualifiedName ?? type.FullName ?? type.Name) + "|" + String.Join("|", names);
            MemberInfo cached;
            if (MemberCache.TryGetValue(key, out cached)) return cached;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo found = null;
            FieldInfo[] fields = type.GetFields(flags);
            PropertyInfo[] properties = type.GetProperties(flags);
            for (int n = 0; n < names.Length && found == null; n++)
            {
                for (int i = 0; i < fields.Length; i++)
                    if (String.Equals(fields[i].Name, names[n], StringComparison.OrdinalIgnoreCase)) { found = fields[i]; break; }
                if (found != null) break;
                for (int i = 0; i < properties.Length; i++)
                    if (String.Equals(properties[i].Name, names[n], StringComparison.OrdinalIgnoreCase)) { found = properties[i]; break; }
            }
            MemberCache[key] = found;
            return found;
        }

        private static object GetMember(object obj, params string[] names)
        {
            if (obj == null) return null;
            Type type = obj as Type;
            object target = type == null ? obj : null;
            if (type == null) type = obj.GetType();
            MemberInfo member = FindMember(type, names);
            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null) return field.GetValue(field.IsStatic ? null : target);
                PropertyInfo property = member as PropertyInfo;
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    MethodInfo getter = property.GetGetMethod(true);
                    return property.GetValue(getter != null && getter.IsStatic ? null : target, null);
                }
            }
            catch { }
            return null;
        }

        private static bool HasMember(object obj, params string[] names)
        {
            if (obj == null) return false;
            Type type = obj as Type;
            if (type == null) type = obj.GetType();
            return FindMember(type, names) != null;
        }

        private static uint ReadUInt(object obj, params string[] names)
        {
            object value = GetMember(obj, names);
            try { return value == null ? 0U : Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0U; }
        }

        private static float ReadFloat(object obj, params string[] names)
        {
            object value = GetMember(obj, names);
            try { return value == null ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch { return 0f; }
        }

        private static string ReadString(object obj, params string[] names)
        {
            object value = GetMember(obj, names);
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
        }

        private static IEnumerable AsEnumerable(object value)
        {
            if (value == null || value is string) return null;
            return value as IEnumerable;
        }

        private static bool TryReadVector(object obj, out Vector3 vector)
        {
            vector = Vector3.zero;
            if (obj == null) return false;
            if (obj is Vector3)
            {
                vector = (Vector3)obj;
                return true;
            }
            if (!HasMember(obj, "x", "X") || !HasMember(obj, "z", "Z")) return false;
            vector = new Vector3(ReadFloat(obj, "x", "X"), ReadFloat(obj, "y", "Y"), ReadFloat(obj, "z", "Z"));
            return true;
        }

        private static bool TryReadTransformPosition(object obj, out Vector3 position)
        {
            position = Vector3.zero;
            if (obj == null) return false;
            try
            {
                Component component = obj as Component;
                if (component != null && component.transform != null)
                {
                    position = component.transform.position;
                    return true;
                }
            }
            catch { }

            object transform = GetMember(obj, "transform", "Transform");
            if (transform == null) return false;
            return TryReadVector(GetMember(transform, "position", "Position"), out position);
        }

        private static bool IsRuntimeGodRockIdentity(string english, string translated, string objectName, string gameObjectName)
        {
            string identity = Normalize((english ?? String.Empty) + " | " + (translated ?? String.Empty)
                + " | " + (objectName ?? String.Empty) + " | " + (gameObjectName ?? String.Empty));
            // World Update 2.0 formations are distinct. Never let Anvil/Arch Rock
            // leak into the God Rock category merely because all contain "rock".
            if (ContainsAny(identity, "anvil rock", "anvil_rock", "anvilrock", "arch rock", "arch_rock", "archrock")) return false;
            return ContainsAny(identity, "large god rock", "medium god rock", "small god rock",
                "god rock", "god_rock", "god-rock", "godrock");
        }

        private static bool IsRockLikeRuntimeIdentity(string value)
        {
            string n = Normalize(value);
            return n.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("formation", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("cliff", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsValidRuntimeSceneComponent(object obj)
        {
            try
            {
                Component component = obj as Component;
                if (component == null || component.gameObject == null) return false;
                object scene = GetMember(component.gameObject, "scene", "Scene");
                if (scene == null) return true;
                MethodInfo isValid = AccessTools.Method(scene.GetType(), "IsValid", Type.EmptyTypes);
                if (isValid == null) return true;
                object result = isValid.Invoke(scene, null);
                return result == null || Convert.ToBoolean(result, CultureInfo.InvariantCulture);
            }
            catch { return true; }
        }

        private static bool ProcessRuntimeMonument(object monument, List<RavenWorldEntry> entries, HashSet<string> seen,
            List<string> diagnostics, ref int rockLike, ref int godRocks, ref int invalidPositions, ref int diagnosticSamples)
        {
            if (monument == null) return false;
            object phrase = GetMember(monument, "displayPhrase", "DisplayPhrase");
            string english = ReadString(phrase, "english", "English");
            string translated = ReadString(phrase, "translated", "Translated");
            string objectName = ReadString(monument, "name", "Name");
            object gameObject = GetMember(monument, "gameObject", "GameObject");
            string gameObjectName = ReadString(gameObject, "name", "Name");
            string identity = english + " | " + translated + " | " + objectName + " | " + gameObjectName;

            if (IsRockLikeRuntimeIdentity(identity))
            {
                rockLike++;
                if (diagnosticSamples < 120)
                {
                    Vector3 samplePosition;
                    string positionText = TryReadTransformPosition(monument, out samplePosition)
                        ? samplePosition.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
                            + samplePosition.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
                            + samplePosition.z.ToString("0.0", CultureInfo.InvariantCulture)
                        : "<unavailable>";
                    diagnostics.Add("Runtime rock monument: english=" + english + ", translated=" + translated
                        + ", name=" + objectName + ", gameObject=" + gameObjectName + ", position=" + positionText);
                    diagnosticSamples++;
                }
            }

            if (!IsRuntimeGodRockIdentity(english, translated, objectName, gameObjectName)) return false;
            Vector3 position;
            if (!TryReadTransformPosition(monument, out position))
            {
                invalidPositions++;
                diagnostics.Add("Runtime God Rock rejected: position unavailable | " + identity);
                return false;
            }
            string raw = "raven://runtime-monument-info/" + identity;
            int before = entries.Count;
            AddEntry(entries, seen, "god_rocks", raw, "terrainmeta_path_monuments", position);
            if (entries.Count > before) godRocks++;
            return true;
        }

        private static bool CollectRuntimeMonuments(List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics)
        {
            List<object> runtimeObjects = new List<object>();
            int terrainMetaCount = 0, sceneScanCount = 0;

            Type terrainMeta = AccessTools.TypeByName("TerrainMeta");
            object terrainPath = terrainMeta == null ? null : GetMember(terrainMeta, "Path");
            if (terrainPath != null && HasMember(terrainPath, "Monuments", "monuments"))
            {
                IEnumerable monuments = AsEnumerable(GetMember(terrainPath, "Monuments", "monuments"));
                if (monuments != null)
                {
                    foreach (object monument in monuments)
                    {
                        if (monument == null) continue;
                        terrainMetaCount++;
                        if (!runtimeObjects.Contains(monument)) runtimeObjects.Add(monument);
                    }
                }
            }
            else
            {
                diagnostics.Add("TerrainMeta.Path.Monuments unavailable at phase");
            }

            // Secondary runtime probe. Some newly introduced natural formations
            // may carry MonumentInfo but be omitted from a particular Path list.
            // Only live scene components are accepted; loaded prefab assets are
            // excluded by Scene.IsValid().
            try
            {
                Type monumentInfoType = AccessTools.TypeByName("MonumentInfo");
                MethodInfo findAll = AccessTools.Method(typeof(Resources), "FindObjectsOfTypeAll", new Type[] { typeof(Type) });
                if (monumentInfoType != null && findAll != null)
                {
                    IEnumerable allObjects = AsEnumerable(findAll.Invoke(null, new object[] { monumentInfoType }));
                    if (allObjects != null)
                    {
                        foreach (object monument in allObjects)
                        {
                            if (monument == null || !IsValidRuntimeSceneComponent(monument)) continue;
                            sceneScanCount++;
                            if (!runtimeObjects.Contains(monument)) runtimeObjects.Add(monument);
                        }
                    }
                }
                else diagnostics.Add("Runtime MonumentInfo scene scan unavailable");
            }
            catch (Exception ex)
            {
                diagnostics.Add("Runtime MonumentInfo scene scan exception: " + ex.GetType().Name + ": " + ex.Message);
            }

            int rockLike = 0, godRocks = 0, invalidPositions = 0, diagnosticSamples = 0;
            for (int i = 0; i < runtimeObjects.Count; i++)
                ProcessRuntimeMonument(runtimeObjects[i], entries, seen, diagnostics, ref rockLike, ref godRocks, ref invalidPositions, ref diagnosticSamples);

            diagnostics.Add("Runtime monuments: unique=" + runtimeObjects.Count.ToString(CultureInfo.InvariantCulture)
                + ", terrainMeta=" + terrainMetaCount.ToString(CultureInfo.InvariantCulture)
                + ", sceneScan=" + sceneScanCount.ToString(CultureInfo.InvariantCulture)
                + ", rockLike=" + rockLike.ToString(CultureInfo.InvariantCulture)
                + ", godRocks=" + godRocks.ToString(CultureInfo.InvariantCulture)
                + ", invalidPositions=" + invalidPositions.ToString(CultureInfo.InvariantCulture));
            // A normal procedural world has many MonumentInfo entries. An empty
            // result at DONE generally means world setup has not finished, so the
            // post-PNG fallback patch gets another chance.
            return runtimeObjects.Count > 0;
        }

        private static string RuntimeTransformIdentity(Transform transform)
        {
            StringBuilder identity = new StringBuilder();
            Transform current = transform;
            for (int depth = 0; current != null && depth < 10; depth++, current = current.parent)
            {
                if (identity.Length > 0) identity.Append(" / ");
                identity.Append(current.gameObject == null ? current.name : current.gameObject.name);
            }
            return identity.ToString();
        }

        private static bool IsExactGodRockHugeCPath(string path)
        {
            string n = Normalize(path ?? String.Empty).Replace('\\', '/').Trim();
            if (n.Length == 0) return false;
            int slash = n.LastIndexOf('/');
            string basename = slash >= 0 ? n.Substring(slash + 1) : n;
            return String.Equals(basename, "rock_formation_huge_c.prefab", StringComparison.OrdinalIgnoreCase)
                || String.Equals(basename, "rock_formation_huge_c", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRuntimeGodRockHugeCRootName(string value)
        {
            string n = Normalize(value ?? String.Empty).Trim();
            if (n.EndsWith("(clone)", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 7).Trim();
            return String.Equals(n, "rock_formation_huge_c", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExactHugeVariantName(string value)
        {
            string n = Normalize(value ?? String.Empty).Trim();
            if (n.EndsWith("(clone)", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 7).Trim();
            const string prefix = "rock_formation_huge_";
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string suffix = n.Substring(prefix.Length);
            if (suffix.Length != 1 || suffix[0] < 'a' || suffix[0] > 'z') return null;
            return suffix.ToUpperInvariant();
        }

        private static bool RuntimeHierarchyLooksLikeMonument(Transform transform)
        {
            Transform current = transform;
            for (int depth = 0; current != null && depth < 14; depth++, current = current.parent)
            {
                string name = Normalize(current.gameObject == null ? current.name : current.gameObject.name);
                if (ContainsAny(name,
                    "assets/bundled/prefabs/autospawn/monument/",
                    "/monument/", "monument/",
                    "deepsea_", "floating_city", "tropical_island_dwelling",
                    "spawnpoint tester")) return true;
            }
            return false;
        }

        private static int CollectRuntimeGodRockHugeCRoots(List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics)
        {
            // Facepunch's World Update 2.0 history ties the modern God Rock fixes
            // directly to rock_formation_huge_c. Unlike the old heuristic cluster
            // detector, this scan accepts ONLY the live scene root whose own name
            // is exactly rock_formation_huge_c. LOD/collider children, Anvil/Arch
            // formations and generic huge rocks are never promoted to God Rock.
            int scanned = 0, accepted = 0, invalidPositions = 0, monumentRejected = 0;
            Dictionary<string, int> hugeVariants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> acceptedObjects = new HashSet<int>();
            try
            {
                UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(typeof(Transform));
                if (objects == null)
                {
                    diagnostics.Add("Runtime exact God Rock huge_c scan unavailable: transform list is null");
                    return 0;
                }

                Stopwatch timer = Stopwatch.StartNew();
                for (int i = 0; i < objects.Length; i++)
                {
                    if (timer.Elapsed.TotalSeconds > 12)
                    {
                        diagnostics.Add("Runtime exact God Rock huge_c scan stopped at 12 second budget");
                        break;
                    }
                    Transform transform = objects[i] as Transform;
                    if (transform == null || !IsValidRuntimeSceneComponent(transform)) continue;
                    scanned++;

                    string localName = transform.gameObject == null ? transform.name : transform.gameObject.name;
                    string variant = ExactHugeVariantName(localName);
                    if (!String.IsNullOrEmpty(variant))
                    {
                        int oldCount;
                        hugeVariants.TryGetValue(variant, out oldCount);
                        hugeVariants[variant] = oldCount + 1;
                    }
                    if (!IsRuntimeGodRockHugeCRootName(localName)) continue;

                    // If the same prefab appears as decorative content inside a
                    // monument/test/deepsea hierarchy it is not a procedural God Rock.
                    if (RuntimeHierarchyLooksLikeMonument(transform))
                    {
                        monumentRejected++;
                        continue;
                    }

                    Transform root = transform;
                    while (root.parent != null)
                    {
                        string parentName = root.parent.gameObject == null ? root.parent.name : root.parent.gameObject.name;
                        if (!IsRuntimeGodRockHugeCRootName(parentName)) break;
                        root = root.parent;
                    }
                    if (!acceptedObjects.Add(root.GetInstanceID())) continue;

                    Vector3 position = root.position;
                    if (Single.IsNaN(position.x) || Single.IsNaN(position.z) || Single.IsInfinity(position.x) || Single.IsInfinity(position.z))
                    {
                        invalidPositions++;
                        continue;
                    }

                    int before = entries.Count;
                    AddEntry(entries, seen, "god_rocks",
                        "raven://runtime-rock-formation/rock_formation_huge_c",
                        "runtime_godrock_huge_c_exact", position);
                    if (entries.Count > before)
                    {
                        accepted++;
                        if (accepted <= 30)
                            diagnostics.Add("Exact God Rock huge_c root: position="
                                + position.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
                                + position.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
                                + position.z.ToString("0.0", CultureInfo.InvariantCulture)
                                + " | hierarchy=" + RuntimeTransformIdentity(root));
                    }
                }

                StringBuilder variants = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in hugeVariants)
                {
                    if (variants.Length > 0) variants.Append("; ");
                    variants.Append(kv.Key).Append("=").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                diagnostics.Add("Runtime exact God Rock huge_c roots: scanned=" + scanned.ToString(CultureInfo.InvariantCulture)
                    + ", accepted=" + accepted.ToString(CultureInfo.InvariantCulture)
                    + ", monumentRejected=" + monumentRejected.ToString(CultureInfo.InvariantCulture)
                    + ", invalidPositions=" + invalidPositions.ToString(CultureInfo.InvariantCulture));
                diagnostics.Add("Runtime exact huge-formation root variants: " + variants.ToString());
                return accepted;
            }
            catch (Exception ex)
            {
                diagnostics.Add("Runtime exact God Rock huge_c scan exception: " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static bool CollectRuntimeNamedGodRockRoots(List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics)
        {
            // God Rock is not exposed as MonumentInfo in every Rust build. Scan
            // live scene transforms as a second authoritative, name-based source.
            // Scene.IsValid excludes prefab assets loaded only as resources.
            int scanned = 0, named = 0, invalidPositions = 0;
            HashSet<int> acceptedObjects = new HashSet<int>();
            try
            {
                UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(typeof(Transform));
                if (objects == null)
                {
                    diagnostics.Add("Runtime named God Rock scan unavailable: transform list is null");
                    return false;
                }
                Stopwatch timer = Stopwatch.StartNew();
                for (int i = 0; i < objects.Length; i++)
                {
                    if (timer.Elapsed.TotalSeconds > 8)
                    {
                        diagnostics.Add("Runtime named God Rock scan stopped at 8 second budget");
                        break;
                    }
                    Transform transform = objects[i] as Transform;
                    if (transform == null || !IsValidRuntimeSceneComponent(transform)) continue;
                    scanned++;
                    string identity = RuntimeTransformIdentity(transform);
                    if (!IsRuntimeGodRockIdentity(String.Empty, String.Empty, identity, transform.name)) continue;

                    // Prefer the highest explicitly named ancestor so child
                    // renderers/colliders do not create multiple icons.
                    Transform root = transform;
                    Transform parent = transform.parent;
                    while (parent != null && IsRuntimeGodRockIdentity(String.Empty, String.Empty, RuntimeTransformIdentity(parent), parent.name))
                    {
                        root = parent;
                        parent = parent.parent;
                    }
                    if (!acceptedObjects.Add(root.GetInstanceID())) continue;
                    Vector3 position = root.position;
                    if (Single.IsNaN(position.x) || Single.IsNaN(position.z) || Single.IsInfinity(position.x) || Single.IsInfinity(position.z))
                    {
                        invalidPositions++;
                        continue;
                    }
                    int before = entries.Count;
                    AddEntry(entries, seen, "god_rocks", "raven://runtime-scene-root/" + identity,
                        "runtime_scene_named_root", position);
                    if (entries.Count > before) named++;
                }
                diagnostics.Add("Runtime named God Rock roots: scanned=" + scanned.ToString(CultureInfo.InvariantCulture)
                    + ", accepted=" + named.ToString(CultureInfo.InvariantCulture)
                    + ", invalidPositions=" + invalidPositions.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                diagnostics.Add("Runtime named God Rock scan exception: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void CollectVectors(object obj, List<Vector3> result, int depth)
        {
            if (obj == null || result == null || depth > 4 || result.Count >= 8192) return;
            Vector3 vector;
            if (TryReadVector(obj, out vector)) { result.Add(vector); return; }

            IEnumerable enumerable = AsEnumerable(obj);
            if (enumerable != null)
            {
                int count = 0;
                foreach (object item in enumerable)
                {
                    CollectVectors(item, result, depth + 1);
                    if (++count >= 8192 || result.Count >= 8192) break;
                }
                return;
            }

            Type type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || obj is decimal || obj is DateTime) return;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo[] fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                string name = Normalize(fields[i].Name);
                if (name.IndexOf("node") < 0 && name.IndexOf("point") < 0 && name.IndexOf("path") < 0 &&
                    name.IndexOf("position") < 0 && name.IndexOf("vector") < 0 && name.IndexOf("start") < 0 &&
                    name.IndexOf("end") < 0 && name.IndexOf("control") < 0 && name.IndexOf("way") < 0) continue;
                try { CollectVectors(fields[i].GetValue(obj), result, depth + 1); } catch { }
            }
            PropertyInfo[] properties = type.GetProperties(flags);
            for (int i = 0; i < properties.Length; i++)
            {
                if (properties[i].GetIndexParameters().Length != 0) continue;
                string name = Normalize(properties[i].Name);
                if (name.IndexOf("node") < 0 && name.IndexOf("point") < 0 && name.IndexOf("path") < 0 &&
                    name.IndexOf("position") < 0 && name.IndexOf("vector") < 0 && name.IndexOf("start") < 0 &&
                    name.IndexOf("end") < 0 && name.IndexOf("control") < 0 && name.IndexOf("way") < 0) continue;
                try { CollectVectors(properties[i].GetValue(obj, null), result, depth + 1); } catch { }
            }
        }

        private static Vector3 Centroid(List<Vector3> vectors)
        {
            if (vectors == null || vectors.Count == 0) return Vector3.zero;
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < vectors.Count; i++) { x += vectors[i].x; y += vectors[i].y; z += vectors[i].z; }
            return new Vector3((float)(x / vectors.Count), (float)(y / vectors.Count), (float)(z / vectors.Count));
        }

        private static string CommandLineValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (!String.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length) return (args[i + 1] ?? String.Empty).Trim('"');
            }
            return String.Empty;
        }

        private static void ReadMapIdentity(out uint worldSize, out uint seed, out string identity)
        {
            worldSize = 0U; seed = 0U; identity = String.Empty;
            try
            {
                // Gerçek sınıf CustomGenerator.Config değil, ExtConfig'tir.
                Type extConfig = AccessTools.TypeByName("CustomGenerator.ExtConfig");
                object temp = extConfig == null ? null : GetMember(extConfig, "tempData");
                worldSize = ReadUInt(temp, "mapsize", "mapSize");
                seed = ReadUInt(temp, "mapseed", "mapSeed");
            }
            catch { }
            try
            {
                Type world = AccessTools.TypeByName("World");
                if (worldSize == 0U) worldSize = ReadUInt(world, "Size");
                if (seed == 0U) seed = ReadUInt(world, "Seed");
            }
            catch { }
            uint parsed;
            if (worldSize == 0U && UInt32.TryParse(CommandLineValue("+server.worldsize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) worldSize = parsed;
            if (seed == 0U && UInt32.TryParse(CommandLineValue("+server.seed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) seed = parsed;
            identity = CommandLineValue("+server.identity");
        }

        private static object GetTerrainPath()
        {
            try
            {
                Type extConfig = AccessTools.TypeByName("CustomGenerator.ExtConfig");
                object temp = extConfig == null ? null : GetMember(extConfig, "tempData");
                object path = GetMember(temp, "terrainPath", "TerrainPath");
                if (path != null) return path;
            }
            catch { }
            try
            {
                Type terrainMeta = AccessTools.TypeByName("TerrainMeta");
                return terrainMeta == null ? null : GetMember(terrainMeta, "Path");
            }
            catch { return null; }
        }

        private static string ResolveMapPath(uint worldSize, uint seed)
        {
            try
            {
                Type world = AccessTools.TypeByName("World");
                string folder = Convert.ToString(GetMember(world, "MapFolderName"), CultureInfo.InvariantCulture);
                string file = Convert.ToString(GetMember(world, "MapFileName"), CultureInfo.InvariantCulture);
                if (!String.IsNullOrWhiteSpace(folder) && !String.IsNullOrWhiteSpace(file))
                {
                    string combined = Path.GetFullPath(Path.Combine(folder, file));
                    if (File.Exists(combined)) return combined;
                }
            }
            catch { }

            try
            {
                string maps = Path.GetFullPath("maps");
                if (!Directory.Exists(maps)) return null;
                string seedText = seed.ToString(CultureInfo.InvariantCulture);
                string sizeText = worldSize.ToString(CultureInfo.InvariantCulture);
                string[] files = Directory.GetFiles(maps, "*.map", SearchOption.TopDirectoryOnly);
                string best = null;
                DateTime bestTime = DateTime.MinValue;
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    if (seed != 0U && name.IndexOf(seedText, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (worldSize != 0U && name.IndexOf(sizeText, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    DateTime time = File.GetLastWriteTimeUtc(files[i]);
                    if (time > bestTime) { best = files[i]; bestTime = time; }
                }
                if (best != null) return Path.GetFullPath(best);

                // İsim şablonu farklıysa son 15 dakikada yazılan en yeni harita.
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-15);
                for (int i = 0; i < files.Length; i++)
                {
                    DateTime time = File.GetLastWriteTimeUtc(files[i]);
                    if (time < cutoff || time <= bestTime) continue;
                    best = files[i]; bestTime = time;
                }
                return best == null ? null : Path.GetFullPath(best);
            }
            catch { return null; }
        }

        private static object LoadSavedWorld(string mapPath, List<string> diagnostics)
        {
            if (String.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
            {
                diagnostics.Add("Saved map file unavailable");
                return null;
            }
            try
            {
                Type type = AccessTools.TypeByName("WorldSerialization");
                if (type == null) { diagnostics.Add("WorldSerialization type unavailable"); return null; }
                object serialization = Activator.CreateInstance(type);
                MethodInfo load = AccessTools.Method(type, "Load", new Type[] { typeof(string) });
                if (load == null) { diagnostics.Add("WorldSerialization.Load(string) unavailable"); return null; }
                load.Invoke(serialization, new object[] { mapPath });
                object world = GetMember(serialization, "world", "World");
                diagnostics.Add("Serialized map loaded: " + mapPath);
                return world;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                diagnostics.Add("Serialized map load failed: " + inner.GetType().Name + ": " + inner.Message);
                return null;
            }
            catch (Exception ex)
            {
                diagnostics.Add("Serialized map load failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static object GetInMemoryWorldData()
        {
            try
            {
                Type world = AccessTools.TypeByName("World");
                object serialization = world == null ? null : GetMember(world, "Serialization");
                return GetMember(serialization, "world", "World", "data", "Data");
            }
            catch { return null; }
        }

        private static MethodInfo GetStringPoolResolver()
        {
            if (StringPoolResolverResolved) return CachedStringPoolResolver;
            StringPoolResolverResolved = true;
            Type type = AccessTools.TypeByName("StringPool");
            if (type == null) return null;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            string[] preferred = { "Get", "GetString", "Find" };
            for (int p = 0; p < preferred.Length; p++)
            {
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!String.Equals(method.Name, preferred[p], StringComparison.OrdinalIgnoreCase) || method.ReturnType != typeof(string)) continue;
                    ParameterInfo[] pars = method.GetParameters();
                    if (pars.Length != 1) continue;
                    Type pt = pars[0].ParameterType;
                    if (pt == typeof(uint) || pt == typeof(int) || pt == typeof(ulong) || pt == typeof(long))
                    {
                        CachedStringPoolResolver = method;
                        return method;
                    }
                }
            }
            return null;
        }

        private static string ResolvePrefabPath(uint id)
        {
            if (id == 0U) return String.Empty;
            string cached;
            if (PrefabPathCache.TryGetValue(id, out cached)) return cached;
            string result = String.Empty;
            MethodInfo method = GetStringPoolResolver();
            if (method != null)
            {
                try
                {
                    Type pt = method.GetParameters()[0].ParameterType;
                    object arg = Convert.ChangeType(id, pt, CultureInfo.InvariantCulture);
                    result = method.Invoke(null, new object[] { arg }) as string ?? String.Empty;
                }
                catch { }
            }
            PrefabPathCache[id] = result;
            return result;
        }

        private static bool PathEndsWithAny(string path, params string[] endings)
        {
            string normalized = Normalize(path);
            for (int i = 0; i < endings.Length; i++)
            {
                string ending = Normalize(endings[i]);
                if (normalized.EndsWith(ending, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool PathContainsAny(string path, params string[] tokens)
        {
            string normalized = Normalize(path);
            for (int i = 0; i < tokens.Length; i++)
                if (normalized.IndexOf(Normalize(tokens[i]), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string ClassifyRootMonumentPrefab(string path)
        {
            string n = Normalize(path);
            if (String.IsNullOrEmpty(n)) return null;

            // Match complete root-prefab paths only. Child/decor prefabs inside a
            // monument must never create extra monument icons.
            if (PathEndsWithAny(n, "/monument/xlarge/launch_site_1.prefab", "/monument/large/launch_site_1.prefab")) return "launch_site";
            if (PathEndsWithAny(n, "/monument/large/military_tunnel_1.prefab")) return "military_tunnels";
            if (PathEndsWithAny(n, "/monument/large/airfield_1.prefab")) return "airfield";
            if (PathEndsWithAny(n, "/monument/large/trainyard_1.prefab")) return "trainyard";
            if (PathEndsWithAny(n, "/monument/large/powerplant_1.prefab")) return "powerplant";
            if (PathEndsWithAny(n, "/monument/large/water_treatment_plant_1.prefab")) return "water_treatment";
            if (PathEndsWithAny(n, "/monument/large/excavator_1.prefab")) return "excavator";
            if (PathEndsWithAny(n, "/monument/medium/junkyard_1.prefab")) return "junkyard";
            if (PathEndsWithAny(n, "/monument/medium/nuclear_missile_silo.prefab")) return "nuclear_silo";
            if (PathContainsAny(n, "/monument/arctic_bases/arctic_research_base_") && n.EndsWith(".prefab")) return "arctic_research_base";
            if (PathContainsAny(n, "/monument/military_bases/desert_military_base_") && n.EndsWith(".prefab")) return "desert_military_base";
            if (PathContainsAny(n, "/monument/harbor/ferry_terminal_") && n.EndsWith(".prefab")) return "ferry_terminal";
            if (PathEndsWithAny(n, "/monument/small/satellite_dish.prefab")) return "satellite_dish";
            if (PathEndsWithAny(n, "/monument/small/sphere_tank.prefab")) return "dome";
            if (PathContainsAny(n, "/monument/medium/apartments_complex_", "/monument/medium/apartment_complex_") && n.EndsWith(".prefab")) return "apartment_complex";
            if (PathContainsAny(n, "/monument/jungle_ruins/jungle_ziggurat_") && n.EndsWith(".prefab")) return "ziggurat";
            if (PathContainsAny(n, "/monument/jungle_ruins/jungle_ruins_") && n.EndsWith(".prefab")) return "ruins";
            if (PathEndsWithAny(n, "/monument/roadside/radtown_1.prefab")) return "radtown";
            if (PathEndsWithAny(n, "/monument/roadside/supermarket_1.prefab")) return "supermarket";
            if (PathEndsWithAny(n, "/monument/roadside/gas_station_1.prefab")) return "gas_station";
            if (PathEndsWithAny(n, "/monument/medium/radtown_small_3.prefab", "/monument/medium/sewer_branch_1.prefab")) return "sewer_branch";
            // Vanilla Mining Outpost is historically stored as roadside/warehouse.prefab.
            // Raven Map Panel exposes the single canonical Warehouse entry.
            if (PathEndsWithAny(n, "/monument/roadside/warehouse.prefab", "/monument/small/mining_outpost_1.prefab")) return "warehouse";
            if (PathContainsAny(n, "/monument/abandoned_cabins") && n.EndsWith(".prefab")) return "abandoned_cabins";
            if (PathEndsWithAny(n, "/monument/medium/compound.prefab")) return "outpost";
            if (PathEndsWithAny(n, "/monument/medium/bandit_town.prefab")) return "bandit_camp";
            if (PathEndsWithAny(n, "/monument/small/stables_a.prefab", "/monument/small/stables_b.prefab")) return "stables";
            if (PathEndsWithAny(n, "/monument/harbor/harbor_1.prefab", "/monument/harbor/harbor_2.prefab")) return "harbor";
            if (PathContainsAny(n, "/monument/fishing_village/fishing_village_") && n.EndsWith(".prefab")) return "fishing_village";
            if (PathEndsWithAny(n, "/monument/lighthouse/lighthouse.prefab")) return "lighthouse";
            if (PathEndsWithAny(n, "/monument/offshore/oilrig_1.prefab")) return "oilrig_large";
            if (PathEndsWithAny(n, "/monument/offshore/oilrig_2.prefab")) return "oilrig_small";
            if (PathContainsAny(n, "/monument/underwater_lab/underwater_lab_") && n.EndsWith(".prefab")) return "underwater_labs";
            if (PathContainsAny(n, "/monument/cave/cave_") && n.EndsWith(".prefab")) return "caves";
            if (PathContainsAny(n, "/monument/tiny/water_well_") && n.EndsWith(".prefab")) return "water_wells";
            if (PathEndsWithAny(n, "/monument/small/mining_quarry_b.prefab")) return "stone_quarry";
            if (PathEndsWithAny(n, "/monument/small/mining_quarry_a.prefab")) return "sulfur_quarry";
            if (PathEndsWithAny(n, "/monument/small/mining_quarry_c.prefab")) return "hqm_quarry";
            return null;
        }

        private static bool IsAuthoritativeRootMonumentCategory(string category)
        {
            switch (category)
            {
                case "launch_site": case "military_tunnels": case "airfield": case "trainyard":
                case "powerplant": case "water_treatment": case "excavator": case "junkyard":
                case "nuclear_silo": case "arctic_research_base": case "desert_military_base":
                case "ferry_terminal": case "satellite_dish": case "dome": case "apartment_complex":
                case "ziggurat": case "radtown": case "supermarket": case "gas_station":
                case "sewer_branch": case "warehouse": case "abandoned_cabins":
                case "outpost": case "bandit_camp": case "stables": case "harbor":
                case "fishing_village": case "lighthouse": case "oilrig_large": case "oilrig_small":
                case "underwater_labs": case "caves": case "water_wells": case "stone_quarry":
                case "sulfur_quarry": case "hqm_quarry":
                    return true;
                default:
                    return false;
            }
        }

        private static string ClassifyPrefab(string path, string category)
        {
            string rootMonument = ClassifyRootMonumentPrefab(path);
            if (!String.IsNullOrEmpty(rootMonument)) return rootMonument;
            string n = Normalize(path + " | " + category);
            if (ContainsAny(n, "/iceberg/", "iceberg_", "/icebergs/")) return "icebergs";
            if (ContainsAny(n, "/power substations/", "/power_substations/", "power_sub_small_", "power_sub_big_")) return "power_substations";
            // Raven's powerline icon represents the large Rust/RustEdit
            // Powerline A/B/C/D transmission towers shown by RustMaps. They are NOT
            // zipline platforms and NOT the small roadside power pole family.
            if (IsLargePowerlineIdentity(path, category)) return "powerlines";

            if (ContainsAny(n, "tunnel-entrance", "tunnel_entrance", "subway_entrance", "metro_entrance")) return "metro_entrances";
            // v3.4.9: the prefab-selection probe for a zero-God-Rock seed saw
            // every large formation root B-J but no A root. Raven's controlled
            // God Rock slot therefore uses the exact rock_formation_a ROOT only.
            // Child rock_formation_huge_* objects remain explicitly excluded.
            if (IsGodRockRootAIdentity(path)) return "god_rocks";
            // rock_formation_huge_c is a reusable child in many large formations;
            // it must never be promoted to God Rock by itself.
            // `rock_formation_*` bütün büyük/dekoratif kaya parçalarının ortak
            // prefab ailesidir; God Rock'a özel değildir. Bu geniş aileyi God
            // Rock saymak binlerce yanlış kayıt ve haritanın her yerine ikon
            // basılmasına neden olur. Yalnız açık God Rock adı/path'i kabul edilir.
            if (ContainsAny(n, "godrock", "god_rock", "god rock", "large_god_rock", "medium_god_rock", "small_god_rock")) return "god_rocks";
            if (ContainsAny(n, "/unique_environment/oasis/", "/unique_environment/oases/", "ue_oasis_")) return "oases";
            if (ContainsAny(n, "/unique_environment/canyon/", "/unique_environment/canyons/", "ue_canyon_")) return "canyons";
            if (ContainsAny(n, "/unique_environment/lake/", "/unique_environment/lakes/", "ue_lake_")) return "lakes";
            if (ContainsAny(n, "/monument/swamp/swamp_", "/unique_environment/jungle/ue_jungle_swamp_")) return "swamps";
            if (ContainsAny(n, "/jungle_ruins/", "jungle_ruins_")) return "ruins";
            return null;
        }

        private static string DisplayName(string category)
        {
            switch (category)
            {
                case "launch_site": return "Launch Site";
                case "military_tunnels": return "Military Tunnel";
                case "airfield": return "Airfield";
                case "trainyard": return "Train Yard";
                case "powerplant": return "Power Plant";
                case "water_treatment": return "Water Treatment Plant";
                case "excavator": return "Giant Excavator Pit";
                case "junkyard": return "Junkyard";
                case "nuclear_silo": return "Missile Silo";
                case "arctic_research_base": return "Arctic Research Base";
                case "desert_military_base": return "Abandoned Military Base";
                case "ferry_terminal": return "Ferry Terminal";
                case "satellite_dish": return "Satellite Dish";
                case "dome": return "The Dome";
                case "apartment_complex": return "Apartment Complex";
                case "ziggurat": return "Ziggurat";
                case "radtown": return "Radtown";
                case "supermarket": return "Abandoned Supermarket";
                case "gas_station": return "Oxum's Gas Station";
                case "sewer_branch": return "Sewer Branch";
                case "warehouse": return "Warehouse";
                case "abandoned_cabins": return "Abandoned Cabins";
                case "outpost": return "Outpost";
                case "bandit_camp": return "Bandit Camp";
                case "stables": return "Stables";
                case "harbor": return "Harbor";
                case "fishing_village": return "Fishing Village";
                case "lighthouse": return "Lighthouse";
                case "oilrig_large": return "Large Oil Rig";
                case "oilrig_small": return "Small Oil Rig";
                case "underwater_labs": return "Underwater Lab";
                case "caves": return "Cave";
                case "water_wells": return "Water Well";
                case "stone_quarry": return "Stone Quarry";
                case "sulfur_quarry": return "Sulfur Quarry";
                case "hqm_quarry": return "HQM Quarry";
                case "icebergs": return "Iceberg";
                case "swamps": return "Swamp";
                case "ruins": return "Ruins";
                case "power_substations": return "Power Substation";
                case "powerlines": return "Powerline";
                case "god_rocks": return "Large God Rock";
                case "metro_entrances": return "Metro Tunnel Entrance";
                case "lakes": return "Lake";
                case "canyons": return "Canyon";
                case "oases": return "Oasis";
                default: return category;
            }
        }

        private static float DedupeMeters(string category)
        {
            if (category == "powerlines") return 12f;
            if (category == "lakes") return 220f;
            if (category == "swamps") return 80f;
            // Runtime named roots are already de-duplicated by instance id. Keep
            // this radius small enough that two nearby formations remain visible.
            if (category == "god_rocks") return 60f;
            return 12f;
        }

        private static string PositionKey(string category, Vector3 position)
        {
            float step = DedupeMeters(category);
            return category + ":" + Math.Round(position.x / step).ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(position.z / step).ToString(CultureInfo.InvariantCulture);
        }

        private static void AddEntry(List<RavenWorldEntry> entries, HashSet<string> seen, string category, string rawName, string source, Vector3 position)
        {
            if (String.IsNullOrEmpty(category)) return;

            // Saved-map and runtime scans can both see the same large pylon. Use
            // true X/Z distance for powerlines instead of grid rounding so a
            // tower close to a quantization boundary is not duplicated.
            if (String.Equals(category, "powerlines", StringComparison.OrdinalIgnoreCase))
            {
                const float duplicateRadius = 18f;
                float r2 = duplicateRadius * duplicateRadius;
                for (int i = 0; i < entries.Count; i++)
                {
                    RavenWorldEntry existing = entries[i];
                    if (!String.Equals(existing.Category, "powerlines", StringComparison.OrdinalIgnoreCase)) continue;
                    float dx = existing.X - position.x;
                    float dz = existing.Z - position.z;
                    if ((dx * dx) + (dz * dz) <= r2) return;
                }
            }

            string key = PositionKey(category, position);
            if (!seen.Add(key)) return;
            entries.Add(new RavenWorldEntry {
                Category = category, DisplayName = DisplayName(category), RawName = rawName ?? String.Empty,
                Source = source ?? String.Empty, X = position.x, Y = position.y, Z = position.z
            });
        }

        private static bool StrongGodRockIdentity(string path, string category)
        {
            if (IsGodRockRootAIdentity(path)) return true;
            string n = Normalize((path ?? String.Empty) + " | " + (category ?? String.Empty));
            string c = Normalize(category).Trim();
            if (c == "god rock" || c == "large god rock" || c == "medium god rock" || c == "small god rock") return true;
            if (ContainsAny(n, "/rock_formation_god", "/god_rock.prefab", "/godrock.prefab", "god_rock_formation", "godrock_formation")) return true;
            return false;
        }

        private static bool IsGodRockRootAIdentity(string path)
        {
            string normalized = Normalize(path).Trim();
            return String.Equals(normalized,
                "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool GenericRockFormationCandidate(string path, string category)
        {
            string n = Normalize((path ?? String.Empty) + " | " + (category ?? String.Empty));
            return ContainsAny(n, "/rock_formations/", "/rock_formation/", "rock_formation_");
        }

        private static bool HugeRockFormationIdentity(string path, string category)
        {
            string n = Normalize((path ?? String.Empty) + " | " + (category ?? String.Empty));
            return ContainsAny(n, "rock_formation_huge_", "huge rock formation");
        }

        private static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static string GodRockBucketKey(Vector3 position, float size)
        {
            int bx = (int)Math.Floor(position.x / size);
            int bz = (int)Math.Floor(position.z / size);
            return bx.ToString(CultureInfo.InvariantCulture) + ":" + bz.ToString(CultureInfo.InvariantCulture);
        }

        private static void AddGodRockClusters(List<RavenGodRockCandidate> candidates, List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics)
        {
            if (candidates == null || candidates.Count == 0)
            {
                diagnostics.Add("God Rock candidates: 0");
                return;
            }

            // Generic rock prefabs can form a map-wide chain when neighbouring
            // pieces are joined recursively. God Rock is instead a local, tall
            // formation. Evaluate a fixed neighbourhood around explicit/large
            // formation roots so unrelated cliffs can never merge together.
            const float neighbourhoodMeters = 100f;
            const float dedupeMeters = 150f;
            List<Vector3> acceptedPositions = new List<Vector3>();
            int accepted = 0, rejected = 0;
            for (int start = 0; start < candidates.Count; start++)
            {
                RavenGodRockCandidate anchor = candidates[start];
                bool largeRoot = ContainsAny(anchor.Path, "/v3_rock_formations_large/", "/rock_formations_v2/rock_formation_huge_");
                if (!anchor.StrongIdentity && !anchor.HugeIdentity && !largeRoot) continue;

                List<int> cluster = new List<int>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (DistanceXZ(anchor.Position, candidates[i].Position) <= neighbourhoodMeters) cluster.Add(i);
                }

                int strong = 0, huge = 0;
                float minY = Single.MaxValue, maxY = Single.MinValue;
                HashSet<string> distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < cluster.Count; i++)
                {
                    RavenGodRockCandidate candidate = candidates[cluster[i]];
                    centroid += candidate.Position;
                    minY = Math.Min(minY, candidate.Position.y);
                    maxY = Math.Max(maxY, candidate.Position.y);
                    if (candidate.StrongIdentity) strong++;
                    if (candidate.HugeIdentity) huge++;
                    if (!String.IsNullOrWhiteSpace(candidate.Path)) distinctPaths.Add(candidate.Path);
                }
                centroid /= Math.Max(1, cluster.Count);
                float maxRadius = 0f;
                for (int i = 0; i < cluster.Count; i++)
                    maxRadius = Math.Max(maxRadius, DistanceXZ(candidates[cluster[i]].Position, centroid));
                float verticalSpan = maxY >= minY ? maxY - minY : 0f;

                // Named roots are authoritative. Unnamed procedural versions
                // must be dense, varied and distinctly tall inside a local area.
                bool trusted = strong > 0 ||
                    (cluster.Count >= 8 && distinctPaths.Count >= 5 && verticalSpan >= 38f && maxRadius <= 105f);
                if (!trusted)
                {
                    rejected++;
                    if (rejected <= 20)
                    {
                        string rejectedSample = candidates[cluster[0]].Path ?? String.Empty;
                        diagnostics.Add("Rejected God Rock cluster: pieces=" + cluster.Count.ToString(CultureInfo.InvariantCulture)
                            + ", distinct=" + distinctPaths.Count.ToString(CultureInfo.InvariantCulture)
                            + ", sample=" + rejectedSample);
                    }
                    continue;
                }

                bool duplicate = false;
                for (int i = 0; i < acceptedPositions.Count; i++)
                {
                    if (DistanceXZ(acceptedPositions[i], centroid) < dedupeMeters) { duplicate = true; break; }
                }
                if (duplicate) continue;

                string sample = candidates[cluster[0]].Path ?? String.Empty;
                string raw = "raven://godrock-cluster/pieces=" + cluster.Count.ToString(CultureInfo.InvariantCulture)
                    + "/distinct=" + distinctPaths.Count.ToString(CultureInfo.InvariantCulture)
                    + "/strong=" + strong.ToString(CultureInfo.InvariantCulture)
                    + "/huge=" + huge.ToString(CultureInfo.InvariantCulture)
                    + "/vertical=" + verticalSpan.ToString("0.0", CultureInfo.InvariantCulture)
                    + "/radius=" + maxRadius.ToString("0.0", CultureInfo.InvariantCulture)
                    + "|" + sample;
                AddEntry(entries, seen, "god_rocks", raw, "saved_map_godrock_local_formation", centroid);
                acceptedPositions.Add(centroid);
                accepted++;
                if (accepted >= 24) break;
            }
            diagnostics.Add("God Rock candidates: " + candidates.Count.ToString(CultureInfo.InvariantCulture)
                + ", accepted clusters: " + accepted.ToString(CultureInfo.InvariantCulture)
                + ", rejected clusters: " + rejected.ToString(CultureInfo.InvariantCulture));
        }

        private static bool CollectWorldPrefabs(object worldData, List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics, bool collectGodRockFallback)
        {
            if (worldData == null) { diagnostics.Add("WorldData unavailable"); return false; }
            bool memberExists = HasMember(worldData, "prefabs", "Prefabs");
            IEnumerable prefabs = AsEnumerable(GetMember(worldData, "prefabs", "Prefabs"));
            if (!memberExists) { diagnostics.Add("WorldData.prefabs member unavailable"); return false; }
            if (prefabs == null) { diagnostics.Add("WorldData.prefabs is empty/null"); return true; }

            int scanned = 0, unresolved = 0, candidates = 0, rootMonuments = 0, powerlineCandidates = 0, powerlineCandidateLogged = 0, largePowerlineCandidateLogged = 0;
            Dictionary<string, int> powerlineBasenameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<RavenGodRockCandidate> godRockCandidates = new List<RavenGodRockCandidate>();
            Stopwatch timer = Stopwatch.StartNew();
            foreach (object prefab in prefabs)
            {
                if (++scanned > 300000) { diagnostics.Add("World prefab safety limit reached"); break; }
                if (timer.Elapsed.TotalSeconds > 50) { diagnostics.Add("World prefab scan exceeded 50 second budget"); return false; }
                uint id = ReadUInt(prefab, "id", "ID", "prefabID", "PrefabID");
                string path = ResolvePrefabPath(id);
                if (String.IsNullOrEmpty(path)) { unresolved++; continue; }
                string categoryText = ReadString(prefab, "category", "Category");
                string category = ClassifyPrefab(path, categoryText);
                Vector3 position;
                bool hasPosition = TryReadVector(GetMember(prefab, "position", "Position"), out position);

                // Track every serialized prefab that mentions powerline. The old
                // diagnostic logged the first 400 items, which were almost entirely
                // powerline-small poles and hid the large A/B/C/D towers later in
                // the prefab list.
                string normalizedPathForDiag = Normalize(path);
                if (normalizedPathForDiag.IndexOf("powerline", StringComparison.Ordinal) >= 0)
                {
                    powerlineCandidates++;

                    int diagSlash = normalizedPathForDiag.LastIndexOf('/');
                    string diagFile = diagSlash >= 0 ? normalizedPathForDiag.Substring(diagSlash + 1) : normalizedPathForDiag;
                    int basenameCount;
                    if (!powerlineBasenameCounts.TryGetValue(diagFile, out basenameCount)) basenameCount = 0;
                    powerlineBasenameCounts[diagFile] = basenameCount + 1;

                    bool targetLarge = IsLargePowerlineIdentity(path, categoryText);
                    string variant = LargePowerlineVariant(path, categoryText);

                    // Always prioritize exact large-tower diagnostics. Keep only a
                    // small sample of non-target powerline entries to avoid huge JSON.
                    if ((targetLarge && largePowerlineCandidateLogged < 300)
                        || (!targetLarge && powerlineCandidateLogged < 30))
                    {
                        diagnostics.Add("Powerline prefab candidate: id=" + id.ToString(CultureInfo.InvariantCulture)
                            + ", path=" + path
                            + ", category=" + (categoryText ?? String.Empty)
                            + ", targetLargePowerline=" + targetLarge.ToString()
                            + ", variant=" + (String.IsNullOrEmpty(variant) ? "-" : variant)
                            + (hasPosition ? ", position=" + position.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
                                + position.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
                                + position.z.ToString("0.0", CultureInfo.InvariantCulture) : ", position=unavailable"));

                        if (targetLarge) largePowerlineCandidateLogged++;
                        else powerlineCandidateLogged++;
                    }
                }

                bool genericRockCandidate = GenericRockFormationCandidate(path, categoryText);
                if (category == "god_rocks")
                {
                    if (hasPosition && StrongGodRockIdentity(path, categoryText))
                    {
                        string godSource = IsGodRockRootAIdentity(path)
                            ? "saved_map_godrock_root_a_exact"
                            : "saved_map_godrock_explicit_identity";
                        AddEntry(entries, seen, "god_rocks", path, godSource, position);
                    }
                    continue;
                }
                if (String.IsNullOrEmpty(category) && genericRockCandidate)
                {
                    // Keep huge roots as diagnostics only. This lets us discover a
                    // renamed Facepunch prefab after an update without drawing a
                    // false God Rock icon on the player's map.
                    if (collectGodRockFallback && hasPosition && godRockCandidates.Count < 2500)
                    {
                        godRockCandidates.Add(new RavenGodRockCandidate {
                            Path = path, Category = categoryText, Position = position,
                            StrongIdentity = StrongGodRockIdentity(path, categoryText),
                            HugeIdentity = HugeRockFormationIdentity(path, categoryText)
                        });
                    }
                    continue;
                }

                if (String.IsNullOrEmpty(category))
                {
                    string lower = Normalize(path);
                    if (lower.IndexOf("ice") >= 0 || lower.IndexOf("formation") >= 0 || lower.IndexOf("powerline") >= 0)
                    {
                        if (candidates < 25) diagnostics.Add("Candidate prefab: " + path);
                        candidates++;
                    }
                    continue;
                }
                if (!hasPosition) continue;
                bool authoritativeRoot = IsAuthoritativeRootMonumentCategory(category);
                string sourceName = category == "powerlines" ? "saved_map_powerline_abcd_exact"
                    : authoritativeRoot ? "saved_map_monument_prefab" : "saved_map_prefab";
                int beforeAdd = entries.Count;
                AddEntry(entries, seen, category, path, sourceName, position);
                if (authoritativeRoot && entries.Count > beforeAdd) rootMonuments++;
            }
            if (collectGodRockFallback)
            {
                diagnostics.Add("Saved-map generic God Rock clustering disabled: candidates are diagnostic-only until exact signature is known.");
                int sampleCount = Math.Min(godRockCandidates.Count, 1200);
                for (int i = 0; i < sampleCount; i++)
                {
                    RavenGodRockCandidate c = godRockCandidates[i];
                    diagnostics.Add("Rock formation candidate (not rendered): path=" + (c.Path ?? String.Empty)
                        + ", category=" + (c.Category ?? String.Empty)
                        + ", huge=" + c.HugeIdentity.ToString()
                        + ", position=" + c.Position.x.ToString("0.0", CultureInfo.InvariantCulture) + ","
                        + c.Position.y.ToString("0.0", CultureInfo.InvariantCulture) + ","
                        + c.Position.z.ToString("0.0", CultureInfo.InvariantCulture));
                }
                if (godRockCandidates.Count > sampleCount)
                    diagnostics.Add("Additional huge rock candidates omitted: " + (godRockCandidates.Count - sampleCount).ToString(CultureInfo.InvariantCulture));
            }
            else diagnostics.Add("Saved-map God Rock candidate diagnostics disabled by caller");
            diagnostics.Add("Saved-map root monuments accepted: " + rootMonuments.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Saved-map prefabs scanned: " + scanned.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Saved-map unresolved prefab IDs: " + unresolved.ToString(CultureInfo.InvariantCulture));
            if (candidates > 25) diagnostics.Add("Additional candidate prefabs omitted: " + (candidates - 25).ToString(CultureInfo.InvariantCulture));
            StringBuilder powerlineBreakdown = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in powerlineBasenameCounts)
            {
                if (powerlineBreakdown.Length > 0) powerlineBreakdown.Append("; ");
                powerlineBreakdown.Append(kv.Key).Append("=").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            diagnostics.Add("Serialized powerline-prefab candidates: total=" + powerlineCandidates.ToString(CultureInfo.InvariantCulture)
                + ", nonTargetSamplesLogged=" + powerlineCandidateLogged.ToString(CultureInfo.InvariantCulture)
                + ", largeTargetsLogged=" + largePowerlineCandidateLogged.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Serialized powerline basename counts: " + powerlineBreakdown.ToString());
            return true;
        }

        private static string DescribePath(object path)
        {
            if (path == null) return String.Empty;
            StringBuilder text = new StringBuilder(path.GetType().FullName ?? path.GetType().Name);
            string[] names = { "name", "Name", "type", "Type", "category", "Category", "id", "ID" };
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMember(path, names[i]);
                if (value == null) continue;
                text.Append("|").Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static bool IsFiniteVector(Vector3 value)
        {
            return !Single.IsNaN(value.x) && !Single.IsNaN(value.y) && !Single.IsNaN(value.z)
                && !Single.IsInfinity(value.x) && !Single.IsInfinity(value.y) && !Single.IsInfinity(value.z);
        }

        private static void AddDistinctVector(List<Vector3> vectors, HashSet<string> keys, Vector3 value)
        {
            if (!IsFiniteVector(value)) return;
            // Millimetre-level precision is unnecessary here. Quantising only
            // removes duplicate reflection hits while preserving actual pole/node
            // locations for the map renderer.
            string key = Math.Round(value.x, 2).ToString(CultureInfo.InvariantCulture) + ":"
                + Math.Round(value.y, 2).ToString(CultureInfo.InvariantCulture) + ":"
                + Math.Round(value.z, 2).ToString(CultureInfo.InvariantCulture);
            if (keys.Add(key)) vectors.Add(value);
        }

        private static List<Vector3> ExtractPathVectors(object path)
        {
            List<Vector3> vectors = new List<Vector3>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            if (path == null) return vectors;

            object current = GetMember(path, "Path", "path", "Spline", "spline");
            if (current == null) current = path;
            object points = GetMember(current, "Points", "points", "Nodes", "nodes", "Positions", "positions", "Waypoints", "waypoints");
            if (points == null)
                points = GetMember(path, "Points", "points", "Nodes", "nodes", "Positions", "positions", "Waypoints", "waypoints");

            // First pass: read one world position per node. This deliberately
            // avoids recursively collecting tangent/control vectors as if they
            // were real power poles.
            IEnumerable enumerable = AsEnumerable(points);
            if (enumerable != null)
            {
                int inspected = 0;
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    Vector3 position;
                    bool found = TryReadVector(item, out position);
                    if (!found)
                    {
                        object member = GetMember(item, "Position", "position", "WorldPosition", "worldPosition", "Point", "point");
                        found = TryReadVector(member, out position);
                    }
                    if (!found)
                    {
                        object transform = GetMember(item, "Transform", "transform");
                        object member = transform == null ? null : GetMember(transform, "Position", "position");
                        found = TryReadVector(member, out position);
                    }
                    if (found) AddDistinctVector(vectors, keys, position);
                    if (++inspected >= 8192) break;
                }
            }

            // Compatibility fallback for Rust builds where path nodes are nested
            // inside another wrapper and no direct Position member is exposed.
            if (vectors.Count == 0)
            {
                List<Vector3> fallback = new List<Vector3>();
                if (points != null) CollectVectors(points, fallback, 0);
                if (fallback.Count == 0) CollectVectors(current, fallback, 0);
                for (int i = 0; i < fallback.Count; i++) AddDistinctVector(vectors, keys, fallback[i]);
            }
            return vectors;
        }

        private static Type FindLoadedTypeByShortName(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Type direct = AccessTools.TypeByName(names[i]);
                if (direct != null) return direct;
            }
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < assemblies.Length; a++)
                {
                    Type[] types;
                    try { types = assemblies[a].GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    catch { continue; }
                    if (types == null) continue;
                    for (int t = 0; t < types.Length; t++)
                    {
                        Type type = types[t];
                        if (type == null) continue;
                        for (int n = 0; n < names.Length; n++)
                        {
                            if (String.Equals(type.Name, names[n], StringComparison.OrdinalIgnoreCase)
                                || String.Equals(type.FullName, names[n], StringComparison.OrdinalIgnoreCase))
                                return type;
                        }
                    }
                }
            }
            catch { }
            return null;
        }


        private static int CollectRuntimePowerlines(List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics)
        {
            // Emergency fallback only. Normal procedural maps should use the
            // authoritative serialized .map prefab roots collected earlier.
            //
            // Runtime scenes contain many unrelated objects named powerline_pole_*
            // inside monuments (Outpost, Trainyard, Stables, Harbor, etc.), zipline
            // platforms and deep-sea/tropical prefabs. Those are NOT world powerline
            // towers and must never become map markers.
            int scanned = 0, accepted = 0, invalid = 0, rejected = 0;
            HashSet<int> roots = new HashSet<int>();
            try
            {
                UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(typeof(Transform));
                if (objects == null) return 0;

                Stopwatch timer = Stopwatch.StartNew();
                for (int i = 0; i < objects.Length; i++)
                {
                    if (timer.Elapsed.TotalSeconds > 8)
                    {
                        diagnostics.Add("Strict runtime Powerline fallback stopped at 8 second budget");
                        break;
                    }

                    Transform t = objects[i] as Transform;
                    if (t == null || !IsValidRuntimeSceneComponent(t)) continue;
                    scanned++;

                    Transform root = null;
                    string variant = String.Empty;
                    Transform current = t;
                    for (int depth = 0; current != null && depth < 14; depth++, current = current.parent)
                    {
                        string identity = RuntimeTransformIdentity(current);
                        string n = Normalize(identity);

                        // Reject known false-positive families immediately.
                        if (n.IndexOf("/monument/", StringComparison.Ordinal) >= 0
                            || n.IndexOf("powerlineplatform", StringComparison.Ordinal) >= 0
                            || n.IndexOf("/powerline-small/", StringComparison.Ordinal) >= 0
                            || n.IndexOf("deepsea_", StringComparison.Ordinal) >= 0
                            || n.IndexOf("tropical_island", StringComparison.Ordinal) >= 0)
                            continue;

                        // A runtime fallback root is trusted only if its hierarchy
                        // resolves to the same exact prefab family that is stored in
                        // the .map: /decor/powerline/powerline_[a-d].prefab.
                        string identityVariant = LargePowerlineVariant(identity, String.Empty);
                        if (!String.IsNullOrEmpty(identityVariant)
                            && n.IndexOf("/decor/powerline/powerline_", StringComparison.Ordinal) >= 0
                            && n.IndexOf(".prefab", StringComparison.Ordinal) >= 0)
                        {
                            root = current;
                            variant = identityVariant;
                            break;
                        }
                    }

                    if (root == null) { rejected++; continue; }

                    int instanceId;
                    try { instanceId = root.GetInstanceID(); }
                    catch { instanceId = root.GetHashCode(); }
                    if (!roots.Add(instanceId)) continue;

                    Vector3 pos = root.position;
                    if (!IsFiniteVector(pos)) { invalid++; continue; }

                    int before = entries.Count;
                    AddEntry(entries, seen, "powerlines",
                        "raven://runtime-strict/powerline-" + variant.ToLowerInvariant() + "/" + RuntimeTransformIdentity(root),
                        "runtime_powerline_abcd_strict_fallback", pos);
                    if (entries.Count > before) accepted++;
                }

                diagnostics.Add("Strict runtime Powerline fallback: transformsScanned="
                    + scanned.ToString(CultureInfo.InvariantCulture)
                    + ", accepted=" + accepted.ToString(CultureInfo.InvariantCulture)
                    + ", rejected=" + rejected.ToString(CultureInfo.InvariantCulture)
                    + ", invalidPositions=" + invalid.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                diagnostics.Add("Strict runtime Powerline fallback exception: " + ex.GetType().Name + ": " + ex.Message);
            }
            return accepted;
        }


        private static bool CollectPowerlines(object savedWorld, List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics, out int exactCount)
        {
            // "powerlines" means ONLY the large world Powerline A/B/C/D roots that
            // are serialized directly in the generated .map:
            //   assets/bundled/prefabs/autospawn/decor/powerline/powerline_[a-d].prefab
            //
            // Runtime scene scanning is intentionally NOT merged into a valid saved
            // map result. Runtime descendants contain decorative powerline_pole_*
            // objects inside monuments and zipline platforms, which caused the
            // duplicate/nonsense markers seen in v3.4.4.
            int savedExact = 0;
            Dictionary<string, int> savedVariants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < entries.Count; i++)
            {
                RavenWorldEntry e = entries[i];
                if (!String.Equals(e.Category, "powerlines", StringComparison.OrdinalIgnoreCase)) continue;
                if (!String.Equals(e.Source, "saved_map_powerline_abcd_exact", StringComparison.OrdinalIgnoreCase)) continue;

                savedExact++;
                string variant = LargePowerlineVariant(e.RawName, String.Empty);
                if (String.IsNullOrEmpty(variant)) variant = "?";

                int count;
                if (!savedVariants.TryGetValue(variant, out count)) count = 0;
                savedVariants[variant] = count + 1;
            }

            int runtimeAdded = 0;
            if (savedWorld != null)
            {
                // The .map was loaded successfully. Its exact prefab roots are the
                // authoritative source, including a legitimate result of zero.
                diagnostics.Add("Runtime Powerline scan skipped: serialized .map is authoritative; monument/zipline child objects are excluded.");
            }
            else
            {
                // Only if the serialized map could not be loaded at all do we use a
                // heavily restricted runtime fallback.
                runtimeAdded = CollectRuntimePowerlines(entries, seen, diagnostics);
            }

            exactCount = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!String.Equals(entries[i].Category, "powerlines", StringComparison.OrdinalIgnoreCase)) continue;
                if (String.Equals(entries[i].Source, "saved_map_powerline_abcd_exact", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(entries[i].Source, "runtime_powerline_abcd_strict_fallback", StringComparison.OrdinalIgnoreCase))
                    exactCount++;
            }

            StringBuilder savedSummary = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in savedVariants)
            {
                if (savedSummary.Length > 0) savedSummary.Append(", ");
                savedSummary.Append(kv.Key).Append("=").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            diagnostics.Add("Powerline marker semantics: SERIALIZED LARGE POWERLINE A/B/C/D ROOTS ONLY."
                + " savedMapLargePowerlines=" + savedExact.ToString(CultureInfo.InvariantCulture)
                + ", savedVariants=[" + savedSummary.ToString() + "]"
                + ", strictRuntimeFallbackAdded=" + runtimeAdded.ToString(CultureInfo.InvariantCulture)
                + ", finalLargePowerlines=" + exactCount.ToString(CultureInfo.InvariantCulture));

            return true;
        }

        private static int ResolveTopologyBit(string wanted)
        {
            Type enumType = AccessTools.TypeByName("TerrainTopology+Enum");
            if (enumType == null) enumType = AccessTools.TypeByName("TerrainTopology.Enum");
            if (enumType == null || !enumType.IsEnum) return 0;
            string[] names = Enum.GetNames(enumType);
            for (int i = 0; i < names.Length; i++)
            {
                if (!String.Equals(names[i], wanted, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Convert.ToInt32(Enum.Parse(enumType, names[i]), CultureInfo.InvariantCulture); }
                catch { return 0; }
            }
            return 0;
        }

        private sealed class TopologyInvoker
        {
            public MethodInfo Method;
            public int Mode;
            public string Signature;
        }

        private static bool IsNumber(Type type)
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(uint);
        }

        private static TopologyInvoker FindGetTopology(Type type)
        {
            if (type == null) return null;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            TopologyInvoker fallback = null;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!String.Equals(method.Name, "GetTopology", StringComparison.OrdinalIgnoreCase) || method.ReturnType != typeof(int)) continue;
                ParameterInfo[] pars = method.GetParameters();
                if (pars.Length == 3 && IsNumber(pars[0].ParameterType) && IsNumber(pars[1].ParameterType) && IsNumber(pars[2].ParameterType))
                    return new TopologyInvoker { Method = method, Mode = 3, Signature = method.ToString() };
                if (pars.Length == 1 && pars[0].ParameterType == typeof(Vector3))
                    fallback = new TopologyInvoker { Method = method, Mode = 1, Signature = method.ToString() };
                else if (fallback == null && pars.Length == 2 && pars[0].ParameterType == typeof(Vector3) && IsNumber(pars[1].ParameterType))
                    fallback = new TopologyInvoker { Method = method, Mode = 2, Signature = method.ToString() };
            }
            return fallback;
        }

        private static bool TryInvokeTopology(object map, TopologyInvoker invoker, float nx, float nz, uint worldSize, out int value)
        {
            value = 0;
            try
            {
                ParameterInfo[] pars = invoker.Method.GetParameters();
                object result;
                if (invoker.Mode == 3)
                {
                    result = invoker.Method.Invoke(map, new object[] {
                        Convert.ChangeType(nx, pars[0].ParameterType, CultureInfo.InvariantCulture),
                        Convert.ChangeType(nz, pars[1].ParameterType, CultureInfo.InvariantCulture),
                        Convert.ChangeType(16f, pars[2].ParameterType, CultureInfo.InvariantCulture)
                    });
                }
                else
                {
                    Vector3 world = new Vector3((nx - 0.5f) * worldSize, 0f, (nz - 0.5f) * worldSize);
                    if (invoker.Mode == 1) result = invoker.Method.Invoke(map, new object[] { world });
                    else result = invoker.Method.Invoke(map, new object[] { world, Convert.ChangeType(16f, pars[1].ParameterType, CultureInfo.InvariantCulture) });
                }
                value = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static List<RavenComponent> BuildComponents(bool[] mask, int grid, int minimumCells)
        {
            bool[] visited = new bool[mask.Length];
            List<RavenComponent> result = new List<RavenComponent>();
            int[] queue = new int[mask.Length];
            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start]) continue;
                int head = 0, tail = 0;
                queue[tail++] = start; visited[start] = true;
                RavenComponent component = new RavenComponent();
                while (head < tail)
                {
                    int idx = queue[head++];
                    int x = idx % grid, z = idx / grid;
                    component.Cells++; component.SumX += x + 0.5; component.SumZ += z + 0.5;
                    for (int d = 0; d < 4; d++)
                    {
                        int xx = x + dx[d], zz = z + dz[d];
                        if (xx < 0 || zz < 0 || xx >= grid || zz >= grid) continue;
                        int next = zz * grid + xx;
                        if (!mask[next] || visited[next]) continue;
                        visited[next] = true; queue[tail++] = next;
                    }
                }
                if (component.Cells >= minimumCells) result.Add(component);
            }
            result.Sort(delegate(RavenComponent a, RavenComponent b) { return b.Cells.CompareTo(a.Cells); });
            return result;
        }

        private static bool CollectTopology(List<RavenWorldEntry> entries, HashSet<string> seen, List<string> diagnostics, uint worldSize, Dictionary<string, int> counts)
        {
            diagnostics.Add("Terrain topology icon scan disabled: the base map already renders mountain and ice-lake terrain.");
            return true;
        }

        private static string Escape(string value)
        {
            return value == null ? String.Empty : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string CoverageFor(string category, bool prefabs, bool powerlines, bool topology, bool runtimeGodRockEvidence)
        {
            if (category == "powerlines") return powerlines ? "exact_saved_map_large_powerline_abcd_roots" : "unavailable";
            // Generic saved-map rock_formation parçaları God Rock'ın yokluğunu
            // kanıtlamaz. God Rock yalnız açık MonumentInfo/isim eşleşmesiyle
            // pozitif doğrulanır; sıfır sonuç "yok" değil "bilinmiyor" olmalıdır.
            if (category == "god_rocks") return prefabs ? "exact_saved_map_rock_formation_a_root" : (runtimeGodRockEvidence ? "explicit_named_identity" : "unavailable");
            if (category == "lakes") return prefabs ? "exact_saved_map_prefabs" : "unavailable";
            if (category == "swamps") return prefabs ? "exact_saved_map_prefabs" : "unavailable";
            return prefabs ? "exact_saved_map_prefabs" : "unavailable";
        }

        private static List<string> ReportDirectories(string identity)
        {
            List<string> result = new List<string>();
            string current = Environment.CurrentDirectory;
            result.Add(Path.GetFullPath(Path.Combine(current, "HarmonyConfig", "RavenMapReports")));
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string root = Path.GetFullPath(Path.Combine(baseDir, ".."));
                result.Add(Path.Combine(root, "HarmonyConfig", "RavenMapReports"));
            }
            catch { }
            if (!String.IsNullOrWhiteSpace(identity))
                result.Add(Path.GetFullPath(Path.Combine(current, "server", identity, "HarmonyConfig", "RavenMapReports")));

            List<string> unique = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Count; i++) if (seen.Add(result[i])) unique.Add(result[i]);
            return unique;
        }

        private static void WriteReportFile(string phase, bool complete, string stage, uint worldSize, uint seed, string identity,
            string mapPath, List<RavenWorldEntry> entries, List<string> diagnostics, bool prefabsAvailable,
            bool runtimeMonumentsAvailable, bool powerlinesAvailable, bool topologyAvailable, int powerlineCount, Dictionary<string, int> topologyCounts)
        {
            bool runtimeGodRockEvidence = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!String.Equals(entries[i].Category, "god_rocks", StringComparison.OrdinalIgnoreCase)) continue;
                if (String.Equals(entries[i].Source, "terrainmeta_path_monuments", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(entries[i].Source, "runtime_scene_named_root", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(entries[i].Source, "saved_map_godrock_exact", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeGodRockEvidence = true;
                    break;
                }
            }
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Categories.Length; i++) counts[Categories[i]] = 0;
            for (int i = 0; i < entries.Count; i++) counts[entries[i].Category] = counts[entries[i].Category] + 1;
            if (powerlinesAvailable) counts["powerlines"] = powerlineCount;

            StringBuilder json = new StringBuilder();
            json.Append("{\n  \"format\": \"raven-world-data-report-v5.1\",\n");
            json.Append("  \"complete\": ").Append(complete ? "true" : "false").Append(",\n");
            json.Append("  \"stage\": \"").Append(Escape(stage)).Append("\",\n");
            json.Append("  \"phase\": \"").Append(Escape(phase)).Append("\",\n");
            json.Append("  \"generatedAtUtc\": \"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            json.Append("  \"worldSize\": ").Append(worldSize.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            json.Append("  \"seed\": ").Append(seed.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            json.Append("  \"identity\": \"").Append(Escape(identity)).Append("\",\n");
            json.Append("  \"mapPath\": \"").Append(Escape(mapPath)).Append("\",\n");
            json.Append("  \"entryCount\": ").Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            json.Append("  \"sourceStatus\": {\"savedMapPrefabs\": ").Append(prefabsAvailable ? "true" : "false");
            json.Append(", \"runtimeMonuments\": ").Append(runtimeMonumentsAvailable ? "true" : "false");
            json.Append(", \"runtimeGodRockEvidence\": ").Append(runtimeGodRockEvidence ? "true" : "false");
            json.Append(", \"powerlinePaths\": ").Append(powerlinesAvailable ? "true" : "false");
            json.Append(", \"powerlineMarkers\": ").Append(powerlineCount.ToString(CultureInfo.InvariantCulture));
            json.Append(", \"terrainTopology\": ").Append(topologyAvailable ? "true" : "false").Append("},\n");
            json.Append("  \"coverage\": {");
            for (int i = 0; i < Categories.Length; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append("\"").Append(Categories[i]).Append("\": \"").Append(CoverageFor(Categories[i], prefabsAvailable, powerlinesAvailable, topologyAvailable, runtimeGodRockEvidence)).Append("\"");
            }
            json.Append("},\n  \"counts\": {");
            for (int i = 0; i < Categories.Length; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append("\"").Append(Categories[i]).Append("\": ").Append(counts[Categories[i]].ToString(CultureInfo.InvariantCulture));
            }
            json.Append("},\n  \"entries\": [\n");
            for (int i = 0; i < entries.Count; i++)
            {
                RavenWorldEntry entry = entries[i];
                json.Append("    {\"category\": \"").Append(Escape(entry.Category)).Append("\", \"displayName\": \"").Append(Escape(entry.DisplayName)).Append("\", ");
                json.Append("\"rawName\": \"").Append(Escape(entry.RawName)).Append("\", \"source\": \"").Append(Escape(entry.Source)).Append("\", ");
                json.Append("\"x\": ").Append(entry.X.ToString("R", CultureInfo.InvariantCulture)).Append(", \"y\": ").Append(entry.Y.ToString("R", CultureInfo.InvariantCulture)).Append(", \"z\": ").Append(entry.Z.ToString("R", CultureInfo.InvariantCulture)).Append("}");
                if (i + 1 < entries.Count) json.Append(',');
                json.Append('\n');
            }
            json.Append("  ],\n  \"diagnostics\": [");
            for (int i = 0; i < diagnostics.Count; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append("\"").Append(Escape(diagnostics[i])).Append("\"");
            }
            json.Append("]\n}");

            List<string> directories = ReportDirectories(identity);
            for (int d = 0; d < directories.Count; d++)
            {
                try
                {
                    Directory.CreateDirectory(directories[d]);
                    string exact = Path.Combine(directories[d], String.Format(CultureInfo.InvariantCulture, "RavenWorldObjectReport_{0}_{1}.json", worldSize, seed));
                    string latest = Path.Combine(directories[d], "RavenWorldObjectReport_latest.json");
                    string temp = exact + ".tmp";
                    File.WriteAllText(temp, json.ToString(), new UTF8Encoding(false));
                    if (File.Exists(exact)) File.Delete(exact);
                    File.Move(temp, exact);
                    File.Copy(exact, latest, true);
                    UDebug.Log("[RavenWorldDataReporterV43] Report file: " + exact);
                }
                catch (Exception ex) { UDebug.LogError("[RavenWorldDataReporterV43] Report write failed: " + directories[d] + " | " + ex.Message); }
            }
        }

        public static void WriteReport(string phase, bool force)
        {
            lock (Sync)
            {
                uint worldSize, seed; string identity;
                ReadMapIdentity(out worldSize, out seed, out identity);
                string key = worldSize.ToString(CultureInfo.InvariantCulture) + ":" + seed.ToString(CultureInfo.InvariantCulture);
                if (!force && FinalWritten.Contains(key)) return;
                if (worldSize == 0U || seed == 0U)
                {
                    UDebug.LogError("[RavenWorldDataReporterV43] Map identity unresolved. size=" + worldSize + ", seed=" + seed + ", phase=" + phase);
                    return;
                }

                Stopwatch total = Stopwatch.StartNew();
                List<RavenWorldEntry> entries = new List<RavenWorldEntry>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> diagnostics = new List<string>();
                Dictionary<string, int> topologyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string mapPath = ResolveMapPath(worldSize, seed);
                diagnostics.Add("Reporter version: 5.1");
                diagnostics.Add("Phase: " + phase);
                diagnostics.Add("Identity: size=" + worldSize + ", seed=" + seed + ", server.identity=" + identity);
                diagnostics.Add("Resolved map path: " + (mapPath ?? "<none>"));

                // Başlangıç dosyası hemen yazılır. Böylece bundan sonraki aşamada
                // hata olsa bile panel raporlayıcının tetiklendiğini görebilir.
                WriteReportFile(phase, false, "started", worldSize, seed, identity, mapPath, entries, diagnostics,
                    false, false, false, false, 0, topologyCounts);

                object savedWorld = LoadSavedWorld(mapPath, diagnostics);
                if (savedWorld == null)
                {
                    savedWorld = GetInMemoryWorldData();
                    if (savedWorld != null) diagnostics.Add("Falling back to in-memory World.Serialization.world");
                }

                UDebug.Log("[RavenWorldDataReporterV43] Stage 1/4 runtime MonumentInfo/named-root scan starting.");
                Stopwatch stage = Stopwatch.StartNew();
                bool runtimeMonumentsAvailable = false;
                try { runtimeMonumentsAvailable = CollectRuntimeMonuments(entries, seen, diagnostics); }
                catch (Exception ex) { diagnostics.Add("Runtime MonumentInfo exception: " + ex.GetType().Name + ": " + ex.Message); }
                // v5.0: do NOT count rock_formation_huge_c. Runtime reports proved
                // it is a reusable child inside rock_formation_b/d/e/f/g and other
                // scenery. Only explicit God Rock identities are accepted here.
                diagnostics.Add("God Rock huge_c promotion disabled: huge_c is a reusable child, not a God Rock instance.");
                bool runtimeGodRockSceneScanAvailable = CollectRuntimeNamedGodRockRoots(entries, seen, diagnostics);
                diagnostics.Add("Runtime explicit God Rock scene scan available: " + runtimeGodRockSceneScanAvailable.ToString());
                diagnostics.Add("Runtime monument stage ms: " + stage.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                UDebug.Log("[RavenWorldDataReporterV43] Stage 1/4 complete. available=" + runtimeMonumentsAvailable + ", entries=" + entries.Count + ", ms=" + stage.ElapsedMilliseconds);

                UDebug.Log("[RavenWorldDataReporterV43] Stage 2/4 saved-map prefab scan starting.");
                stage.Restart();
                bool prefabsAvailable = CollectWorldPrefabs(savedWorld, entries, seen, diagnostics, true);
                diagnostics.Add("Prefab stage ms: " + stage.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                UDebug.Log("[RavenWorldDataReporterV43] Stage 2/4 complete. entries=" + entries.Count + ", ms=" + stage.ElapsedMilliseconds);

                UDebug.Log("[RavenWorldDataReporterV43] Stage 3/4 powerline scan starting.");
                stage.Restart();
                int powerlineCount;
                bool powerlinesAvailable = CollectPowerlines(savedWorld, entries, seen, diagnostics, out powerlineCount);
                diagnostics.Add("Powerline stage ms: " + stage.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                UDebug.Log("[RavenWorldDataReporterV43] Stage 3/4 complete. count=" + powerlineCount + ", ms=" + stage.ElapsedMilliseconds);

                WriteReportFile(phase, false, "prefabs_powerlines_complete", worldSize, seed, identity, mapPath, entries, diagnostics,
                    prefabsAvailable, runtimeMonumentsAvailable, powerlinesAvailable, false, powerlineCount, topologyCounts);

                UDebug.Log("[RavenWorldDataReporterV43] Stage 4/4 topology scan starting.");
                stage.Restart();
                bool topologyAvailable = false;
                try { topologyAvailable = CollectTopology(entries, seen, diagnostics, worldSize, topologyCounts); }
                catch (Exception ex) { diagnostics.Add("Topology exception: " + ex.GetType().Name + ": " + ex.Message); }
                diagnostics.Add("Topology stage ms: " + stage.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                diagnostics.Add("Total reporter ms: " + total.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));

                bool finalComplete = runtimeMonumentsAvailable || String.Equals(phase, "after_map_png", StringComparison.OrdinalIgnoreCase);
                string finalStage = finalComplete ? "complete" : "runtime_monuments_pending";
                WriteReportFile(phase, finalComplete, finalStage, worldSize, seed, identity, mapPath, entries, diagnostics,
                    prefabsAvailable, runtimeMonumentsAvailable, powerlinesAvailable, topologyAvailable, powerlineCount, topologyCounts);
                if (finalComplete) FinalWritten.Add(key);
                UDebug.Log("[RavenWorldDataReporterV43] Report written. complete=" + finalComplete + ", entries=" + entries.Count
                    + ", runtimeMonuments=" + runtimeMonumentsAvailable + ", prefabs=" + prefabsAvailable
                    + ", powerlines=" + powerlinesAvailable + ", topology=" + topologyAvailable + ", ms=" + total.ElapsedMilliseconds);
            }
        }
    }
}
