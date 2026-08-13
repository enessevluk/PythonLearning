using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using ProtoBuf;
using UnityEngine;

namespace Raven.MapGenerator
{
    internal sealed class GuaranteedRule
    {
        public string id;
        public string name;
        public string state;
        public int? min;
        public int? max;
        public string[] prefabMarkers;
        public string spawnPrefab;
        public string placement;
        public bool generatorOnly;
        public float minDistance = 260f;
        public float flatRadius = 45f;
        public float maxHeightDelta = 8f;
    }

    internal sealed class GuaranteedConfig
    {
        public bool enabled;
        public bool singleSeed = true;
        public int maxSeedAttempts = 1;
        public bool alwaysDeliver = true;
        public string jobId;
        public uint seed;
        public uint worldSize;
        public string auditPath;
        public string mapPath;
        public List<GuaranteedRule> rules = new List<GuaranteedRule>();
    }

    internal sealed class GuaranteedPlacementEntry
    {
        public string id;
        public string prefabPath;
        public float x;
        public float y;
        public float z;
        public float rotationY;
    }

    internal sealed class GuaranteedAudit
    {
        public string format = "raven-guaranteed-placement-audit-v1";
        public int version = 1;
        public string jobId;
        public uint seed;
        public uint worldSize;
        public string mapPath;
        public string backupPath;
        public bool mapSaved;
        public bool ok;
        public string deliveryMode = "single_seed_always_deliver";
        public int seedAttempts = 1;
        public bool saveContinuesOnRuleFailure = true;
        public bool beforeSaveApplied;
        public bool partial;
        public Dictionary<string, int> initialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> finalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<GuaranteedPlacementEntry> placements = new List<GuaranteedPlacementEntry>();
        public List<GuaranteedPlacementEntry> removals = new List<GuaranteedPlacementEntry>();
        public List<string> errors = new List<string>();
        public List<string> warnings = new List<string>();
    }

    internal sealed class RequiredRuleValidationItem
    {
        public string id;
        public string name;
        public string state;
        public int targetMin;
        public int targetMax;
        public int beforeCount;
        public int afterCount;
        public bool injectionAllowed;
        public string status;
    }

    internal sealed class RequiredMonumentsValidationReport
    {
        public string format = "raven-required-monuments-validation-v1";
        public string generatedAtUtc;
        public uint seed;
        public uint worldSize;
        public bool singleSeed = true;
        public int seedAttempts = 1;
        public bool retryAnotherSeed = false;
        public bool alwaysDeliverMap = true;
        public bool allRulesMet;
        public string deliveryStatus = "current_seed_will_be_delivered_even_if_rules_are_missing";
        public List<RequiredRuleValidationItem> rules = new List<RequiredRuleValidationItem>();
        public List<string> notes = new List<string>();
    }

    internal sealed class GodRockExactCountState
    {
        public string format = "raven-godrock-exact-count-controller-v1";
        public string generatedAtUtc;
        public uint seed;
        public uint worldSize;
        public string mapPath;
        public string ruleState;
        public int targetCount;
        public int beforeCount;
        public int afterCount;
        public int convertedToGodRock;
        public int convertedFromGodRock;
        public int availableLargeRockSlots;
        public bool appliedBeforeSave;
        public bool ok;
        public List<string> notes = new List<string>();
    }

    public sealed class HarmonyModHooks : IHarmonyModHooks
    {
        void IHarmonyModHooks.OnLoaded(OnHarmonyModLoadedArgs args)
        {
            Debug.Log("[Raven Guaranteed Placement] Loaded raven-guaranteed-placement-v2.3.0-godrock-only-natural-monuments");
        }

        void IHarmonyModHooks.OnUnloaded(OnHarmonyModUnloadedArgs args)
        {
            Debug.Log("[Raven Guaranteed Placement] Unloaded");
        }
    }

    internal sealed class GodRockGeneratorComponentProbe
    {
        public int index;
        public string type;
        public string description;
        public string gameObject;
        public string hierarchy;
        public bool strongGodIdentity;
        public bool countMemberAvailable;
        public Dictionary<string, string> members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> methods = new List<string>();
    }

    internal sealed class GodRockGeneratorProbe
    {
        public string format = "raven-godrock-generator-probe-v1";
        public string generatedAtUtc;
        public uint seed;
        public uint worldSize;
        public string ruleState;
        public int targetCount = -1;
        public int proceduralComponentCount;
        public int interestingComponentCount;
        public int strongCandidateCount;
        public bool controllerApplied;
        public List<string> controllerNotes = new List<string>();
        public List<GodRockGeneratorComponentProbe> components = new List<GodRockGeneratorComponentProbe>();
    }

    [HarmonyPatch]
    [HarmonyBefore("com.facepunch.rust_dedicated.CustomGenerator")]
    [HarmonyPriority(Priority.First)]
    public static class RavenGodRockGeneratorProbePatch
    {
        private static bool _ran;

        public static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("WorldSetup");
            return type == null ? null : AccessTools.Method(type, "InitCoroutine");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(object __instance)
        {
            if (_ran) return;
            _ran = true;
            try { GodRockGeneratorController.ProbeAndApply(__instance); }
            catch (Exception ex) { Debug.LogError("[Raven GodRock Generator] Probe failed: " + ex); }
        }
    }

