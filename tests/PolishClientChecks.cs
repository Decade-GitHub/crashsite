using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class PolishClientChecks
{
    public static readonly IReadOnlyList<int> OriginalParts = new[] { 0, 1, 2, 3, 4, 5, 6 };
    private static Type Plugin;
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static void Set(string name, object value) { Plugin.GetField(name, PrivateStatic).SetValue(null, value); }
    private static object Call(string name, params object[] args) { return Plugin.GetMethod(name, PrivateStatic).Invoke(null, args); }
    private static void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS: " + name); }
    public sealed class ZoneFixture { public Dictionary<string, List<int>> Floors = new Dictionary<string, List<int>>(); }
    public sealed class BundleFixture
    {
        public ZoneFixture Value { get; private set; }
        private ZoneFixture _override;
        public BundleFixture(ZoneFixture value) { Value = value; _override = value; }
        public ZoneFixture Override { get { return _override; } }
    }
    public sealed class ConfigFixture
    {
        public Dictionary<string, BundleFixture> MapZones = new Dictionary<string, BundleFixture>();
        public object MapGeometries = new object();
    }
    public sealed class WorldFixture
    {
        public static WorldFixture Instance { get; set; }
        public string LocationId { get; set; }
    }
    public sealed class WaypointFixture
    {
        public string MapId, ZoneKey;
        public ConfigFixture Config;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public WaypointFixture(string mapId, string zoneKey, ConfigFixture config, object bots, object humans)
        { MapId = mapId; ZoneKey = zoneKey; Config = config; }
    }
    public static int Main(string[] args)
    {
        var spt = args[0];
        var probes = new[] { Path.Combine(spt,"BepInEx/core"), Path.Combine(spt,"EscapeFromTarkov_Data/Managed"), Path.Combine(spt,"BepInEx/plugins/spt") };
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            var name = new AssemblyName(request.Name).Name;
            if (name.EndsWith(".resources",StringComparison.Ordinal)) return null;
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == name);
            if (loaded != null) return loaded;
            foreach (var probe in probes) { var path = Path.Combine(probe,name+".dll"); if (File.Exists(path)) return Assembly.Load(File.ReadAllBytes(path)); }
            return null;
        };
        return Run(args[1]);
    }
    private static int Run(string clientPath)
    {
        Plugin = Assembly.LoadFrom(clientPath).GetType("Crashsite.Client.CrashsiteClientPlugin",true);
        var originalField = typeof(PolishClientChecks).GetField("OriginalParts");
        var thorax = new[] { 1 };
        var tracked = new object();
        Set("_realBodyPartsField", originalField);
        Set("_thoraxBodyParts", thorax);
        Set("_crashsiteToxin", tracked);
        Check(ReferenceEquals(Call("GetToxinBodyParts", tracked), thorax), "Tracked toxin receives only the thorax list");
        Check(ReferenceEquals(Call("GetToxinBodyParts", new object()), OriginalParts), "Other toxin receives the original full list");
        Set("_crashsiteToxin", null);
        Check(ReferenceEquals(Call("GetToxinBodyParts", tracked), OriginalParts), "No active Crashsite toxin leaves ordinary effects unchanged");
        var instruction = new CodeInstruction(OpCodes.Ldsfld, originalField);
        var label = new DynamicMethod("LabelFixture",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel();
        instruction.labels.Add(label);
        var patched = ((IEnumerable<CodeInstruction>)Call("ThoraxOnlyToxinTranspiler",new List<CodeInstruction> { instruction, new CodeInstruction(OpCodes.Ret) })).ToList();
        Check(patched.Count == 4 && patched[0].opcode == OpCodes.Ldarg_0 && patched[1].opcode == OpCodes.Call && patched[2].opcode == OpCodes.Castclass, "Body-part read becomes instance helper plus typed cast");
        Check(patched[0].labels.Contains(label), "Transpiler preserves branch labels");
        Check(RejectsReadCount(originalField,0) && RejectsReadCount(originalField,2), "Unexpected zero/duplicate body-part reads fail closed");
        foreach (var level in new[] { 20, 21, 22 })
        {
            var fixture = new { _menuOperation = new { iEftSession = new { Profile = new { Info = new { Level = level } } } } };
            var rejection = (string)Call("GetCrashsiteAccessRejection", fixture);
            Check(level < 21 ? rejection != null && rejection.Contains("Level 21") : rejection == null,
                "Client readiness boundary at PMC Level " + level);
        }
        Check(Call("GetCrashsiteAccessRejection", new object()) != null, "Missing client PMC level fails closed");
        Check(Call("GetCrashsiteAccessRejection", new { _menuOperation = new { iEftSession = new { Profile = new { Info = new { Level = "invalid" } } } } }) != null,
            "Malformed client PMC level fails closed");
        CheckOrbit();
        Console.WriteLine("Managed helper checks only; Unity/Harmony installation and gameplay remain pending.");
        return 0;
    }
    private static void CheckOrbit()
    {
        var originalValue = new ZoneFixture();
        originalValue.Floors["dorms"] = new List<int> { 0, 1, 2 };
        var config = new ConfigFixture();
        config.MapZones["bigmap"] = new BundleFixture(originalValue);
        config.MapZones["Woods"] = new BundleFixture(new ZoneFixture());
        var args = new object[] { "crashsite", "crashsite", config };
        Call("UseCustomsOrbit211ZoneKey", args);
        Check((string)args[0] == "crashsite" && (string)args[1] == "bigmap", "ORBIT keeps Crashsite map ID and reports Customs zone key");
        var scoped = (ConfigFixture)args[2];
        Check(!ReferenceEquals(scoped, config) && !ReferenceEquals(scoped.MapZones, config.MapZones) &&
            !ReferenceEquals(scoped.MapZones["bigmap"], config.MapZones["bigmap"]), "ORBIT zone overrides use independent config/dictionary/bundle");
        scoped.MapZones["bigmap"].Value.Floors["dorms"].Add(99);
        scoped.MapZones["bigmap"].Override.Floors["dorms"].Add(88);
        Check(originalValue.Floors["dorms"].SequenceEqual(new[] { 0, 1, 2 }), "Nested zone edits cannot mutate original Customs values/overrides");
        Check(ReferenceEquals(scoped.MapGeometries, config.MapGeometries) && ReferenceEquals(scoped.MapZones["Woods"], config.MapZones["Woods"]),
            "Read-only geometry source and unrelated zone config remain intact");
        var normal = new object[] { "bigmap", "bigmap", config };
        Call("UseCustomsOrbit211ZoneKey", normal);
        Check(ReferenceEquals(normal[2], config) && (string)normal[1] == "bigmap", "Ordinary Customs bypasses the ORBIT alias hook");
        var failed = new object[] { "crashsite", "crashsite", new ConfigFixture() };
        var before = failed[2];
        Call("UseCustomsOrbit211ZoneKey", failed);
        Check((string)failed[1] == "crashsite" && ReferenceEquals(failed[2], before), "Missing Customs data leaves ORBIT arguments unchanged");
        var harmony = new Harmony("crashsite.tests.orbit.constructor");
        var constructor = typeof(WaypointFixture).GetConstructors().Single();
        try
        {
            var replacement = harmony.Patch(constructor, new HarmonyMethod(Plugin.GetMethod("UseCustomsOrbit211ZoneKey", PrivateStatic)));
            var fixture = (WaypointFixture)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(WaypointFixture));
            replacement.Invoke(null, new object[] { fixture, "crashsite", "crashsite", config, null, null });
            Check(fixture.MapId == "crashsite" && fixture.ZoneKey == "bigmap" && !ReferenceEquals(fixture.Config,config),
                "Harmony-generated constructor wrapper applies zone alias and config copy");
        }
        finally { harmony.UnpatchSelf(); }
        Set("_gameWorldInstanceProperty", typeof(WorldFixture).GetProperty("Instance"));
        WorldFixture.Instance = new WorldFixture { LocationId = "crashsite" };
        foreach (var role in new[] { "bossKnight", "followerBigPipe", "followerBirdEye", "sectantPriest", "sectantWarrior" })
        {
            var bot = new { Profile = new { Info = new { Settings = new { Role = role } } } };
            var values = new object[] { bot, true };
            var runOriginal = (bool)Call("KeepCrashsiteRosterAwake", values);
            Check(!runOriginal && !(bool)values[1], "Crashsite ORBIT dormancy blocked for " + role);
        }
        var scav = new { Profile = new { Info = new { Settings = new { Role = "assault" } } } };
        Check((bool)Call("KeepCrashsiteRosterAwake", new object[] { scav, true }), "Ordinary Crashsite Scav dormancy remains with ORBIT");
        WorldFixture.Instance.LocationId = "bigmap";
        var goon = new { Profile = new { Info = new { Settings = new { Role = "bossKnight" } } } };
        Check((bool)Call("KeepCrashsiteRosterAwake", new object[] { goon, true }), "Customs Goons retain ORBIT's ordinary dormancy policy");
    }
    private static bool RejectsReadCount(FieldInfo field,int count)
    {
        try
        {
            var fixture = Enumerable.Range(0,count).Select(i => new CodeInstruction(OpCodes.Ldsfld,field)).ToList();
            ((IEnumerable<CodeInstruction>)Call("ThoraxOnlyToxinTranspiler",fixture)).ToList();
            return false;
        }
        catch (InvalidOperationException) { return true; }
    }
}