    internal static class GodRockGeneratorController
    {
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void ProbeAndApply(object worldSetup)
        {
            GuaranteedConfig config = LoadConfig();
            GuaranteedRule godRule = config == null || config.rules == null ? null : config.rules.FirstOrDefault(r => String.Equals(r.id, "god_rocks", StringComparison.OrdinalIgnoreCase));

            GodRockGeneratorProbe probe = new GodRockGeneratorProbe();
            probe.generatedAtUtc = DateTime.UtcNow.ToString("o");
            probe.seed = ReadWorldUInt("Seed");
            probe.worldSize = ReadWorldUInt("Size");
            if (godRule != null)
            {
                probe.ruleState = godRule.state ?? "optional";
                probe.targetCount = String.Equals(godRule.state, "blocked", StringComparison.OrdinalIgnoreCase)
                    ? 0 : Math.Max(1, godRule.min ?? 1);
            }
            else probe.ruleState = "not_configured";

            Component setupComponent = worldSetup as Component;
            if (setupComponent == null)
            {
                probe.controllerNotes.Add("WorldSetup is not a Unity Component; probe could not enumerate children.");
                WriteProbe(probe);
                return;
            }

            ProceduralComponent[] procedural = setupComponent.GetComponentsInChildren<ProceduralComponent>(true);
            if (procedural == null) procedural = new ProceduralComponent[0];
            probe.proceduralComponentCount = procedural.Length;

            List<Tuple<object, GodRockGeneratorComponentProbe, string>> strong = new List<Tuple<object, GodRockGeneratorComponentProbe, string>>();
            for (int i = 0; i < procedural.Length; i++)
            {
                object component = procedural[i];
                if (component == null) continue;
                GodRockGeneratorComponentProbe item = BuildProbe(component, i);
                string searchable = BuildSearchable(item);
                bool genericRock = ContainsAny(searchable, "rock", "formation", "cliff", "anvil", "arch");
                bool commonPlacementType = ContainsAny((item.type ?? String.Empty).ToLowerInvariant(), "placedecor", "placecliff", "placemonument", "placeprefab");
                item.strongGodIdentity = ContainsAny(searchable, "god rock", "godrock", "god_rock", "large god");
                item.countMemberAvailable = HasWritableIntMember(component, "TargetCount");

                if (genericRock || commonPlacementType || item.strongGodIdentity)
                {
                    probe.components.Add(item);
                    if (item.strongGodIdentity) strong.Add(Tuple.Create(component, item, searchable));
                }
            }

            probe.interestingComponentCount = probe.components.Count;
            probe.strongCandidateCount = strong.Count;

            if (godRule == null)
            {
                probe.controllerNotes.Add("God Rock rule is not configured; probe only.");
            }
            else if (!String.Equals(godRule.state, "required", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(godRule.state, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                probe.controllerNotes.Add("God Rock rule is optional; target count was not applied.");
            }
            else
            {
                // v3.5.0 no longer mutates ObjectDensity/TargetCount. The exact
                // requested count is applied after the procedural map is saved by
                // replacing already-valid v3_rock_formations_large root slots with
                // the rare rock_formation_a root. Keeping the WorldSetup probe read-
                // only avoids changing all large formations at once.
                probe.controllerApplied = false;
                probe.controllerNotes.Add("v3.5.0: WorldSetup mutation is intentionally skipped; exact count is applied in WorldSerialization.Save prefix.");
                probe.controllerNotes.Add("Exact God Rock count is handled by the before-save rock_formation_a root-slot controller. target=" + probe.targetCount);
            }

            WriteProbe(probe);
            Debug.Log("[Raven GodRock Generator] Probe complete. components=" + probe.proceduralComponentCount
                + ", interesting=" + probe.interestingComponentCount + ", strong=" + probe.strongCandidateCount
                + ", target=" + probe.targetCount + ", applied=" + probe.controllerApplied);
        }

        private static GuaranteedConfig LoadConfig()
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenGuaranteedPlacement.json"));
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<GuaranteedConfig>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static GodRockGeneratorComponentProbe BuildProbe(object component, int index)
        {
            GodRockGeneratorComponentProbe item = new GodRockGeneratorComponentProbe();
            item.index = index;
            Type type = component.GetType();
            item.type = type.FullName ?? type.Name;
            Component unityComponent = component as Component;
            item.gameObject = unityComponent == null || unityComponent.gameObject == null ? String.Empty : unityComponent.gameObject.name;
            item.hierarchy = unityComponent == null ? String.Empty : Hierarchy(unityComponent.transform);
            object description = GetMember(component, "Description");
            item.description = description == null ? String.Empty : Convert.ToString(description);

            FieldInfo[] fields = type.GetFields(InstanceFlags);
            for (int i = 0; i < fields.Length; i++)
            {
                try
                {
                    object value = fields[i].GetValue(component);
                    string text = ValueText(value, 0);
                    if (InterestingMember(fields[i].Name, text)) item.members["field:" + fields[i].Name] = text;
                }
                catch { }
            }
            PropertyInfo[] properties = type.GetProperties(InstanceFlags);
            for (int i = 0; i < properties.Length; i++)
            {
                if (properties[i].GetIndexParameters().Length != 0 || !properties[i].CanRead) continue;
                try
                {
                    object value = properties[i].GetValue(component, null);
                    string text = ValueText(value, 0);
                    if (InterestingMember(properties[i].Name, text)) item.members["prop:" + properties[i].Name] = text;
                }
                catch { }
            }

            // v3.4.8: the previous probe proved God Rock is not controlled by a
            // dedicated TargetCount component. Capture the callable surface of the
            // large-rock generator so the exact prefab-selection stage can be mapped
            // without guessing method names in a future controller patch.
            string resourceFolder = Convert.ToString(GetMember(component, "ResourceFolder")) ?? String.Empty;
            bool largeRockGenerator = resourceFolder.IndexOf("v3_rock_formations_large", StringComparison.OrdinalIgnoreCase) >= 0
                || String.Equals(item.description, "Rock Formations", StringComparison.OrdinalIgnoreCase)
                || String.Equals(item.description, "Rock Formations Arctic", StringComparison.OrdinalIgnoreCase);
            if (largeRockGenerator)
            {
                MethodInfo[] methods = type.GetMethods(InstanceFlags);
                for (int i = 0; i < methods.Length && item.methods.Count < 80; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.IsSpecialName) continue;
                    try
                    {
                        string signature = MethodSignature(method);
                        if (!item.methods.Contains(signature)) item.methods.Add(signature);
                    }
                    catch { }
                }
                item.methods.Sort(StringComparer.OrdinalIgnoreCase);
            }
            return item;
        }

        private static string MethodSignature(MethodInfo method)
        {
            if (method == null) return String.Empty;
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            b.Append(method.ReturnType == null ? "void" : method.ReturnType.Name);
            b.Append(" ").Append(method.Name).Append("(");
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) b.Append(", ");
                b.Append(parameters[i].ParameterType == null ? "?" : parameters[i].ParameterType.Name);
                if (!String.IsNullOrEmpty(parameters[i].Name)) b.Append(" ").Append(parameters[i].Name);
            }
            b.Append(")");
            if (method.DeclaringType != null) b.Append(" @").Append(method.DeclaringType.Name);
            return b.ToString();
        }

        private static bool InterestingMember(string name, string value)
        {
            string n = (name ?? String.Empty).ToLowerInvariant();
            string v = (value ?? String.Empty).ToLowerInvariant();
            if (ContainsAny(n, "description", "resource", "folder", "prefab", "path", "target", "count", "worldsize", "spawn", "distance", "filter", "density", "rock", "cliff")) return true;
            if (ContainsAny(v, "god", "rock", "formation", "anvil", "arch", "v3_")) return true;
            return false;
        }

        private static string BuildSearchable(GodRockGeneratorComponentProbe item)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            b.Append(item.type).Append(" | ").Append(item.description).Append(" | ").Append(item.gameObject).Append(" | ").Append(item.hierarchy);
            foreach (KeyValuePair<string, string> kv in item.members) b.Append(" | ").Append(kv.Key).Append("=").Append(kv.Value);
            return b.ToString().ToLowerInvariant();
        }

        private static string Hierarchy(Transform transform)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            Transform current = transform;
            for (int depth = 0; current != null && depth < 12; depth++, current = current.parent)
            {
                if (b.Length > 0) b.Append(" / ");
                b.Append(current.gameObject == null ? current.name : current.gameObject.name);
            }
            return b.ToString();
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj.GetType();
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (field != null) { try { return field.GetValue(obj); } catch { } }
            PropertyInfo prop = type.GetProperty(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0) { try { return prop.GetValue(obj, null); } catch { } }
            return null;
        }

        private static bool HasWritableIntMember(object obj, string name)
        {
            if (obj == null) return false;
            Type type = obj.GetType();
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (field != null && (field.FieldType == typeof(int) || field.FieldType == typeof(uint))) return true;
            PropertyInfo prop = type.GetProperty(name, InstanceFlags | BindingFlags.IgnoreCase);
            return prop != null && prop.CanWrite && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(uint));
        }

        private static bool TrySetIntMember(object obj, string name, int value)
        {
            if (obj == null) return false;
            Type type = obj.GetType();
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (field != null)
            {
                try
                {
                    if (field.FieldType == typeof(int)) field.SetValue(obj, value);
                    else if (field.FieldType == typeof(uint)) field.SetValue(obj, (uint)Math.Max(0, value));
                    else return false;
                    return true;
                }
                catch { }
            }
            PropertyInfo prop = type.GetProperty(name, InstanceFlags | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    if (prop.PropertyType == typeof(int)) prop.SetValue(obj, value, null);
                    else if (prop.PropertyType == typeof(uint)) prop.SetValue(obj, (uint)Math.Max(0, value), null);
                    else return false;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static string ValueText(object value, int depth)
        {
            if (value == null) return "<null>";
            if (depth > 1) return value.GetType().Name;
            string text = value as string;
            if (text != null) return Limit(text);
            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is decimal) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            UnityEngine.Object unity = value as UnityEngine.Object;
            if (unity != null) return unity.GetType().Name + ":" + unity.name;
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count++ >= 12) { b.Append(";..."); break; }
                    if (b.Length > 0) b.Append(";");
                    b.Append(ValueText(item, depth + 1));
                }
                return Limit(b.ToString());
            }
            return Limit(Convert.ToString(value));
        }

        private static string Limit(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            return value.Length <= 700 ? value : value.Substring(0, 700) + "...";
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (String.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < terms.Length; i++) if (value.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static uint ReadWorldUInt(string name)
        {
            try
            {
                Type world = AccessTools.TypeByName("World");
                if (world == null) return 0;
                BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                FieldInfo field = world.GetField(name, flags);
                if (field != null) return Convert.ToUInt32(field.GetValue(null));
                PropertyInfo prop = world.GetProperty(name, flags);
                if (prop != null) return Convert.ToUInt32(prop.GetValue(null, null));
            }
            catch { }
            return 0;
        }

        private static void WriteProbe(GodRockGeneratorProbe probe)
        {
            try
            {
                string folder = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenMapReports"));
                Directory.CreateDirectory(folder);
                string json = JsonConvert.SerializeObject(probe, Formatting.Indented);
                string specific = Path.Combine(folder, "RavenGodRockGeneratorProbe_" + probe.worldSize + "_" + probe.seed + ".json");
                File.WriteAllText(specific, json);
                File.WriteAllText(Path.Combine(folder, "RavenGodRockGeneratorProbe_latest.json"), json);
            }
            catch (Exception ex) { Debug.LogError("[Raven GodRock Generator] Probe write failed: " + ex.Message); }
        }
    }


    internal sealed class GodRockSelectedPrefabInstanceProbe
    {
        public int index;
        public string prefabPath;
        public string rootName;
        public float x;
        public float y;
        public float z;
        public float rotationY;
        public int childTransformCount;
        public int rendererCount;
        public float boundsSizeX;
        public float boundsSizeY;
        public float boundsSizeZ;
        public bool hasTerrainHeightLikeComponent;
        public bool hasMonumentLikeComponent;
        public Dictionary<string, int> hugeChildren = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<string> componentTypes = new List<string>();
        public List<string> terrainLikeComponents = new List<string>();
        public List<string> interestingChildNames = new List<string>();
        public string structuralFingerprint;
    }

    internal sealed class GodRockPrefabSelectionProbe
    {
        public string format = "raven-godrock-prefab-selection-probe-v1";
        public string generatedAtUtc;
        public uint seed;
        public uint worldSize;
        public string semantics = "selected runtime roots whose prefab path is under decor/v3_rock_formations_large; diagnostic only, no God Rock classification";
        public int transformsScanned;
        public int rootInstancesAccepted;
        public int invalidSceneObjects;
        public int invalidPositions;
        public Dictionary<string, int> prefabCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<GodRockSelectedPrefabInstanceProbe> instances = new List<GodRockSelectedPrefabInstanceProbe>();
        public List<string> notes = new List<string>();
    }

    // v3.4.8 Prefab Selection Probe
    //
    // The generator probe proved that large formations are produced by
    // PlaceDecorWhiteNoise(ResourceFolder=decor/v3_rock_formations_large), which
    // exposes ObjectDensity instead of TargetCount. Changing that density would
    // affect every large formation, not only God Rock.
    //
    // Therefore this patch does NOT mutate generation. At DONE it records the
    // exact large-formation root prefabs that Rust actually selected, their world
    // positions and a structural fingerprint (components/huge children/bounds).
    // This is the safe data needed to distinguish Large God Rock from
    // Anvil/Arch/ordinary rock formations before any count controller is enabled.
    [HarmonyPatch]
    [HarmonyBefore("com.facepunch.rust_dedicated.CustomGenerator")]
    [HarmonyPriority(Priority.First)]
    public static class RavenGodRockPrefabSelectionProbePatch
    {
        private static bool _ran;

        // v3.5.0 production build: the A-root identity is now verified. The old
        // 500k+ transform selection probe is intentionally disabled to avoid
        // unnecessary generation-time overhead.
        public static bool Prepare() { return false; }

        public static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UI_LoadingScreen");
            return type == null ? null : AccessTools.Method(type, "Update", new Type[] { typeof(string) });
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref string strType)
        {
            if (_ran || !String.Equals(strType, "DONE", StringComparison.Ordinal)) return;
            _ran = true;
            try { GodRockPrefabSelectionProbeWriter.Write(); }
            catch (Exception ex) { Debug.LogError("[Raven GodRock Selection Probe] Failed: " + ex); }
        }
    }

    internal static class GodRockPrefabSelectionProbeWriter
    {
        private const string LargeFormationFolder = "/decor/v3_rock_formations_large/";

        public static void Write()
        {
            GodRockPrefabSelectionProbe probe = new GodRockPrefabSelectionProbe();
            probe.generatedAtUtc = DateTime.UtcNow.ToString("o");
            probe.seed = ReadWorldUInt("Seed");
            probe.worldSize = ReadWorldUInt("Size");
            probe.notes.Add("God Rock classification intentionally disabled in this probe.");
            probe.notes.Add("rock_formation_huge_c is a reusable child and is never treated as a God Rock root.");
            probe.notes.Add("Powerline logic is untouched by v3.4.8.");

            UnityEngine.Object[] objects = Resources.FindObjectsOfTypeAll(typeof(Transform));
            if (objects == null)
            {
                probe.notes.Add("Resources.FindObjectsOfTypeAll(Transform) returned null.");
                WriteFiles(probe);
                return;
            }

            HashSet<int> acceptedRoots = new HashSet<int>();
            for (int i = 0; i < objects.Length; i++)
            {
                Transform transform = objects[i] as Transform;
                if (transform == null) continue;
                probe.transformsScanned++;

                if (!IsValidSceneTransform(transform))
                {
                    probe.invalidSceneObjects++;
                    continue;
                }

                Transform root;
                string prefabPath;
                if (!TryFindLargeFormationRoot(transform, out root, out prefabPath)) continue;
                if (root == null || !acceptedRoots.Add(root.GetInstanceID())) continue;

                Vector3 position = root.position;
                if (!ValidNumber(position.x) || !ValidNumber(position.y) || !ValidNumber(position.z))
                {
                    probe.invalidPositions++;
                    continue;
                }

                GodRockSelectedPrefabInstanceProbe item = InspectRoot(root, prefabPath, probe.instances.Count);
                probe.instances.Add(item);
                int old;
                probe.prefabCounts.TryGetValue(item.prefabPath ?? String.Empty, out old);
                probe.prefabCounts[item.prefabPath ?? String.Empty] = old + 1;
            }

            probe.rootInstancesAccepted = probe.instances.Count;
            probe.instances = probe.instances
                .OrderBy(x => x.prefabPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.x)
                .ThenBy(x => x.z)
                .ToList();

            probe.notes.Add("Selected root prefab variants=" + probe.prefabCounts.Count + ", instances=" + probe.rootInstancesAccepted + ".");
            WriteFiles(probe);
            Debug.Log("[Raven GodRock Selection Probe] Complete. transforms=" + probe.transformsScanned
                + ", roots=" + probe.rootInstancesAccepted + ", variants=" + probe.prefabCounts.Count);
        }

        private static GodRockSelectedPrefabInstanceProbe InspectRoot(Transform root, string prefabPath, int index)
        {
            GodRockSelectedPrefabInstanceProbe item = new GodRockSelectedPrefabInstanceProbe();
            item.index = index;
            item.prefabPath = NormalizePath(prefabPath);
            item.rootName = root.gameObject == null ? root.name : root.gameObject.name;
            Vector3 position = root.position;
            item.x = position.x;
            item.y = position.y;
            item.z = position.z;
            try { item.rotationY = root.eulerAngles.y; } catch { item.rotationY = 0f; }

            Transform[] transforms;
            try { transforms = root.GetComponentsInChildren<Transform>(true); }
            catch { transforms = new Transform[] { root }; }
            if (transforms == null) transforms = new Transform[] { root };
            item.childTransformCount = transforms.Length;

            HashSet<string> componentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> terrainTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform == null) continue;
                string localName = transform.gameObject == null ? transform.name : transform.gameObject.name;
                string lowerName = (localName ?? String.Empty).ToLowerInvariant();

                string hugeVariant = ExtractHugeVariant(lowerName);
                if (!String.IsNullOrEmpty(hugeVariant))
                {
                    int old;
                    item.hugeChildren.TryGetValue(hugeVariant, out old);
                    item.hugeChildren[hugeVariant] = old + 1;
                }

                if (ContainsAny(lowerName, "god", "anvil", "arch", "huge", "terrain", "height", "rock_formation"))
                    childNames.Add(Limit(localName, 180));

                Component[] components = null;
                try { components = transform.GetComponents<Component>(); } catch { }
                if (components == null) continue;
                for (int c = 0; c < components.Length; c++)
                {
                    Component component = components[c];
                    if (component == null) continue;
                    Type type = component.GetType();
                    string typeName = type.FullName ?? type.Name;
                    componentTypes.Add(typeName);

                    string lowerType = typeName.ToLowerInvariant();
                    if (ContainsAny(lowerType, "terrain", "height", "topology", "biome", "splat"))
                    {
                        terrainTypes.Add(typeName);
                        item.hasTerrainHeightLikeComponent = true;
                    }
                    if (ContainsAny(lowerType, "monument", "landmark"))
                        item.hasMonumentLikeComponent = true;
                }
            }

            item.componentTypes = componentTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(120).ToList();
            item.terrainLikeComponents = terrainTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(80).ToList();
            item.interestingChildNames = childNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(120).ToList();

            Renderer[] renderers = null;
            try { renderers = root.GetComponentsInChildren<Renderer>(true); } catch { }
            if (renderers != null)
            {
                item.rendererCount = renderers.Length;
                bool hasBounds = false;
                Bounds combined = new Bounds();
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer renderer = renderers[r];
                    if (renderer == null) continue;
                    try
                    {
                        if (!hasBounds)
                        {
                            combined = renderer.bounds;
                            hasBounds = true;
                        }
                        else combined.Encapsulate(renderer.bounds);
                    }
                    catch { }
                }
                if (hasBounds)
                {
                    item.boundsSizeX = combined.size.x;
                    item.boundsSizeY = combined.size.y;
                    item.boundsSizeZ = combined.size.z;
                }
            }

            item.structuralFingerprint = BuildFingerprint(item);
            return item;
        }

        private static string BuildFingerprint(GodRockSelectedPrefabInstanceProbe item)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            b.Append(Path.GetFileNameWithoutExtension(item.prefabPath ?? String.Empty).ToLowerInvariant());
            b.Append("|t=").Append(item.childTransformCount);
            b.Append("|r=").Append(item.rendererCount);
            b.Append("|bounds=")
                .Append(item.boundsSizeX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("x")
                .Append(item.boundsSizeY.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("x")
                .Append(item.boundsSizeZ.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            b.Append("|terrain=").Append(item.hasTerrainHeightLikeComponent ? "1" : "0");
            foreach (KeyValuePair<string, int> kv in item.hugeChildren.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                b.Append("|").Append(kv.Key).Append("=").Append(kv.Value);
            return b.ToString();
        }

        private static bool TryFindLargeFormationRoot(Transform start, out Transform root, out string prefabPath)
        {
            root = null;
            prefabPath = null;
            Transform current = start;
            for (int depth = 0; current != null && depth < 16; depth++, current = current.parent)
            {
                string name = current.gameObject == null ? current.name : current.gameObject.name;
                string normalized = NormalizePath(name);
                if (normalized.IndexOf(LargeFormationFolder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                root = current;
                prefabPath = normalized;
                return true;
            }
            return false;
        }

        private static bool IsValidSceneTransform(Transform transform)
        {
            try
            {
                if (transform == null || transform.gameObject == null) return false;
                object scene = GetMember(transform.gameObject, "scene", "Scene");
                if (scene == null) return true;
                MethodInfo isValid = AccessTools.Method(scene.GetType(), "IsValid", Type.EmptyTypes);
                if (isValid == null) return true;
                object result = isValid.Invoke(scene, null);
                return result == null || Convert.ToBoolean(result, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return true; }
        }

        private static object GetMember(object obj, params string[] names)
        {
            if (obj == null || names == null) return null;
            Type type = obj.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    FieldInfo field = type.GetField(names[i], flags);
                    if (field != null) return field.GetValue(obj);
                    PropertyInfo prop = type.GetProperty(names[i], flags);
                    if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                        return prop.GetValue(obj, null);
                }
                catch { }
            }
            return null;
        }

        private static string ExtractHugeVariant(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;
            string lower = name.ToLowerInvariant();
            int index = lower.IndexOf("rock_formation_huge_", StringComparison.Ordinal);
            if (index < 0) return null;
            int start = index + "rock_formation_huge_".Length;
            if (start >= lower.Length) return "huge_unknown";
            char c = lower[start];
            if (c >= 'a' && c <= 'z') return "huge_" + c;
            return "huge_unknown";
        }

        private static string NormalizePath(string value)
        {
            return (value ?? String.Empty).Replace('\\', '/').Trim();
        }

        private static bool ValidNumber(float value)
        {
            return !Single.IsNaN(value) && !Single.IsInfinity(value);
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (String.IsNullOrEmpty(value) || terms == null) return false;
            for (int i = 0; i < terms.Length; i++)
                if (!String.IsNullOrEmpty(terms[i]) && value.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string Limit(string value, int max)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static uint ReadWorldUInt(string name)
        {
            try
            {
                Type world = AccessTools.TypeByName("World");
                if (world == null) return 0;
                BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                FieldInfo field = world.GetField(name, flags);
                if (field != null) return Convert.ToUInt32(field.GetValue(null));
                PropertyInfo prop = world.GetProperty(name, flags);
                if (prop != null) return Convert.ToUInt32(prop.GetValue(null, null));
            }
            catch { }
            return 0;
        }

        private static void WriteFiles(GodRockPrefabSelectionProbe probe)
        {
            try
            {
                string folder = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenMapReports"));
                Directory.CreateDirectory(folder);
                string json = JsonConvert.SerializeObject(probe, Formatting.Indented);
                string specific = Path.Combine(folder, "RavenGodRockPrefabSelectionProbe_" + probe.worldSize + "_" + probe.seed + ".json");
                File.WriteAllText(specific, json);
                File.WriteAllText(Path.Combine(folder, "RavenGodRockPrefabSelectionProbe_latest.json"), json);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven GodRock Selection Probe] Write failed: " + ex.Message);
            }
        }
    }

    // v3.5.2: only the verified God Rock exact-count controller may mutate the map.
    // Every other monument rule is validation/native-generator only. Rust still saves
    // the requested single seed even when a requested monument is naturally missing.
    [HarmonyPatch]
    [HarmonyBefore("com.facepunch.rust_dedicated.CustomGenerator")]
    [HarmonyPriority(Priority.First)]
    public static class RavenGodRockExactCountBeforeSavePatch
    {
        private static string _completedSignature;

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(WorldSerialization), "Save", new Type[] { typeof(string) });
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(WorldSerialization __instance, string __0)
        {
            try
            {
                uint seed = GuaranteedPlacementEngine.ReadWorldUIntForPatch("Seed");
                uint size = GuaranteedPlacementEngine.ReadWorldUIntForPatch("Size");
                string fullPath = String.IsNullOrWhiteSpace(__0) ? String.Empty : Path.GetFullPath(__0);
                string signature = seed + ":" + size + ":" + fullPath.ToLowerInvariant();
                if (!String.IsNullOrEmpty(_completedSignature) && String.Equals(_completedSignature, signature, StringComparison.Ordinal)) return;

                // v3.5.2 mutates only the verified God Rock root slots. All other
                // monument rules are observed/validated only; no blind injection/removal.
                if (GuaranteedPlacementEngine.TryApplyConfiguredRulesBeforeSave(__instance, fullPath))
                    _completedSignature = signature;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven Guaranteed Placement] Before-save patch failed; original Save continues: " + ex);
            }
        }
    }

    [HarmonyPatch]
    [HarmonyBefore("com.facepunch.rust_dedicated.CustomGenerator")]
    [HarmonyPriority(Priority.First)]
    public static class RavenGuaranteedPlacementPatch
    {
        private static bool _ran;

        public static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("UI_LoadingScreen");
            return type == null ? null : AccessTools.Method(type, "Update", new Type[] { typeof(string) });
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref string strType)
        {
            if (_ran || !string.Equals(strType, "DONE", StringComparison.Ordinal)) return;
            _ran = true;
            try
            {
                GuaranteedPlacementEngine.Run();
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven Guaranteed Placement] Fatal: " + ex);
            }
        }
    }

    internal static class GuaranteedPlacementEngine
    {
        private const string BuildMarker = "raven-guaranteed-placement-v2.3.0-godrock-only-natural-monuments";
        private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string GodRockRootPath = "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab";
        private static readonly string[] LargeRockFallbackPaths = new string[]
        {
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_c.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_j.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_b.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_d.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_e.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_f.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_g.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_h.prefab",
            "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_i.prefab"
        };

        internal static uint ReadWorldUIntForPatch(string member) { return ReadWorldUInt(member); }

        internal static bool TryApplyConfiguredRulesBeforeSave(WorldSerialization map, string savePath)
        {
            try
            {
                string configPath = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenGuaranteedPlacement.json"));
                if (!File.Exists(configPath)) return false;
                GuaranteedConfig config = JsonConvert.DeserializeObject<GuaranteedConfig>(File.ReadAllText(configPath));
                if (config == null || !config.enabled || config.rules == null || config.rules.Count == 0) return false;

                uint seed = ReadWorldUInt("Seed");
                uint size = ReadWorldUInt("Size");
                if (config.seed != 0 && seed != 0 && config.seed != seed) return false;
                if (config.worldSize != 0 && size != 0 && config.worldSize != size) return false;
                if (!String.IsNullOrWhiteSpace(config.mapPath) && !String.IsNullOrWhiteSpace(savePath))
                {
                    if (!String.Equals(Path.GetFileName(config.mapPath), Path.GetFileName(savePath), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                if (map == null || map.world == null || map.world.prefabs == null) return false;

                GuaranteedAudit audit = new GuaranteedAudit
                {
                    jobId = config.jobId,
                    seed = seed != 0 ? seed : config.seed,
                    worldSize = size != 0 ? size : config.worldSize,
                    mapPath = savePath,
                    beforeSaveApplied = true,
                    seedAttempts = 1,
                    saveContinuesOnRuleFailure = true,
                    deliveryMode = "single_seed_always_deliver"
                };
                List<PrefabData> prefabs = map.world.prefabs;
                Dictionary<uint, string> pathCache = new Dictionary<uint, string>();
                Func<PrefabData, string> getPath = p =>
                {
                    string cached;
                    if (pathCache.TryGetValue(p.id, out cached)) return cached;
                    string value = GetStringPoolPath(p.id) ?? String.Empty;
                    pathCache[p.id] = value;
                    return value;
                };

                foreach (GuaranteedRule rule in config.rules)
                    audit.initialCounts[rule.id] = CountMatches(prefabs, rule, getPath);

                // God Rock uses the verified rock_formation_a root-slot controller.
                ApplyGodRockRootController(config, prefabs, getPath, audit);

                // v3.5.2: no post-generator forcing for normal monuments.
                // Keep Rust's native procedural result untouched and only report whether
                // each requested/blocked rule happened to be satisfied in this one seed.
                foreach (GuaranteedRule rule in config.rules)
                {
                    if (!String.Equals(rule.id, "god_rocks", StringComparison.OrdinalIgnoreCase))
                        audit.warnings.Add("NATIVE_GENERATOR_ONLY_NO_POSTMAP_MUTATION:" + rule.id);
                }

                foreach (GuaranteedRule rule in config.rules)
                {
                    int count = CountMatches(prefabs, rule, getPath);
                    audit.finalCounts[rule.id] = count;
                    int min = String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, rule.min ?? 1) : 0;
                    int max = String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase) ? 0 : (rule.max ?? Int32.MaxValue);
                    if (String.Equals(rule.id, "god_rocks", StringComparison.OrdinalIgnoreCase) && String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase))
                        max = min; // God Rock is an exact-count rule.
                    if (count < min) audit.errors.Add("MIN_NOT_MET:" + rule.id + ":" + count + "/" + min);
                    if (count > max) audit.errors.Add("MAX_EXCEEDED:" + rule.id + ":" + count + "/" + max);
                }

                audit.ok = audit.errors.Count == 0;
                audit.partial = !audit.ok;
                audit.mapSaved = false; // original WorldSerialization.Save runs immediately after this prefix
                audit.warnings.Add("SINGLE_SEED_POLICY:seedAttempts=1,retryAnotherSeed=false");
                audit.warnings.Add(audit.ok
                    ? "ALL_RULES_READY_BEFORE_SAVE"
                    : "PARTIAL_RULE_RESULT_BUT_MAP_SAVE_WILL_CONTINUE");
                WriteAudit(config, audit);
                WriteRequiredValidation(config, audit);
                WriteGodRockStateFromAudit(config, audit, savePath);

                Debug.Log("[Raven Guaranteed Placement] Before-save validation result=" + (audit.ok ? "OK" : "PARTIAL")
                    + " | placements=" + audit.placements.Count + " removals=" + audit.removals.Count
                    + " | seed=" + audit.seed + " | map will be saved regardless");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven Guaranteed Placement] Before-save validation failed, original map save will continue: " + ex);
                return true;
            }
        }

        internal static bool TryApplyGodRockExactCountBeforeSave(WorldSerialization map, string savePath)
        {
            GodRockExactCountState state = new GodRockExactCountState();
            state.generatedAtUtc = DateTime.UtcNow.ToString("o");
            state.seed = ReadWorldUInt("Seed");
            state.worldSize = ReadWorldUInt("Size");
            state.mapPath = savePath;
            state.appliedBeforeSave = true;

            try
            {
                string configPath = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenGuaranteedPlacement.json"));
                if (!File.Exists(configPath)) { state.notes.Add("CONFIG_NOT_FOUND"); WriteGodRockExactCountState(state); return false; }
                GuaranteedConfig config = JsonConvert.DeserializeObject<GuaranteedConfig>(File.ReadAllText(configPath));
                if (config == null || !config.enabled || config.rules == null) { state.notes.Add("CONFIG_DISABLED_OR_INVALID"); WriteGodRockExactCountState(state); return false; }

                GuaranteedRule rule = config.rules.FirstOrDefault(r => String.Equals(r.id, "god_rocks", StringComparison.OrdinalIgnoreCase));
                if (rule == null || (!String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase) && !String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase)))
                {
                    state.notes.Add("GODROCK_RULE_NOT_REQUIRED_OR_BLOCKED");
                    WriteGodRockExactCountState(state);
                    return false;
                }

                state.ruleState = rule.state;
                state.targetCount = String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(1, rule.min ?? 1);

                if (config.seed != 0 && state.seed != 0 && config.seed != state.seed) { state.notes.Add("SEED_MISMATCH"); WriteGodRockExactCountState(state); return false; }
                if (config.worldSize != 0 && state.worldSize != 0 && config.worldSize != state.worldSize) { state.notes.Add("WORLD_SIZE_MISMATCH"); WriteGodRockExactCountState(state); return false; }
                if (!String.IsNullOrWhiteSpace(config.mapPath) && !String.IsNullOrWhiteSpace(savePath))
                {
                    string expectedName = Path.GetFileName(config.mapPath);
                    string actualName = Path.GetFileName(savePath);
                    if (!String.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase))
                    {
                        state.notes.Add("SAVE_PATH_NOT_TARGET_MAP:" + actualName);
                        WriteGodRockExactCountState(state);
                        return false;
                    }
                }

                if (map == null || map.world == null || map.world.prefabs == null) { state.notes.Add("PREFAB_LIST_NOT_READY"); WriteGodRockExactCountState(state); return false; }

                List<PrefabData> prefabs = map.world.prefabs;
                Dictionary<uint, string> pathCache = new Dictionary<uint, string>();
                Func<PrefabData, string> getPath = p =>
                {
                    string cached;
                    if (pathCache.TryGetValue(p.id, out cached)) return cached;
                    string value = GetStringPoolPath(p.id) ?? String.Empty;
                    pathCache[p.id] = value;
                    return value;
                };

                state.beforeCount = prefabs.Count(p => IsExactPath(getPath(p), GodRockRootPath));
                state.availableLargeRockSlots = prefabs.Count(p => IsLargeRockRootPath(getPath(p)) && !IsExactPath(getPath(p), GodRockRootPath));

                GuaranteedAudit audit = new GuaranteedAudit
                {
                    jobId = config.jobId,
                    seed = state.seed != 0 ? state.seed : config.seed,
                    worldSize = state.worldSize != 0 ? state.worldSize : config.worldSize,
                    mapPath = savePath
                };

                ApplyGodRockRootController(config, prefabs, getPath, audit);
                state.afterCount = prefabs.Count(p => IsExactPath(getPath(p), GodRockRootPath));
                state.convertedToGodRock = Math.Max(0, state.afterCount - state.beforeCount);
                state.convertedFromGodRock = Math.Max(0, state.beforeCount - state.afterCount);
                foreach (string error in audit.errors) state.notes.Add(error);
                foreach (string warning in audit.warnings) state.notes.Add(warning);
                state.ok = audit.errors.Count == 0 && state.afterCount == state.targetCount;
                if (state.ok) state.notes.Add("EXACT_COUNT_READY_BEFORE_MAP_SAVE");
                WriteGodRockExactCountState(state);

                Debug.Log("[Raven GodRock Exact Count] before=" + state.beforeCount + " target=" + state.targetCount + " after=" + state.afterCount + " save=" + savePath);
                return state.ok;
            }
            catch (Exception ex)
            {
                state.ok = false;
                state.notes.Add("EXCEPTION:" + ex);
                WriteGodRockExactCountState(state);
                return false;
            }
        }

        public static void Run()
        {
            string configPath = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenGuaranteedPlacement.json"));
            if (!File.Exists(configPath)) return;

            GuaranteedConfig config = JsonConvert.DeserializeObject<GuaranteedConfig>(File.ReadAllText(configPath));
            if (config == null || !config.enabled || config.rules == null || config.rules.Count == 0) return;

            uint currentSeed = ReadWorldUInt("Seed");
            uint currentSize = ReadWorldUInt("Size");
            if (config.seed != 0 && currentSeed != 0 && config.seed != currentSeed) return;
            if (config.worldSize != 0 && currentSize != 0 && config.worldSize != currentSize) return;

            string mapPath = ResolveMapPath(config);
            GuaranteedAudit audit = new GuaranteedAudit
            {
                jobId = config.jobId,
                seed = currentSeed != 0 ? currentSeed : config.seed,
                worldSize = currentSize != 0 ? currentSize : config.worldSize,
                mapPath = mapPath,
            };

            try
            {
                if (string.IsNullOrEmpty(mapPath) || !File.Exists(mapPath))
                {
                    audit.errors.Add("MAP_FILE_NOT_FOUND");
                    WriteAudit(config, audit);
                    return;
                }

                WorldSerialization map = new WorldSerialization();
                map.Load(mapPath);
                if (map.world == null || map.world.prefabs == null)
                {
                    audit.errors.Add("MAP_PREFAB_LIST_MISSING");
                    WriteAudit(config, audit);
                    return;
                }

                List<PrefabData> prefabs = map.world.prefabs;
                Dictionary<uint, string> pathCache = new Dictionary<uint, string>();
                Func<PrefabData, string> getPath = p =>
                {
                    string cached;
                    if (pathCache.TryGetValue(p.id, out cached)) return cached;
                    string value = GetStringPoolPath(p.id) ?? string.Empty;
                    pathCache[p.id] = value;
                    return value;
                };

                foreach (GuaranteedRule rule in config.rules)
                    audit.initialCounts[rule.id] = CountMatches(prefabs, rule, getPath);

                // v3.5.0: God Rock is the verified rock_formation_a ROOT inside the same
                // v3_rock_formations_large pool as B-J. The probe for a seed with
                // zero real God Rocks showed 74 selected roots covering B-J and zero
                // A roots. Instead of changing ObjectDensity or injecting a rock at
                // an arbitrary terrain position, convert already-valid large-rock
                // root slots to/from A. This preserves the procedural placement,
                // terrain suitability, rotation and scale while allowing an exact
                // user-selected count.
                ApplyGodRockRootController(config, prefabs, getPath, audit);

                // v3.5.2: normal monuments are never injected, removed or repositioned
                // after procedural generation. God Rock is the sole verified exception.
                foreach (GuaranteedRule rule in config.rules)
                {
                    if (!String.Equals(rule.id, "god_rocks", StringComparison.OrdinalIgnoreCase))
                        audit.warnings.Add("NATIVE_GENERATOR_ONLY_NO_POSTMAP_MUTATION:" + rule.id);
                }

                foreach (GuaranteedRule rule in config.rules)
                {
                    int count = CountMatches(prefabs, rule, getPath);
                    audit.finalCounts[rule.id] = count;
                    int min = rule.state == "required" ? Math.Max(1, rule.min ?? 1) : 0;
                    int max = rule.state == "blocked" ? 0 : (rule.max ?? int.MaxValue);
                    if (String.Equals(rule.id, "god_rocks", StringComparison.OrdinalIgnoreCase) && rule.state == "required") max = min;
                    if (count < min) audit.errors.Add("MIN_NOT_MET:" + rule.id + ":" + count + "/" + min);
                    if (count > max) audit.errors.Add("MAX_EXCEEDED:" + rule.id + ":" + count + "/" + max);
                    if (rule.generatorOnly && (count < min || count > max)) audit.warnings.Add("VALIDATION_ONLY_RULE_NOT_MET:" + rule.id);
                }

                audit.ok = audit.errors.Count == 0;
                audit.partial = !audit.ok;
                // v3.5.2 policy: one seed only and always deliver that map.
                // Normal monument rules are validation-only; God Rock exact-count is the
                // only verified map mutation. Missing rules never trigger another seed.
                string backupPath = mapPath + ".raven_before_guarantee.bak";
                File.Copy(mapPath, backupPath, true);
                audit.backupPath = backupPath;
                map.Save(mapPath);
                audit.mapSaved = true;
                audit.warnings.Add("SINGLE_SEED_POLICY:seedAttempts=1,retryAnotherSeed=false");
                if (!audit.ok) audit.warnings.Add("PARTIAL_RULE_RESULT_MAP_SAVED_AND_DELIVERED");
                WriteRequiredValidation(config, audit);
            }
            catch (Exception ex)
            {
                audit.errors.Add("ENGINE_EXCEPTION:" + ex);
                audit.ok = false;
            }

            WriteAudit(config, audit);
            Debug.Log("[Raven Guaranteed Placement] " + (audit.ok ? "Completed" : "Failed") + " | " + audit.mapPath);
        }

        private static void ApplyGodRockRootController(GuaranteedConfig config, List<PrefabData> prefabs, Func<PrefabData, string> getPath, GuaranteedAudit audit)
        {
            GuaranteedRule rule = config.rules.FirstOrDefault(r => String.Equals(r.id, "god_rocks", StringComparison.OrdinalIgnoreCase));
            if (rule == null) return;
            if (!String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase))
                return;

            int target = String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase)
                ? 0 : Math.Max(1, rule.min ?? 1);

            uint godId = ResolveStringPoolId(GodRockRootPath);
            if (godId == 0)
            {
                audit.errors.Add("GODROCK_ROOT_A_ID_NOT_RESOLVED:" + GodRockRootPath);
                return;
            }

            List<PrefabData> existingGod = prefabs.Where(p => IsExactPath(getPath(p), GodRockRootPath)).ToList();
            int before = existingGod.Count;
            audit.warnings.Add("GODROCK_ROOT_A_CONTROLLER:before=" + before + ",target=" + target);

            if (before < target)
            {
                int needed = target - before;
                List<PrefabData> candidates = prefabs
                    .Where(p => IsLargeRockRootPath(getPath(p)) && !IsExactPath(getPath(p), GodRockRootPath))
                    .ToList();

                List<PrefabData> chosen = ChooseSpreadLargeRockSlots(candidates, existingGod, needed, audit.seed);
                if (chosen.Count < needed)
                {
                    audit.errors.Add("GODROCK_NOT_ENOUGH_LARGE_ROCK_SLOTS:" + chosen.Count + "/" + needed);
                    return;
                }

                for (int i = 0; i < chosen.Count; i++)
                {
                    PrefabData slot = chosen[i];
                    string oldPath = getPath(slot);
                    audit.removals.Add(ToAuditEntry("god_rocks_slot_replaced", oldPath, slot));
                    slot.id = godId;
                    audit.placements.Add(ToAuditEntry("god_rocks", GodRockRootPath, slot));
                }
            }
            else if (before > target)
            {
                int excess = before - target;
                List<PrefabData> replace = existingGod
                    .OrderByDescending(p => StableRockScore(p, audit.seed))
                    .Take(excess)
                    .ToList();

                for (int i = 0; i < replace.Count; i++)
                {
                    PrefabData slot = replace[i];
                    string fallback = ResolveFallbackRockPath(i, audit.seed);
                    uint fallbackId = ResolveStringPoolId(fallback);
                    if (fallbackId == 0)
                    {
                        audit.errors.Add("GODROCK_FALLBACK_ID_NOT_RESOLVED:" + fallback);
                        return;
                    }
                    audit.removals.Add(ToAuditEntry("god_rocks", GodRockRootPath, slot));
                    slot.id = fallbackId;
                    audit.placements.Add(ToAuditEntry("god_rocks_slot_replacement", fallback, slot));
                }
            }

            int after = prefabs.Count(p => IsExactPath(getPath(p), GodRockRootPath));
            audit.warnings.Add("GODROCK_ROOT_A_CONTROLLER:after=" + after + ",target=" + target);
            if (after != target) audit.errors.Add("GODROCK_EXACT_COUNT_NOT_MET:" + after + "/" + target);
        }

        private static List<PrefabData> ChooseSpreadLargeRockSlots(List<PrefabData> candidates, List<PrefabData> existingGod, int needed, uint seed)
        {
            List<PrefabData> result = new List<PrefabData>();
            if (needed <= 0) return result;

            List<PrefabData> ordered = candidates
                .OrderBy(p => StableRockScore(p, seed))
                .ToList();

            List<PrefabData> occupied = new List<PrefabData>();
            if (existingGod != null) occupied.AddRange(existingGod);

            float[] spacingPasses = new float[] { 650f, 500f, 350f, 220f, 0f };
            for (int pass = 0; pass < spacingPasses.Length && result.Count < needed; pass++)
            {
                float spacing = spacingPasses[pass];
                float spacingSq = spacing * spacing;
                for (int i = 0; i < ordered.Count && result.Count < needed; i++)
                {
                    PrefabData candidate = ordered[i];
                    if (result.Contains(candidate)) continue;
                    bool close = occupied.Any(other =>
                    {
                        float dx = other.position.x - candidate.position.x;
                        float dz = other.position.z - candidate.position.z;
                        return dx * dx + dz * dz < spacingSq;
                    });
                    if (close) continue;
                    result.Add(candidate);
                    occupied.Add(candidate);
                }
            }
            return result;
        }

        private static long StableRockScore(PrefabData prefab, uint seed)
        {
            unchecked
            {
                long x = (long)Math.Round(prefab.position.x * 10f);
                long z = (long)Math.Round(prefab.position.z * 10f);
                long h = 1469598103934665603L;
                h = (h ^ x) * 1099511628211L;
                h = (h ^ z) * 1099511628211L;
                h = (h ^ (long)seed) * 1099511628211L;
                return h & 0x7fffffffffffffffL;
            }
        }

        private static string ResolveFallbackRockPath(int index, uint seed)
        {
            int offset = (int)(seed % (uint)LargeRockFallbackPaths.Length);
            return LargeRockFallbackPaths[(offset + index) % LargeRockFallbackPaths.Length];
        }

        private static bool IsLargeRockRootPath(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            const string prefix = "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return false;
            string suffix = normalized.Substring(prefix.Length, normalized.Length - prefix.Length - ".prefab".Length);
            return suffix.Length == 1 && suffix[0] >= 'a' && suffix[0] <= 'j';
        }

        private static bool IsExactPath(string path, string expected)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(expected)) return false;
            return String.Equals(path.Replace('\\', '/').Trim(), expected.Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static int CountMatches(IEnumerable<PrefabData> prefabs, GuaranteedRule rule, Func<PrefabData, string> getPath)
        {
            return prefabs.Count(p => IsMatch(getPath(p), rule));
        }

        private static bool IsMatch(string path, GuaranteedRule rule)
        {
            if (string.IsNullOrEmpty(path) || rule == null) return false;
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            string spawn = (rule.spawnPrefab ?? string.Empty).Replace('\\', '/').ToLowerInvariant().Trim();
            if (spawn.Length >= 3 && string.Equals(normalized, spawn, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.prefabMarkers == null) return false;
            foreach (string marker in rule.prefabMarkers)
            {
                string m = (marker ?? string.Empty).Replace('\\', '/').ToLowerInvariant().Trim();
                if (m.Length >= 3 && normalized.Contains(m)) return true;
            }
            return false;
        }

        private static GuaranteedPlacementEntry ToAuditEntry(string id, string path, PrefabData prefab)
        {
            return new GuaranteedPlacementEntry
            {
                id = id,
                prefabPath = path,
                x = prefab.position.x,
                y = prefab.position.y,
                z = prefab.position.z,
                rotationY = prefab.rotation.y,
            };
        }

        private static float DistanceFromOrigin(VectorData p)
        {
            return p.x * p.x + p.z * p.z;
        }

        private static string ResolveMapPath(GuaranteedConfig config)
        {
            try
            {
                if (config != null && !string.IsNullOrWhiteSpace(config.mapPath))
                {
                    string explicitPath = Path.GetFullPath(config.mapPath);
                    if (File.Exists(explicitPath)) return explicitPath;
                }
            }
            catch { }

            try
            {
                object folder = GetStaticMember(typeof(World), "MapFolderName");
                object file = GetStaticMember(typeof(World), "MapFileName");
                string folderText = Convert.ToString(folder);
                string fileText = Convert.ToString(file);
                if (!string.IsNullOrEmpty(folderText) && !string.IsNullOrEmpty(fileText))
                    return Path.GetFullPath(Path.Combine(folderText, fileText));
            }
            catch { }

            try
            {
                string maps = Path.GetFullPath("maps");
                return Directory.GetFiles(maps, "*.map").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch { return null; }
        }

        private static uint ReadWorldUInt(string member)
        {
            try
            {
                object value = GetStaticMember(typeof(World), member);
                return Convert.ToUInt32(value);
            }
            catch { return 0; }
        }

        private static object GetStaticMember(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, StaticFlags);
            if (property != null) return property.GetValue(null, null);
            FieldInfo field = type.GetField(name, StaticFlags);
            if (field != null) return field.GetValue(null);
            return null;
        }

        private static string GetStringPoolPath(uint id)
        {
            try
            {
                MethodInfo method = typeof(StringPool).GetMethods(StaticFlags)
                    .FirstOrDefault(m => m.Name == "Get" && m.ReturnType == typeof(string) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(uint));
                return method == null ? null : Convert.ToString(method.Invoke(null, new object[] { id }));
            }
            catch { return null; }
        }

        private static uint GetStringPoolId(string path)
        {
            try
            {
                MethodInfo method = typeof(StringPool).GetMethods(StaticFlags)
                    .FirstOrDefault(m => m.Name == "Get" && m.ReturnType == typeof(uint) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (method == null) return 0;
                return Convert.ToUInt32(method.Invoke(null, new object[] { path }));
            }
            catch { return 0; }
        }

        private static uint ResolveStringPoolId(string path)
        {
            uint id = GetStringPoolId(path);
            if (id != 0) return id;
            try
            {
                MethodInfo method = typeof(StringPool).GetMethods(StaticFlags)
                    .FirstOrDefault(m => (m.Name == "GetOrAdd" || m.Name == "Add") && m.ReturnType == typeof(uint)
                        && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (method != null) return Convert.ToUInt32(method.Invoke(null, new object[] { path }));
            }
            catch { }
            return 0;
        }

        private static void WriteGodRockExactCountState(GodRockExactCountState state)
        {
            try
            {
                string folder = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenMapReports"));
                Directory.CreateDirectory(folder);
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                string specific = Path.Combine(folder, "RavenGodRockExactCount_" + state.worldSize + "_" + state.seed + ".json");
                File.WriteAllText(specific, json);
                File.WriteAllText(Path.Combine(folder, "RavenGodRockExactCount_latest.json"), json);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven GodRock Exact Count] State write failed: " + ex.Message);
            }
        }

        private static void WriteGodRockStateFromAudit(GuaranteedConfig config, GuaranteedAudit audit, string savePath)
        {
            try
            {
                GuaranteedRule rule = config.rules.FirstOrDefault(r => String.Equals(r.id, "god_rocks", StringComparison.OrdinalIgnoreCase));
                if (rule == null) return;
                GodRockExactCountState state = new GodRockExactCountState();
                state.generatedAtUtc = DateTime.UtcNow.ToString("o");
                state.seed = audit.seed;
                state.worldSize = audit.worldSize;
                state.mapPath = savePath;
                state.ruleState = rule.state;
                state.targetCount = String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(1, rule.min ?? 1);
                int before;
                int after;
                audit.initialCounts.TryGetValue(rule.id, out before);
                audit.finalCounts.TryGetValue(rule.id, out after);
                state.beforeCount = before;
                state.afterCount = after;
                state.convertedToGodRock = Math.Max(0, after - before);
                state.convertedFromGodRock = Math.Max(0, before - after);
                state.appliedBeforeSave = audit.beforeSaveApplied;
                state.ok = after == state.targetCount;
                state.notes.Add("SINGLE_SEED_ALWAYS_DELIVER_POLICY");
                state.notes.Add(state.ok ? "EXACT_COUNT_READY" : "EXACT_COUNT_NOT_MET_BUT_MAP_WILL_BE_DELIVERED");
                WriteGodRockExactCountState(state);
            }
            catch { }
        }

        private static void WriteRequiredValidation(GuaranteedConfig config, GuaranteedAudit audit)
        {
            try
            {
                RequiredMonumentsValidationReport report = new RequiredMonumentsValidationReport();
                report.generatedAtUtc = DateTime.UtcNow.ToString("o");
                report.seed = audit.seed;
                report.worldSize = audit.worldSize;
                report.allRulesMet = audit.ok;
                foreach (GuaranteedRule rule in config.rules)
                {
                    int before;
                    int after;
                    audit.initialCounts.TryGetValue(rule.id, out before);
                    audit.finalCounts.TryGetValue(rule.id, out after);
                    int min = String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase) ? Math.Max(1, rule.min ?? 1) : 0;
                    int max = String.Equals(rule.state, "blocked", StringComparison.OrdinalIgnoreCase) ? 0 : (rule.max ?? Int32.MaxValue);
                    if (String.Equals(rule.id, "god_rocks", StringComparison.OrdinalIgnoreCase) && String.Equals(rule.state, "required", StringComparison.OrdinalIgnoreCase)) max = min;
                    bool ok = after >= min && after <= max;
                    report.rules.Add(new RequiredRuleValidationItem
                    {
                        id = rule.id,
                        name = rule.name,
                        state = rule.state,
                        targetMin = min,
                        targetMax = max,
                        beforeCount = before,
                        afterCount = after,
                        injectionAllowed = !rule.generatorOnly,
                        status = ok ? "OK" : (rule.generatorOnly ? "WARNING_VALIDATE_ONLY" : "WARNING_NOT_MET")
                    });
                }
                report.notes.Add("Only the requested seed was generated. No alternate-seed retry is performed.");
                report.notes.Add(report.allRulesMet
                    ? "All configured Required/Blocked rules were satisfied in this seed."
                    : "One or more rules were not satisfied safely; the same seed map is still delivered.");

                string folder = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenMapReports"));
                Directory.CreateDirectory(folder);
                string json = JsonConvert.SerializeObject(report, Formatting.Indented);
                File.WriteAllText(Path.Combine(folder, "RavenRequiredMonumentsValidation_" + report.worldSize + "_" + report.seed + ".json"), json);
                File.WriteAllText(Path.Combine(folder, "RavenRequiredMonumentsValidation_latest.json"), json);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven Guaranteed Placement] Validation report write failed: " + ex.Message);
            }
        }

        private static string ResolveSpawnPath(GuaranteedRule rule)
        {
            if (!string.IsNullOrWhiteSpace(rule.spawnPrefab)) return rule.spawnPrefab.Replace('\\', '/');
            try
            {
                Type manifestType = AccessTools.TypeByName("GameManifest");
                object current = manifestType == null ? null : GetStaticMember(manifestType, "Current");
                if (current == null) return null;
                object entities = null;
                PropertyInfo p = current.GetType().GetProperty("entities", InstanceFlags) ?? current.GetType().GetProperty("Entities", InstanceFlags);
                if (p != null) entities = p.GetValue(current, null);
                FieldInfo f = current.GetType().GetField("entities", InstanceFlags) ?? current.GetType().GetField("Entities", InstanceFlags);
                if (entities == null && f != null) entities = f.GetValue(current);
                IEnumerable enumerable = entities as IEnumerable;
                if (enumerable == null) return null;

                List<string> candidates = new List<string>();
                foreach (object item in enumerable)
                {
                    string path = Convert.ToString(item);
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsMatch(path, rule)) candidates.Add(path.Replace('\\', '/'));
                }
                return candidates.OrderBy(x => x.Length).FirstOrDefault();
            }
            catch { return null; }
        }

        // Root monument centres are not their physical bounds. Launch Site, Airfield
        // and the other large monuments extend hundreds of metres from the saved
        // prefab position. A single fixed centre distance therefore allowed small
        // roadside monuments to be inserted inside a large monument. These radii
        // are conservative horizontal footprints used only by Raven additions.
        private static float CandidateFootprintRadius(string id, float fallback)
        {
            string value = (id ?? string.Empty).ToLowerInvariant();
            if (value == "launch_site") return 380f;
            if (value == "airfield") return 330f;
            if (value == "excavator") return 300f;
            if (value == "military_tunnels") return 260f;
            if (value == "trainyard") return 250f;
            if (value == "powerplant" || value == "water_treatment") return 230f;
            if (value == "desert_military_base" || value == "arctic_research_base") return 210f;
            if (value == "nuclear_silo" || value == "apartment_complex" || value == "ziggurat") return 190f;
            if (value == "outpost" || value == "bandit_camp") return 180f;
            if (value == "harbor" || value == "ferry_terminal") return 170f;
            if (value == "junkyard" || value == "satellite_dish" || value == "dome") return 115f;
            if (value == "oilrig_large") return 150f;
            if (value == "oilrig_small" || value == "underwater_labs") return 110f;
            if (value == "fishing_village" || value == "lighthouse" || value == "stables") return 90f;
            if (value == "radtown" || value == "sewer_branch" || value == "warehouse") return 75f;
            if (value == "supermarket" || value == "gas_station") return 65f;
            if (value.EndsWith("_quarry")) return 80f;
            return Math.Max(55f, fallback);
        }

        private static float ExistingFootprintRadius(string path)
        {
            string p = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (p.Contains("launch_site")) return 380f;
            if (p.Contains("airfield")) return 330f;
            if (p.Contains("excavator")) return 300f;
            if (p.Contains("military_tunnel")) return 260f;
            if (p.Contains("trainyard")) return 250f;
            if (p.Contains("powerplant") || p.Contains("water_treatment")) return 230f;
            if (p.Contains("desert_military_base") || p.Contains("arctic_research_base")) return 210f;
            if (p.Contains("nuclear_missile_silo") || p.Contains("apartments_complex") || p.Contains("apartment_complex") || p.Contains("ziggurat")) return 190f;
            if (p.Contains("/compound") || p.Contains("bandit_town")) return 180f;
            if (p.Contains("/harbor/") || p.Contains("harbor_1") || p.Contains("harbor_2") || p.Contains("ferry_terminal")) return 170f;
            if (p.Contains("junkyard") || p.Contains("satellite_dish") || p.Contains("sphere_tank")) return 115f;
            if (p.Contains("oilrig_1")) return 150f;
            if (p.Contains("oilrig_2") || p.Contains("underwater_lab")) return 110f;
            if (p.Contains("fishing_village") || p.Contains("lighthouse") || p.Contains("stables") || p.Contains("ranch")) return 90f;
            if (p.Contains("radtown_1") || p.Contains("radtown_small_3") || p.Contains("warehouse")) return 75f;
            if (p.Contains("supermarket") || p.Contains("gas_station")) return 65f;
            if (p.Contains("mining_quarry")) return 80f;
            return 0f;
        }

        private static bool TryFindPlacement(GuaranteedRule rule, List<PrefabData> prefabs, Func<PrefabData, string> getPath, System.Random random, uint worldSize, out Vector3 result, out float yaw)
        {
            result = Vector3.zero;
            yaw = 0f;
            float half = Math.Max(500f, worldSize / 2f);
            string placement = (rule.placement ?? "land").ToLowerInvariant();
            float minDistance = Math.Max(80f, rule.minDistance);
            float flatRadius = Math.Max(10f, rule.flatRadius);
            float maxDelta = Math.Max(1f, rule.maxHeightDelta);
            float candidateRadius = CandidateFootprintRadius(rule.id, flatRadius);

            for (int attempt = 0; attempt < 1200; attempt++)
            {
                float x;
                float z;
                if (placement == "offshore" || placement == "underwater")
                {
                    double angle = random.NextDouble() * Math.PI * 2.0;
                    float radius = half + 180f + (float)random.NextDouble() * 420f;
                    x = (float)Math.Cos(angle) * radius;
                    z = (float)Math.Sin(angle) * radius;
                }
                else
                {
                    float range = half - Math.Max(160f, flatRadius * 2f);
                    x = ((float)random.NextDouble() * 2f - 1f) * range;
                    z = ((float)random.NextDouble() * 2f - 1f) * range;
                }

                float terrain = GetTerrainHeight(x, z);
                if (placement == "offshore" || placement == "underwater")
                {
                    float requiredDepth = placement == "underwater" ? 15f : 3f;
                    if (terrain > -requiredDepth) continue;
                }
                else
                {
                    // Rust procedural map sea level is Y=0. WaterMap.GetHeight is not a
                    // reliable sea-level query during this generation callback.
                    if (terrain < 1f) continue;
                    if (!IsFlatEnough(x, z, flatRadius, maxDelta)) continue;
                    if (placement == "coast" && !HasNearbyWater(x, z, flatRadius * 2.5f)) continue;
                }

                bool tooClose = prefabs.Any(p =>
                {
                    float dx = p.position.x - x;
                    float dz = p.position.z - z;
                    float existingRadius = ExistingFootprintRadius(getPath(p));
                    float required = existingRadius > 0f
                        ? Math.Max(minDistance, candidateRadius + existingRadius + 35f)
                        : minDistance;
                    return dx * dx + dz * dz < required * required;
                });
                if (tooClose) continue;

                float y = placement == "offshore" ? 0f : terrain;
                result = new Vector3(x, y, z);
                yaw = (float)(random.NextDouble() * 360.0);
                return true;
            }
            return false;
        }

        private static bool IsFlatEnough(float x, float z, float radius, float maxDelta)
        {
            float center = GetTerrainHeight(x, z);
            float[] values =
            {
                GetTerrainHeight(x + radius, z), GetTerrainHeight(x - radius, z),
                GetTerrainHeight(x, z + radius), GetTerrainHeight(x, z - radius),
                GetTerrainHeight(x + radius * 0.7f, z + radius * 0.7f),
                GetTerrainHeight(x - radius * 0.7f, z - radius * 0.7f),
            };
            return values.All(v => Math.Abs(v - center) <= maxDelta);
        }

        private static bool HasNearbyWater(float x, float z, float radius)
        {
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI * 2.0 / 12.0;
                float sx = x + (float)Math.Cos(a) * radius;
                float sz = z + (float)Math.Sin(a) * radius;
                if (GetTerrainHeight(sx, sz) <= 1f) return true;
            }
            return false;
        }

        private static float GetTerrainHeight(float x, float z)
        {
            return InvokeMapHeight("HeightMap", x, z, 0f);
        }

        private static float GetWaterHeight(float x, float z)
        {
            return InvokeMapHeight("WaterMap", x, z, 0f);
        }

        private static float InvokeMapHeight(string memberName, float x, float z, float fallback)
        {
            try
            {
                object map = GetStaticMember(typeof(TerrainMeta), memberName);
                if (map == null) return fallback;
                // TerrainHeightMap.GetHeight(float,float) expects normalized map UV,
                // not world-space X/Z. Passing world coordinates clamps sampling to
                // the ocean edge and makes every valid land candidate look submerged.
                float size = ReadWorldUInt("Size");
                if (size > 0f)
                {
                    MethodInfo uvMethod = map.GetType().GetMethod("GetHeight", InstanceFlags, null, new Type[] { typeof(float), typeof(float) }, null);
                    if (uvMethod != null)
                    {
                        float u = x / size + 0.5f;
                        float v = z / size + 0.5f;
                        return Convert.ToSingle(uvMethod.Invoke(map, new object[] { u, v }));
                    }
                }
                MethodInfo worldMethod = map.GetType().GetMethod("GetHeight", InstanceFlags, null, new Type[] { typeof(Vector3) }, null);
                if (worldMethod == null) return fallback;
                return Convert.ToSingle(worldMethod.Invoke(map, new object[] { new Vector3(x, 0f, z) }));
            }
            catch { return fallback; }
        }

        private static void WriteAudit(GuaranteedConfig config, GuaranteedAudit audit)
        {
            try
            {
                string path = config.auditPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    string dir = Path.GetFullPath(Path.Combine("HarmonyConfig", "RavenGuaranteedPlacementAudits"));
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "RavenGuaranteedPlacementAudit_" + audit.seed + ".json");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, JsonConvert.SerializeObject(audit, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogError("[Raven Guaranteed Placement] Audit write failed: " + ex);
            }
        }
    }
}
