using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Crashsite.Client
{

    [BepInPlugin("com.crashsite.m1.client", "Crashsite Client", PluginVersion)]
    public sealed partial class CrashsiteClientPlugin : BaseUnityPlugin
    {

        private const string PluginVersion = "1.0.0";
        private const string CrashsiteId = "crashsite";
        private const string CustomsId = "bigmap";
        private const int MinimumPlayerLevel = 21;
        private const string HandshakeRoute = "/crashsite/client/handshake";
        private const string HandshakeVersion = PluginVersion;
        private const string TransferRoute = "/crashsite/inventory/transfer";
        private const string TransferAckRoute = "/crashsite/inventory/ack";

        private static readonly TimeSpan CrashsiteNightTime = TimeSpan.FromHours(1);

        private const float CrashsiteToxinDamageScale = 0.5f;
        private Harmony _harmony;
        private static CrashsiteClientPlugin _instance;
        private static bool _awaitingCrashsiteReturn;
        private static bool _invokeOriginalLocalGameMatching;
        private static bool _handshakeAccepted;
        private static bool _abpsCapLogged;

        private static string _inventoryUntrustedReason;
        private static PropertyInfo _gameWorldInstanceProperty;
        private static bool _nightClockPatchInstalled;
        private bool _abpsCapPatchInstalled;
        private bool _orbitWaypointPatchInstalled;
        private bool _abpsBossTrackingPatchInstalled;
        private bool _orbitDormancyPatchInstalled;
        private bool _sainLocationPatchInstalled;
        private bool _waypointsNavmeshPatchInstalled;
        private static bool _abpsTrackingLogged;
        private static bool _orbitDormancyLogged;
        private static bool _sainLocationLogged;
        private static MethodInfo _sainSetLocation;

        private static object _crashsiteToxin;
        private static object _crashsiteToxinHealth;
        private static bool _toxinCureBlockLogged;
        private static bool _toxinPatchesInstalled;
        private static Type _intoxicationType;
        private static FieldInfo _realBodyPartsField;
        private static object _thoraxBodyParts;
        private float _nextToxinCheck;
        private static FieldInfo _sainFoundLocation;

        private static bool _halloweenWeatherPatchReady;
        private static bool _scavWarsPatchInstalled;

        private const int CrashsiteWeatherNodeSeconds = 600;
        private const int CrashsiteWeatherNodeCount = 36;

        [Conditional("CRASHSITE_DEBUG")]
        private static void DebugInfo(string message) => _instance?.Logger.LogInfo(message);

        private static void LogWarning(string message) => _instance?.Logger.LogWarning(PublicLogMessage(message));
        private static void LogError(string message) => _instance?.Logger.LogError(PublicLogMessage(message));
        private static string PublicLogMessage(string message)
        {
#if CRASHSITE_DEBUG
            return message;
#else
            return Regex.Replace(message, @"\bM\d+\s*:?\s+", "");
#endif
        }
        private void Awake()
        {
            _instance = this;
            _harmony = new Harmony("com.crashsite.m1.client");
            InstallStarterPatch();

            var selectionType = FindType("EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen");
            var applicationType = FindType("EFT.TarkovApplication");

            Patch(selectionType, "OnLocationSelected", null,
                nameof(OnLocationSelectedPostfix));
            Patch(FindType("EFT.UI.LocationButton"), "Show", null, nameof(CrashsiteLocationButtonPostfix));
            Patch(applicationType, "TryGetLocationById", null,
                nameof(TryGetLocationByIdPostfix));
            Patch(applicationType, "LoadLocations", null,
                nameof(LoadLocationsPostfix));
            Patch(applicationType, "LoadMapAndData", nameof(LoadMapAndDataPrefix), null);
            Patch(applicationType, "LocalGameMatching", nameof(LocalGameMatchingPrefix), null);
            Patch(applicationType, "OnGameEnd", nameof(OnGameEndPrefix), null);
            Patch(applicationType, "ShowSessionResult", nameof(ShowSessionResultPrefix), null);
            Patch(applicationType, "ComebackToMainMenu", null,
                nameof(ComebackToMainMenuPostfix));

            var localGameType = FindType("EFT.LocalGame");
            _nightClockPatchInstalled = Patch(localGameType?.BaseType, "InitWeather", null,
                nameof(InitWeatherPostfix));
            Patch(FindType("EFT.UI.Matchmaker.LocationConditionsPanel"), "Set", null,
                nameof(LocationConditionsPanelSetPostfix));

            _halloweenWeatherPatchReady = _nightClockPatchInstalled && CheckHalloweenWeatherMembers();

            if (!Patch(FindType("EFT.GameWorld"), "OnGameStarted", null, nameof(UnlockCrashsiteKeyedObjects)))
            {
                LogError("M11 keyless-doors patch was not installed; Crashsite doors keep their locks.");
            }

            _scavWarsPatchInstalled = Patch(FindType("EFT.BotsController"), "Init", nameof(ForceCrashsiteScavWarsPrefix), null);
            if (!_scavWarsPatchInstalled)
            {
                LogError("M10 Scav War patch was not installed; Crashsite uses the raid-settings option.");
            }

            _intoxicationType = FindType("EFT.HealthSystem.ActiveHealthController+Intoxication");
            var effectType = FindType("EFT.HealthSystem.ActiveHealthController+Effect");
            _toxinPatchesInstalled =
                Patch(FindType("EFT.GameWorld"), "OnGameStarted", null, nameof(ApplyCrashsiteToxinOnGameStarted)) &
                Patch(effectType, "ForceResidue", nameof(BlockCrashsiteToxinCure), null) &
                Patch(effectType, "ForceRemove", nameof(BlockCrashsiteToxinCure), null) &
                Patch(_intoxicationType, "Started", nameof(ScaleCrashsiteToxinPrefix), nameof(ScaleCrashsiteToxinPostfix)) &
                InstallThoraxOnlyToxin();

            TryPatchAbpsCap();
            TryPatchOrbitWaypoints();
            TryPatchM6BotModCompatibility();

            DebugInfo("Crashsite client active. Expected location ID: " + CrashsiteId +
                           "; Customs ID: " + CustomsId + ".");
            DebugInfo("M3 client version " + HandshakeVersion +
                           "; server handshake will run after the Crashsite catalog is loaded.");
        }

        private void Start()
        {

            TryPatchAbpsCap();
            TryPatchOrbitWaypoints();
            TryPatchM6BotModCompatibility();
        }

        private void TryPatchOrbitWaypoints()
        {
            if (_orbitWaypointPatchInstalled)
            {
                return;
            }

            var waypointType = FindOptionalType("Orbit.Systems.WaypointSystem");
            if (waypointType == null)
            {
                return;
            }

            var version = waypointType.Assembly.GetName().Version;
            if (!IsSupportedOrbitVersion(waypointType))
            {
                return;
            }

            if (version.ToString() == "2.1.1.612")
            {
                TryPatchOrbit211Waypoints(waypointType);
                return;
            }

            var constructor = waypointType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 4 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType.FullName == "Orbit.Config.WaypointConfig" &&
                           parameters[2].ParameterType.FullName == "EFT.BotsController";
                });
            var prefix = AccessTools.Method(typeof(CrashsiteClientPlugin), nameof(AddOrbitCustomsWaypointData));
            if (constructor == null || prefix == null)
            {
                LogError("M1 ORBIT waypoint constructor was not found; compatibility was not installed.");
                return;
            }

            try
            {
                _harmony.Patch(constructor, new HarmonyMethod(prefix));
                _orbitWaypointPatchInstalled = true;
                DebugInfo("M1 patched ORBIT WaypointSystem constructor for Crashsite Customs geometry.");
            }
            catch (Exception ex)
            {
                LogError("M1 ORBIT waypoint compatibility patch failed: " + ex);
            }
        }

        private static bool IsSupportedOrbitVersion(Type type)
        {
            var version = type.Assembly.GetName().Version?.ToString();
            if (version == "2.0.0.42986" || version == "2.1.1.612")
                return true;
            LogError("M16 ORBIT assembly version " + version +
                " is unverified (supported: 2.0.0.42986, 2.1.1.612); its Crashsite compatibility patch was not installed.");
            return false;
        }

        private void TryPatchOrbit211Waypoints(Type waypointType)
        {
            try
            {
                var constructor = waypointType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(candidate =>
                    {
                        var p = candidate.GetParameters();
                        return p.Length == 5 && p[0].ParameterType == typeof(string) &&
                            p[1].ParameterType == typeof(string) && p[2].ParameterType.FullName == "Orbit.Config.WaypointConfig" &&
                            p[3].ParameterType.FullName == "EFT.BotsController" &&
                            p[4].ParameterType.IsGenericType && p[4].ParameterType.GetGenericTypeDefinition() == typeof(List<>) &&
                            p[4].ParameterType.GetGenericArguments()[0].FullName == "EFT.Player";
                    });
                var configType = constructor?.GetParameters()[2].ParameterType;
                if (constructor == null || configType == null ||
                    AccessTools.Field(waypointType, "_mapId")?.FieldType != typeof(string) ||
                    AccessTools.Field(waypointType, "_zoneKey")?.FieldType != typeof(string) ||
                    AccessTools.DeclaredMethod(configType, "ResolveZones", new[] { typeof(string), typeof(string) }) == null ||
                    AccessTools.DeclaredMethod(configType, "ResolveGeometry", new[] { typeof(string), typeof(string) }) == null)
                    throw new MissingMemberException("ORBIT 2.1.1 waypoint/zone-key signature mismatch.");
                _harmony.Patch(constructor, new HarmonyMethod(AccessTools.Method(
                    typeof(CrashsiteClientPlugin), nameof(UseCustomsOrbit211ZoneKey))));
                _orbitWaypointPatchInstalled = true;
                DebugInfo("M16 ORBIT 2.1.1 waypoint hook installed: Crashsite keeps its map ID and uses Customs zone/floor data.");
            }
            catch (Exception ex)
            {
                LogError("M16 ORBIT 2.1.1 waypoint compatibility failed: " + ex);
            }
        }

        private static void UseCustomsOrbit211ZoneKey(string __0, ref string __1, ref object __2)
        {
            if (!string.Equals(__0, CrashsiteId, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {

                var scopedConfig = CloneOrbitCustomsConfig(__2);
                __1 = CustomsId;
                __2 = scopedConfig;
                DebugInfo("M16 ORBIT Crashsite zoneKey=bigmap; native floor reports use the Customs catalog; zone config is isolated.");
            }
            catch (Exception ex)
            {
                LogError("M16 ORBIT Crashsite zone/floor compatibility failed; original ORBIT configuration retained: " + ex);
            }
        }

        private static object CloneOrbitCustomsConfig(object config)
        {
            if (config == null)
                throw new InvalidOperationException("ORBIT waypoint configuration is unavailable.");
            var zoneField = AccessTools.Field(config.GetType(), "MapZones");
            var originalZones = zoneField?.GetValue(config) as IDictionary;
            if (originalZones == null || !originalZones.Contains(CustomsId))
                throw new InvalidOperationException("ORBIT Customs zone configuration is unavailable.");
            var originalBundle = originalZones[CustomsId];
            var clone = AccessTools.Method(typeof(object), "MemberwiseClone");
            var scopedConfig = clone.Invoke(config, null);
            var scopedZones = Activator.CreateInstance(originalZones.GetType()) as IDictionary;
            if (scopedZones == null || originalBundle == null)
                throw new InvalidOperationException("ORBIT Customs zone bundle cannot be copied.");
            foreach (DictionaryEntry entry in originalZones)
                scopedZones[entry.Key] = entry.Value;
            var scopedBundle = clone.Invoke(originalBundle, null);
            foreach (var fieldName in new[] { "<Value>k__BackingField", "_override" })
            {
                var field = AccessTools.Field(originalBundle.GetType(), fieldName);
                if (field == null)
                    throw new MissingFieldException(originalBundle.GetType().FullName, fieldName);
                var value = field.GetValue(originalBundle);
                field.SetValue(scopedBundle, value == null ? null : JToken.FromObject(value).ToObject(value.GetType()));
            }
            scopedZones[CustomsId] = scopedBundle;
            zoneField.SetValue(scopedConfig, scopedZones);

            return scopedConfig;
        }

        private static void AddOrbitCustomsWaypointData(string __0, object __1)
        {
            if (!string.Equals(__0, CrashsiteId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var zones = ReadMember(__1, "MapZones") as IDictionary;
                var geometries = ReadMember(ReadMember(__1, "MapGeometries"), "Value") as IDictionary;
                if (zones == null || geometries == null ||
                    !zones.Contains(CustomsId) || !geometries.Contains(CustomsId))
                {
                    throw new InvalidOperationException("ORBIT Customs waypoint zones or geometry are unavailable.");
                }

                if (!zones.Contains(CrashsiteId))
                {
                    var customsBundle = zones[CustomsId];
                    var defaultZone = ReadMember(customsBundle, "Value");
                    zones[CrashsiteId] = Activator.CreateInstance(
                        customsBundle.GetType(), "Maps/Zones/bigmap.json", defaultZone);
                }

                if (!geometries.Contains(CrashsiteId))
                {
                    var customsGeometry = geometries[CustomsId];
                    geometries[CrashsiteId] = Activator.CreateInstance(
                        customsGeometry.GetType(),
                        ReadMember(customsGeometry, "Min"),
                        ReadMember(customsGeometry, "Max"),
                        ReadMember(customsGeometry, "CellSize"));
                }

                if (_instance != null)
                {
                    DebugInfo(
                        "M1 ORBIT compatibility: Crashsite has independent Customs waypoint zones and geometry.");
                }
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 ORBIT waypoint compatibility failed: " + ex);
                }
            }
        }

        private void TryPatchAbpsCap()
        {
            if (_abpsCapPatchInstalled)
            {
                return;
            }

            var abpsCapPatch = FindOptionalType(
                "BotPlacementSystemClient.Patches.SetMaxBotCountPatch");
            if (abpsCapPatch != null)
            {
                var abpsVersion = abpsCapPatch.Assembly.GetName().Version;
                if (abpsVersion != null && abpsVersion.ToString() == "1.0.0.0")
                {
                    _abpsCapPatchInstalled = Patch(
                        abpsCapPatch, "PatchPostfix", nameof(SkipAbpsZeroCapForCrashsite), null);
                }
                else
                {
                    LogError("M1 ABPS client assembly version " + abpsVersion +
                                    " is unverified; bot-cap compatibility was not installed.");
                }
            }
        }

        private void OnDestroy()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }

        private static Type FindType(string fullName)
        {
            var type = FindOptionalType(fullName);
            if (type == null && _instance != null)
            {
                LogError("M1 target type missing: " + fullName);
            }
            return type;
        }

        private static Type FindOptionalType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool SkipAbpsZeroCapForCrashsite()
        {
            try
            {
                if (_gameWorldInstanceProperty == null)
                {

                    var gameWorldType = FindOptionalType("EFT.GameWorld");
                    var singletonType = FindOptionalType("Comfort.Common.Singleton`1");
                    if (gameWorldType == null || singletonType == null)
                    {
                        return true;
                    }

                    _gameWorldInstanceProperty = singletonType.MakeGenericType(gameWorldType).GetProperty(
                        "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                var instance = _gameWorldInstanceProperty?.GetValue(null, null);
                if (!string.Equals(ReadString(instance, "LocationId"), CrashsiteId,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!_abpsCapLogged && _instance != null)
                {
                    _abpsCapLogged = true;
                    DebugInfo(
                        "M1 ABPS compatibility: skipped its unknown-map zero bot cap for Crashsite.");
                }
                return false;
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 ABPS bot-cap compatibility failed: " + ex);
                }
                return true;
            }
        }

        private static void ApplyCrashsiteToxinOnGameStarted(object __instance)
        {
            try
            {
                _crashsiteToxin = null;
                _crashsiteToxinHealth = null;
                if (!string.Equals(ReadString(__instance, "LocationId"), CrashsiteId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _starterWorldStarted = true;
                var health = ReadMember(ReadMember(__instance, "MainPlayer"), "ActiveHealthController");
                if (health == null)
                {
                    LogError("M7 Crashsite toxin: the main player has no ActiveHealthController.");
                    return;
                }

                _crashsiteToxinHealth = health;
                AddCrashsiteToxin("raid start");
            }
            catch (Exception ex)
            {
                LogError("M7 Crashsite toxin could not be applied: " + ex);
            }
        }

        private static void UnlockCrashsiteKeyedObjects(object __instance)
        {
            try
            {
                if (!string.Equals(ReadString(__instance, "LocationId"), CrashsiteId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var objectType = FindType("EFT.Interactive.WorldInteractiveObject");
                var stateType = FindType("EFT.Interactive.EDoorState");
                var switchType = FindType("EFT.Interactive.Switch");
                var doorSwitchType = FindType("EFT.Interactive.DoorSwitch");
                var enumerate = AccessTools.Method(__instance.GetType(), "WorldInteractiveObjects", Type.EmptyTypes);
                var setState = AccessTools.Method(objectType, "SetDoorState", new[] { stateType, typeof(bool) });

                var doorState = objectType?.GetProperty("DoorState", BindingFlags.Instance | BindingFlags.Public,
                    null, stateType, Type.EmptyTypes, null);
                if (objectType == null || stateType == null || enumerate == null || setState == null || doorState == null)
                {
                    throw new MissingMethodException("GameWorld.WorldInteractiveObjects or WorldInteractiveObject.SetDoorState/DoorState");
                }

                var locked = Enum.Parse(stateType, "Locked");
                var shut = Enum.Parse(stateType, "Shut");
                var unlocked = new Dictionary<string, int>();
                var skipped = 0;

                IEnumerable<object> candidates;
                if (ReadMember(__instance, "World") != null)
                {
                    candidates = ((IEnumerable)enumerate.Invoke(__instance, null)).Cast<object>().ToList();
                }
                else
                {
                    candidates = UnityEngine.Object.FindObjectsOfType(objectType).Cast<object>().ToList();
                }

                foreach (var item in candidates)
                {
                    if (item == null || !Equals(doorState.GetValue(item, null), locked))
                    {
                        continue;
                    }

                    var type = item.GetType();
                    if (string.IsNullOrEmpty(ReadMember(item, "KeyId") as string) ||
                        (switchType != null && switchType.IsAssignableFrom(type)) ||
                        (doorSwitchType != null && doorSwitchType.IsAssignableFrom(type)))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        setState.Invoke(item, new[] { shut, (object)true });
                        unlocked[type.Name] = unlocked.TryGetValue(type.Name, out var count) ? count + 1 : 1;
                    }
                    catch (Exception ex)
                    {
                        LogWarning("M11 could not unlock " + type.Name + " " +
                                                     ReadString(item, "Id") + ": " + ex.GetBaseException().Message);
                    }
                }

                DebugInfo("M11 Crashsite unlocked " + unlocked.Values.Sum() + " keyed objects (" +
                                          string.Join(", ", unlocked.Select(pair => pair.Key + " " + pair.Value).ToArray()) +
                                          "); " + skipped + " locked objects without a key or driven by a switch left as they are.");
            }
            catch (Exception ex)
            {
                LogError("M11 Crashsite door unlock failed; doors keep their locks: " + ex);
            }
        }

        private bool InstallThoraxOnlyToxin()
        {
            try
            {
                _realBodyPartsField = AccessTools.Field(FindType("EFT.HealthSystem.HealthHelper"), "RealBodyParts");
                var bodyPartType = FindType("EBodyPart");
                var started = AccessTools.DeclaredMethod(_intoxicationType, "Started", Type.EmptyTypes);
                var bleed = AccessTools.DeclaredMethod(_intoxicationType, "BleedHealth", Type.EmptyTypes);
                if (_realBodyPartsField == null || !_realBodyPartsField.IsStatic || bodyPartType == null ||
                    started == null || bleed == null || started.ReturnType != typeof(void) || bleed.ReturnType != typeof(void))
                    throw new MissingMemberException("SPT 4.1.6 Intoxication body-part hooks are unavailable.");

                var thorax = Array.CreateInstance(bodyPartType, 1);
                thorax.SetValue(Enum.Parse(bodyPartType, "Chest"), 0);
                if (!_realBodyPartsField.FieldType.IsInstanceOfType(thorax))
                    throw new InvalidOperationException("Unexpected RealBodyParts list type.");
                _thoraxBodyParts = thorax;
                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(CrashsiteClientPlugin), nameof(ThoraxOnlyToxinTranspiler)));
                _harmony.Patch(started, transpiler: transpiler);
                _harmony.Patch(bleed, transpiler: transpiler);
                DebugInfo("M13 thorax-only toxin hooks installed (Started and BleedHealth).");
                return true;
            }
            catch (Exception ex)
            {
                LogError("M13 thorax-only toxin hooks failed; Crashsite readiness is blocked: " + ex);
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> ThoraxOnlyToxinTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            if (code.Count(instruction => instruction.opcode == OpCodes.Ldsfld &&
                Equals(instruction.operand, _realBodyPartsField)) != 1)
                throw new InvalidOperationException("Expected exactly one RealBodyParts read in the SPT 4.1.6 toxin method.");
            foreach (var instruction in code)
            {
                if (instruction.opcode != OpCodes.Ldsfld || !Equals(instruction.operand, _realBodyPartsField))
                {
                    yield return instruction;
                    continue;
                }

                instruction.opcode = OpCodes.Ldarg_0;
                instruction.operand = null;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(CrashsiteClientPlugin), nameof(GetToxinBodyParts)));
                yield return new CodeInstruction(OpCodes.Castclass, _realBodyPartsField.FieldType);
            }
        }

        private static object GetToxinBodyParts(object effect) =>
            _crashsiteToxin != null && ReferenceEquals(effect, _crashsiteToxin)
                ? _thoraxBodyParts : _realBodyPartsField.GetValue(null);

        private static void AddCrashsiteToxin(string reason)
        {
            var health = _crashsiteToxinHealth;
            if (health == null || _intoxicationType == null)
            {
                return;
            }

            var addEffect = health.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "AddEffect" && method.IsGenericMethodDefinition &&
                                          method.GetParameters().Length == 6);
            var bodyPart = _intoxicationType.GetProperty("DefaultBodyPart", BindingFlags.Static | BindingFlags.Public)
                ?.GetValue(null, null);
            if (addEffect == null || bodyPart == null)
            {
                throw new MissingMethodException(health.GetType().FullName, "AddEffect<Intoxication>");
            }

            _crashsiteToxin = addEffect.MakeGenericMethod(_intoxicationType)
                .Invoke(health, new[] { bodyPart, null, null, null, null, null });
            DebugInfo("M7 Crashsite toxin applied (" + reason + "): " +
                                      (_crashsiteToxin == null ? "<not created>" : "state " + ReadString(_crashsiteToxin, "State")) + ".");
        }

        private static bool BlockCrashsiteToxinCure(object __instance)
        {
            if (_crashsiteToxin == null || !ReferenceEquals(__instance, _crashsiteToxin))
            {
                return true;
            }

            if (!_toxinCureBlockLogged)
            {
                _toxinCureBlockLogged = true;
                DebugInfo("M7 Crashsite toxin resisted a cure attempt.");
            }

            return false;
        }

        private static void ScaleCrashsiteToxinPrefix(object __instance, ref float __state)
        {
            __state = float.NaN;
            if (_crashsiteToxin == null || !ReferenceEquals(__instance, _crashsiteToxin))
            {
                return;
            }

            var settings = ReadMember(__instance, "Settings");
            var field = settings?.GetType().GetField("DamageHealth", BindingFlags.Instance | BindingFlags.Public);
            if (field == null)
            {
                LogError("M7 Crashsite toxin: DamageHealth setting not found; vanilla damage applies.");
                return;
            }

            __state = (float)field.GetValue(settings);
            field.SetValue(settings, __state * CrashsiteToxinDamageScale);
        }

        private static void ScaleCrashsiteToxinPostfix(object __instance, float __state)
        {
            if (float.IsNaN(__state))
            {
                return;
            }

            var settings = ReadMember(__instance, "Settings");
            settings?.GetType().GetField("DamageHealth", BindingFlags.Instance | BindingFlags.Public)?.SetValue(settings, __state);
            DebugInfo("M7 Crashsite toxin started at " + CrashsiteToxinDamageScale +
                                      "x vanilla damage per tick (vanilla setting " + __state + ", Immunity skill applied).");
        }

        private void Update()
        {
            if (_crashsiteToxinHealth == null || UnityEngine.Time.time < _nextToxinCheck)
            {
                return;
            }

            _nextToxinCheck = UnityEngine.Time.time + 3f;
            try
            {
                if (!Convert.ToBoolean(ReadMember(_crashsiteToxinHealth, "IsAlive") ?? false))
                {
                    return;
                }

                var state = _crashsiteToxin == null ? 0 : Convert.ToInt32(ReadMember(_crashsiteToxin, "State"));

                if (_crashsiteToxin == null || state == 0 || state == 3 || state == 4)
                {
                    AddCrashsiteToxin("re-applied after removal");
                }
            }
            catch (Exception ex)
            {
                LogError("M7 Crashsite toxin check failed: " + ex);
                _crashsiteToxinHealth = null;
            }
        }

        private void TryPatchM6BotModCompatibility()
        {

            if (!_abpsBossTrackingPatchInstalled)
            {
                var tracking = FindOptionalType("BotPlacementSystemClient.Patches.BossProgressiveRegressivePatch");
                if (tracking != null && IsExactVersion(tracking, "1.0.0.0", "ABPS"))
                {
                    _abpsBossTrackingPatchInstalled = Patch(tracking, "PatchPostfix",
                        nameof(SkipAbpsBossTrackingForCrashsite), null);
                }
            }

            if (!_orbitDormancyPatchInstalled)
            {
                var dormancy = FindOptionalType("Orbit.Systems.DormancySystem");
                if (dormancy != null && IsSupportedOrbitVersion(dormancy))
                {
                    var method = AccessTools.DeclaredMethod(dormancy, "IsDefaultDormant",
                        new[] { FindType("EFT.BotOwner") });
                    if (method != null && method.ReturnType == typeof(bool))
                        _orbitDormancyPatchInstalled = Patch(dormancy, "IsDefaultDormant",
                            nameof(KeepCrashsiteRosterAwake), null);
                    else
                        LogError("M16 ORBIT IsDefaultDormant(BotOwner) signature mismatch; roster dormancy protection was not installed.");
                }
            }

            if (!_sainLocationPatchInstalled)
            {
                var location = FindOptionalType("SAIN.Components.LocationClass");
                if (location != null && IsExactVersion(location, "4.5.1.0", "SAIN"))
                {
                    _sainSetLocation = AccessTools.PropertySetter(location, "Location");
                    _sainFoundLocation = AccessTools.Field(location, "_foundLocation");
                    _sainLocationPatchInstalled = _sainSetLocation != null &&
                        Patch(location, "findLocation", null, nameof(UseCustomsSainLocation));
                }
            }

            if (!_waypointsNavmeshPatchInstalled)
            {
                var waypoints = FindOptionalType("DrakiaXYZ.Waypoints.Patches.WaypointPatch");
                if (waypoints != null && IsExactVersion(waypoints, "1.9.0.0", "Waypoints"))
                {
                    _waypointsNavmeshPatchInstalled = Patch(waypoints, "InjectNavmesh",
                        nameof(InjectCustomsNavmeshForCrashsite), null);
                }
            }
        }

        private bool IsExactVersion(Type type, string expected, string modName)
        {
            var version = type.Assembly.GetName().Version;
            if (version != null && version.ToString() == expected)
            {
                return true;
            }

            LogError("M6 " + modName + " assembly version " + version + " is unverified (expected " +
                            expected + "); its Crashsite compatibility patch was not installed.");
            return false;
        }

        private static bool IsCrashsiteRaid()
        {
            if (_gameWorldInstanceProperty == null)
            {
                var gameWorldType = FindOptionalType("EFT.GameWorld");
                var singletonType = FindOptionalType("Comfort.Common.Singleton`1");
                if (gameWorldType == null || singletonType == null)
                {
                    return false;
                }

                _gameWorldInstanceProperty = singletonType.MakeGenericType(gameWorldType).GetProperty(
                    "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            var instance = _gameWorldInstanceProperty?.GetValue(null, null);
            return string.Equals(ReadString(instance, "LocationId"), CrashsiteId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SkipAbpsBossTrackingForCrashsite()
        {
            try
            {
                if (!IsCrashsiteRaid())
                {
                    return true;
                }

                if (!_abpsTrackingLogged)
                {
                    _abpsTrackingLogged = true;
                    DebugInfo("M6 ABPS compatibility: Crashsite boss spawns are not recorded in ABPS progressive boss tracking.");
                }

                return false;
            }
            catch (Exception ex)
            {
                LogError("M6 ABPS boss-tracking compatibility failed: " + ex);
                return true;
            }
        }

        private static readonly HashSet<string> OrbitAwakeRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bossKnight", "followerBigPipe", "followerBirdEye", "sectantPriest", "sectantWarrior",
        };

        private static bool KeepCrashsiteRosterAwake(object __0, ref bool __result)
        {
            try
            {
                if (!IsCrashsiteRaid())
                {
                    return true;
                }

                var roleName = ReadMember(ReadMember(ReadMember(ReadMember(__0, "Profile"), "Info"), "Settings"), "Role")?.ToString();
                if (roleName == null || !OrbitAwakeRoles.Contains(roleName))
                {
                    return true;
                }

                if (!_orbitDormancyLogged)
                {
                    _orbitDormancyLogged = true;
                    DebugInfo("M6 ORBIT compatibility: Crashsite Goons and cultists are kept out of dormancy (first: " + roleName + ").");
                }

                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                LogError("M6 ORBIT dormancy compatibility failed: " + ex);
                return true;
            }
        }

        private static void UseCustomsSainLocation(object __instance)
        {
            try
            {
                if (!IsCrashsiteRaid())
                {
                    return;
                }

                var current = ReadMember(__instance, "Location");
                if (current == null || Convert.ToInt32(current) != 0)
                {
                    return;
                }

                var customs = Enum.ToObject(current.GetType(), 3);
                _sainSetLocation.Invoke(__instance, new[] { customs });
                _sainFoundLocation?.SetValue(__instance, true);
                if (!_sainLocationLogged)
                {
                    _sainLocationLogged = true;
                    DebugInfo("M6 SAIN compatibility: Crashsite uses SAIN location settings " + customs + ".");
                }
            }
            catch (Exception ex)
            {
                LogError("M6 SAIN location compatibility failed: " + ex);
            }
        }

        private static bool InjectCustomsNavmeshForCrashsite(object __0)
        {
            try
            {
                if (!string.Equals(ReadString(__0, "LocationId"), CrashsiteId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var pluginType = FindOptionalType("DrakiaXYZ.Waypoints.WaypointsPlugin");
                var folder = AccessTools.Field(pluginType, "NavMeshFolder")?.GetValue(null) as string;
                var bundlePath = folder == null ? null : System.IO.Path.Combine(folder, CustomsId + "-navmesh.bundle");
                if (bundlePath == null || !System.IO.File.Exists(bundlePath))
                {
                    LogWarning("M6 Waypoints compatibility: Customs navmesh bundle not found; Crashsite keeps the vanilla navmesh.");
                    return true;
                }

                var assetBundleType = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule");
                var navMeshDataType = Type.GetType("UnityEngine.AI.NavMeshData, UnityEngine.AIModule");
                var navMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");
                var load = assetBundleType?.GetMethod("LoadFromFile", new[] { typeof(string) });
                var loadAll = assetBundleType?.GetMethod("LoadAllAssets", new[] { typeof(Type) });
                var unload = assetBundleType?.GetMethod("Unload", new[] { typeof(bool) });
                var removeAll = navMeshType?.GetMethod("RemoveAllNavMeshData", Type.EmptyTypes);
                var add = navMeshDataType == null ? null : navMeshType?.GetMethod("AddNavMeshData", new[] { navMeshDataType });
                if (load == null || loadAll == null || unload == null || removeAll == null || add == null)
                {
                    LogWarning("M6 Waypoints compatibility: Unity navmesh APIs not found; Crashsite keeps the vanilla navmesh.");
                    return true;
                }

                var bundle = load.Invoke(null, new object[] { bundlePath });
                if (bundle == null)
                {
                    LogWarning("M6 Waypoints compatibility: could not load " + bundlePath + ".");
                    return true;
                }

                try
                {
                    var assets = loadAll.Invoke(bundle, new object[] { navMeshDataType }) as Array;
                    var navMeshData = assets != null && assets.Length > 0 ? assets.GetValue(0) : null;
                    if (navMeshData == null || !navMeshDataType.IsInstanceOfType(navMeshData))
                    {
                        LogWarning("M6 Waypoints compatibility: Customs bundle has no NavMeshData.");
                        return true;
                    }

                    removeAll.Invoke(null, null);
                    add.Invoke(null, new[] { navMeshData });
                }
                finally
                {
                    unload.Invoke(bundle, new object[] { false });
                }

                DebugInfo("M6 Waypoints compatibility: Crashsite uses the Customs custom navmesh.");
                return false;
            }
            catch (Exception ex)
            {
                LogError("M6 Waypoints navmesh compatibility failed: " + ex);
                return true;
            }
        }

        private bool Patch(Type targetType, string targetName, string prefixName, string postfixName)
        {
            if (targetType == null)
            {
                return false;
            }

            var original = AccessTools.Method(targetType, targetName);
            if (original == null)
            {
                LogError("M1 target method missing: " + targetType.FullName + "." + targetName);
                return false;
            }

            try
            {
                var patchType = typeof(CrashsiteClientPlugin);
                var prefix = prefixName == null ? null :
                    new HarmonyMethod(AccessTools.Method(patchType, prefixName));
                var postfix = postfixName == null ? null :
                    new HarmonyMethod(AccessTools.Method(patchType, postfixName));
                _harmony.Patch(original, prefix, postfix);
                DebugInfo("M1 patched " + original.DeclaringType.FullName + "." + original.Name);
                return true;
            }
            catch (Exception ex)
            {
                LogError("M1 patch failed for " + original.DeclaringType.FullName + "." +
                                original.Name + ": " + ex);
                return false;
            }
        }

        private static void CrashsiteLocationButtonPostfix(object __instance, object __0, int playerLevel)
        {
            if (!string.Equals(ReadString(__0, "Id"), CrashsiteId, StringComparison.OrdinalIgnoreCase))
                return;

            var border = ReadMember(__instance, "_secretBorder") as UnityEngine.Component;
            if (border != null)
                border.gameObject.SetActive(true);
            else
                LogWarning("M15 LocationButton secret border is missing; Crashsite uses the default outline.");
            if (playerLevel < MinimumPlayerLevel)
            {
                var text = ReadMember(__instance, "_infoText");
                var textProperty = text?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                textProperty?.SetValue(text, "Requires PMC Level 21", null);
            }
        }

        private static void OnLocationSelectedPostfix(object __0)
        {
            try
            {
                var id = ReadString(__0, "Id");
                if (!IsRelevant(id) || _instance == null)
                {
                    return;
                }

                var scene = ReadMember(__0, "Scene");
                DebugInfo("M1 selected location Id=" + id +
                                         " MongoId=" + ReadString(__0, "_Id") +
                                         " Name=" + ReadString(__0, "Name") +
                                         " Scene=" + DescribeScene(scene));
                if (string.Equals(id, CrashsiteId, StringComparison.OrdinalIgnoreCase))
                {
                    _ = SendHandshakeAsync("selection");
                }
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 selection probe failed: " + ex);
                }
            }
        }

        private static void TryGetLocationByIdPostfix(string __0, bool __result)
        {
            if (IsRelevant(__0) && _instance != null)
            {
                DebugInfo("M1 TryGetLocationById(" + __0 + ")=" + __result);
            }
        }

        private static void LoadLocationsPostfix(object __instance, Task __result)
        {
            if (__result == null)
            {
                return;
            }

            __result.ContinueWith(task =>
            {
                try
                {
                    if (_instance == null)
                    {
                        return;
                    }

                    if (task.IsFaulted || task.IsCanceled)
                    {
                        LogError("M1 LoadLocations did not complete: " + task.Exception);
                        return;
                    }

                    var method = AccessTools.Method(__instance.GetType(), "TryGetLocationById");
                    if (method == null)
                    {
                        LogError("M1 cannot verify server location catalog: TryGetLocationById missing.");
                        return;
                    }

                    var args = new object[] { CrashsiteId, null };
                    var found = (bool)method.Invoke(__instance, args);
                    if (!found)
                    {
                        LogError("M1 server location catalog has no " + CrashsiteId +
                                                  ". Install the matching server mod; Crashsite cannot be selected.");
                        return;
                    }

                    var scene = ReadMember(args[1], "Scene");
                    DebugInfo("M1 server location catalog contains " + CrashsiteId +
                                             " MongoId=" + ReadString(args[1], "_Id") +
                                             " Scene=" + DescribeScene(scene));
                    if (!string.Equals(ReadString(scene, "path"), "maps/customs_preset.bundle",
                                       StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(ReadString(scene, "rcid"), "bigmap.scenespreset.asset",
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        LogError("M1 Crashsite scene does not match installed Customs scene.");
                    }
                    _ = SendHandshakeAsync("catalog load");
                }
                catch (Exception ex)
                {
                    if (_instance != null)
                    {
                        LogError("M1 location-catalog probe failed: " + ex);
                    }
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private static async Task SendHandshakeAsync(string trigger)
        {
            try
            {
                var response = await RequestHandler.PostJsonAsync(
                    HandshakeRoute, "{\"Version\":\"" + HandshakeVersion + "\"}");
                var data = JObject.Parse(response)["data"];
                var serverVersion = (string)data?["Version"] ?? (string)data?["version"];
                var accepted = (bool?)data?["Accepted"] ?? (bool?)data?["accepted"] ?? false;
                if (!accepted || !string.Equals(serverVersion, HandshakeVersion, StringComparison.Ordinal))
                {
                    _handshakeAccepted = false;
                    LogError("M3 client/server handshake rejected after " + trigger +
                                               ": client=" + HandshakeVersion +
                                               ", server=" + (serverVersion ?? "<missing>") +
                                               ". Install matching Crashsite DLLs.");
                    return;
                }

                _handshakeAccepted = true;
                DebugInfo("M3 client/server handshake accepted after " + trigger +
                                          "; version=" + serverVersion + ".");
            }
            catch (Exception ex)
            {
                _handshakeAccepted = false;
                LogError("M3 client/server handshake failed after " + trigger +
                                           ". Install the matching server DLL: " + ex);
            }
        }

        private static bool LocalGameMatchingPrefix(object __instance, object[] __args, ref Task __result)
        {
            if (_invokeOriginalLocalGameMatching)
            {
                return true;
            }

            if (_inventoryUntrustedReason != null)
            {

                NotifyPlayer(_inventoryUntrustedReason);
                __result = Task.FromException(new InvalidOperationException(_inventoryUntrustedReason));
                return false;
            }

            CleanupUnstartedStarter();

            var raidSettings = ReadMember(__instance, "_raidSettings") ??
                              ReadMember(__instance, "CurrentRaidSettings");
            var selectedLocation = ReadMember(raidSettings, "SelectedLocation");
            if (!string.Equals(ReadString(selectedLocation, "Id"), CrashsiteId,
                               StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var accessRejection = GetCrashsiteAccessRejection(__instance);
            if (accessRejection != null)
            {
                NotifyPlayer(accessRejection);
                __result = Task.FromException(new InvalidOperationException(accessRejection));
                return false;
            }

            if (!_starterPatchInstalled)
            {
                const string noKit = "The Crashsite starter-kit hook could not be installed; the raid was not started.";
                NotifyPlayer(noKit);
                __result = Task.FromException(new InvalidOperationException(noKit));
                return false;
            }

            if (!_toxinPatchesInstalled)
            {
                const string noToxin = "The Crashsite toxin could not be installed in this game build; the raid was not started.";
                NotifyPlayer(noToxin);
                __result = Task.FromException(new InvalidOperationException(noToxin));
                return false;
            }

            if (!_nightClockPatchInstalled)
            {
                const string noNightClock = "The Crashsite night clock could not be installed in this game build; the raid was not started.";
                NotifyPlayer(noNightClock);
                __result = Task.FromException(new InvalidOperationException(noNightClock));
                return false;
            }

            if (!Convert.ToBoolean(ReadMember(raidSettings, "Local") ?? false))
            {
                const string soloOnly = "Crashsite currently supports solo SPT raids only.";
                NotifyPlayer(soloOnly);
                __result = Task.FromException(new InvalidOperationException(soloOnly));
                return false;
            }

            __result = TransferCrashsiteInventoryThenStartAsync(__instance, __args);
            return false;
        }

        private static async Task TransferCrashsiteInventoryThenStartAsync(object application, object[] matchingArgs)
        {
            var moves = new List<ClientMove>();

            var stateUnknown = false;
            var matchingStarted = false;
            try
            {
                await SendHandshakeAsync("Crashsite ready");
                if (!_handshakeAccepted)
                {
                    throw new InvalidOperationException(
                        "The matching Crashsite server is not ready. Install both Crashsite DLLs and try again.");
                }

                var inventory = GetClientInventory(application);
                var roots = GetClientTransferRoots(inventory);
                var rootIds = roots.Select(root => root.Id).ToArray();
                var stashGrid = ReadMember(ReadMember(inventory, "Stash"), "Grid");
                if (stashGrid == null)
                {
                    throw new InvalidOperationException("The client profile has no stash grid; Crashsite was not started.");
                }

                stateUnknown = true;
                try
                {
                    StageClientMoves(roots, stashGrid, moves);
                }
                catch
                {
                    RollbackClientMoves(moves);
                    stateUnknown = false;
                    throw;
                }

                var operationId = Guid.NewGuid().ToString("D");
                var requestBody = new JObject
                {
                    ["OperationId"] = operationId,
                    ["Location"] = CrashsiteId,
                    ["RootIds"] = new JArray(rootIds),
                }.ToString();

                string responseText;
                try
                {
                    responseText = await RequestHandler.PostJsonAsync(TransferRoute, requestBody);
                }
                catch (Exception firstFailure)
                {
                    try
                    {

                        responseText = await RequestHandler.PostJsonAsync(TransferRoute, requestBody);
                    }
                    catch (Exception retryFailure)
                    {
                        LogError(
                            "Crashsite M3 transfer response was unavailable after retry. First error: " +
                            firstFailure + " Retry error: " + retryFailure);
                        throw new InvalidOperationException(
                            "Crashsite could not confirm the inventory transfer with the server.", retryFailure);
                    }
                }

                var responseData = JObject.Parse(responseText)["data"];
                var accepted = (bool?)responseData?["Accepted"] ?? (bool?)responseData?["accepted"] ?? false;
                if (!accepted)
                {

                    RollbackClientMoves(moves);
                    stateUnknown = false;
                    var reason = (string)responseData?["Reason"] ?? (string)responseData?["reason"] ??
                                 "The server rejected the Crashsite inventory transfer.";
                    throw new InvalidOperationException(reason);
                }

                var serverRoots = responseData?["MovedRoots"] as JArray ??
                                  responseData?["movedRoots"] as JArray ?? new JArray();
                var serverIds = serverRoots
                    .Select(root => (string)root?["Id"] ?? (string)root?["id"])
                    .Where(id => id != null)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                if (!rootIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(serverIds, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The server accepted a different set of carried items than the client staged.");
                }

                var responseOperationId = (string)responseData?["OperationId"] ??
                                          (string)responseData?["operationId"];
                if (!string.Equals(responseOperationId, operationId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The server response did not match this Crashsite inventory transfer.");
                }

                var realigned = AlignClientStashWithServer(stashGrid, roots, serverRoots);
                stateUnknown = false;

                DebugInfo(
                    "Crashsite M3 inventory transfer accepted; moved " + rootIds.Length +
                    " carried item trees to stash before loading the raid" +
                    (realigned > 0 ? "; realigned " + realigned + " client stash positions to the server." : "."));
                try
                {
                    var ackBody = new JObject { ["OperationId"] = operationId }.ToString();
                    await RequestHandler.PostJsonAsync(TransferAckRoute, ackBody);
                }
                catch (Exception ackFailure)
                {
                    LogWarning("Crashsite M3 transfer acknowledgement failed; transfer remains committed: " + ackFailure);
                }

                var matchingMethod = AccessTools.Method(application.GetType(), "LocalGameMatching");
                if (matchingMethod == null)
                {
                    throw new MissingMethodException("EFT.TarkovApplication.LocalGameMatching was not found after the transfer.");
                }

                Task matchingTask;
                matchingStarted = true;
                _invokeOriginalLocalGameMatching = true;
                try
                {
                    matchingTask = (Task)matchingMethod.Invoke(application, matchingArgs);
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    throw exception.InnerException;
                }
                finally
                {
                    _invokeOriginalLocalGameMatching = false;
                }

                await matchingTask;
            }
            catch (Exception exception)
            {
                if (stateUnknown)
                {
                    _inventoryUntrustedReason =
                        "Crashsite could not confirm that the client inventory matches the server profile. " +
                        "Raids are blocked until you restart the game so the profile reloads from the server. " +
                        "The server copy of your items was not deleted.";
                }

                CleanupUnstartedStarter();
                LogError("Crashsite M3 ready-time transfer/start failed: " + exception);
                if (!matchingStarted)
                {
                    NotifyPlayer(_inventoryUntrustedReason ?? exception.Message);
                }

                throw;
            }
        }

        private static void NotifyPlayer(string message)
        {
            try
            {
                var managerType = FindOptionalType("EFT.Communications.NotificationManager");
                var durationType = FindOptionalType("EFT.Communications.ENotificationDurationType");
                var display = managerType == null || durationType == null ? null :
                    managerType.GetMethod("DisplayWarningNotification", BindingFlags.Public | BindingFlags.Static,
                                          null, new[] { typeof(string), durationType }, null);
                if (display == null)
                {
                    LogWarning("Crashsite could not show an in-game notification: " + message);
                    return;
                }

                var duration = Enum.IsDefined(durationType, "Long")
                    ? Enum.Parse(durationType, "Long")
                    : Enum.ToObject(durationType, 0);
                display.Invoke(null, new[] { "Crashsite: " + message, duration });
            }
            catch (Exception ex)
            {
                LogWarning("Crashsite in-game notification failed: " + ex);
            }
        }

        private static string GetCrashsiteAccessRejection(object application)
        {
            try
            {
                var session = ReadMember(ReadMember(application, "_menuOperation"), "iEftSession");
                var levelValue = ReadMember(ReadMember(ReadMember(session, "Profile"), "Info"), "Level");
                if (levelValue == null)
                    return "The active PMC level could not be verified; Crashsite was not started.";
                var level = Convert.ToInt32(levelValue);
                return level < MinimumPlayerLevel
                    ? "Crashsite requires PMC Level 21 (current level: " + level + ")." : null;
            }
            catch (Exception ex)
            {
                LogError("M14 PMC level check failed: " + ex);
                return "The active PMC level could not be verified; Crashsite was not started.";
            }
        }

        private static object GetClientInventory(object application)
        {
            var menuOperation = ReadMember(application, "_menuOperation");
            var session = ReadMember(menuOperation, "iEftSession");
            var profile = ReadMember(session, "Profile");
            var inventory = ReadMember(profile, "Inventory");
            return inventory ?? throw new InvalidOperationException(
                "The active client PMC inventory could not be found; Crashsite was not started.");
        }

        private static List<ClientRoot> GetClientTransferRoots(object inventory)
        {
            var equipment = ReadMember(inventory, "Equipment");
            var slots = ReadMember(equipment, "Slots") as IEnumerable;
            if (slots == null)
            {
                throw new InvalidOperationException("The active client equipment slots could not be read.");
            }

            var roots = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                if (slot == null)
                {
                    continue;
                }

                var slotId = ReadString(slot, "ID");
                var item = ReadMember(slot, "ContainedItem");
                if (item == null || IsRetainedClientSlot(slotId))
                {
                    continue;
                }

                if (IsContentsOnlyClientSlot(slotId))
                {
                    AddContainedClientRoots(roots, item);
                    continue;
                }

                AddClientRoot(roots, item);
            }

            return roots
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ClientRoot(pair.Key, pair.Value))
                .ToList();
        }

        private static void AddContainedClientRoots(IDictionary<string, object> roots, object containerItem)
        {
            if (ReadMember(containerItem, "Grids") is IEnumerable grids)
            {
                foreach (var grid in grids)
                {
                    if (!(ReadMember(grid, "Items") is IEnumerable items))
                    {
                        continue;
                    }

                    foreach (var item in items)
                    {
                        if (item != null)
                        {
                            AddClientRoot(roots, item);
                        }
                    }
                }
            }

            if (ReadMember(containerItem, "Slots") is IEnumerable childSlots)
            {
                foreach (var childSlot in childSlots)
                {
                    if (childSlot == null || IsRetainedChildSlot(ReadString(childSlot, "ID")))
                    {
                        continue;
                    }

                    var item = ReadMember(childSlot, "ContainedItem");
                    if (item != null)
                    {
                        AddClientRoot(roots, item);
                    }
                }
            }
        }

        private static void AddClientRoot(IDictionary<string, object> roots, object item)
        {
            var id = ReadString(item, "Id");
            if (string.IsNullOrWhiteSpace(id) || id == "<null>")
            {
                throw new InvalidOperationException("A carried item has no ID; Crashsite was not started.");
            }

            if (roots.ContainsKey(id))
            {
                throw new InvalidOperationException("The client inventory contains duplicate carried item roots.");
            }

            roots.Add(id, item);
        }

        private static bool IsRetainedClientSlot(string slotId) =>
            string.Equals(slotId, "Scabbard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(slotId, "Dogtag", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(slotId, "ArmBand", StringComparison.OrdinalIgnoreCase) ||
            IsSpecialSlot(slotId);

        private static bool IsContentsOnlyClientSlot(string slotId) =>
            string.Equals(slotId, "SecuredContainer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(slotId, "Pockets", StringComparison.OrdinalIgnoreCase);

        private static bool IsRetainedChildSlot(string slotId) => IsSpecialSlot(slotId);

        private static bool IsSpecialSlot(string slotId) =>
            slotId != null && slotId.StartsWith("SpecialSlot", StringComparison.OrdinalIgnoreCase);

        private static void StageClientMoves(
            IEnumerable<ClientRoot> roots,
            object stashGrid,
            ICollection<ClientMove> completedMoves)
        {
            foreach (var root in roots)
            {
                var sourceAddress = ReadMember(root.Item, "CurrentAddress");
                var sourceContainer = ReadMember(sourceAddress, "Container");
                if (sourceAddress == null || sourceContainer == null)
                {
                    throw new InvalidOperationException("A carried item has no valid source location; Crashsite was not started.");
                }

                var move = new ClientMove(root.Id, root.Item, sourceAddress, sourceContainer);
                completedMoves.Add(move);
                InvokeAddressAction(sourceAddress, "Remove", root.Item);
                if (ContainerHasItem(sourceContainer, root.Id))
                {
                    throw new InvalidOperationException("The client could not safely remove an item from its carried slot.");
                }

                if (!TryPlaceInFreeStashCell(stashGrid, root.Item))
                {
                    throw new InvalidOperationException(
                        "There is not enough stash space for all carried Crashsite items. No raid was started.");
                }
            }
        }

        private static bool TryPlaceInFreeStashCell(object stashGrid, object item)
        {
            var findArguments = new object[] { item, null };
            var findMethod = FindOutAddressMethod(stashGrid.GetType(), "TryFindLocationForItem", item);
            if (findMethod == null || !(bool)findMethod.Invoke(stashGrid, findArguments) || findArguments[1] == null)
            {
                return false;
            }

            return TryInvokeAddressAction(findArguments[1], "Add", item, out _) && IsInContainer(item, stashGrid);
        }

        private static void RollbackClientMoves(IReadOnlyList<ClientMove> moves)
        {
            for (var index = moves.Count - 1; index >= 0; index--)
            {
                var move = moves[index];
                if (ContainerHasItem(move.SourceContainer, move.Id))
                {
                    continue;
                }

                var currentAddress = ReadMember(move.Item, "CurrentAddress");
                var currentContainer = ReadMember(currentAddress, "Container");
                if (currentContainer != null && ContainerHasItem(currentContainer, move.Id))
                {
                    InvokeAddressAction(currentAddress, "Remove", move.Item);
                }

                InvokeAddressAction(move.SourceAddress, "Add", move.Item);
                if (!ContainerHasItem(move.SourceContainer, move.Id))
                {
                    throw new InvalidOperationException("The client could not restore its pre-raid inventory after a rejected transfer.");
                }
            }
        }

        private static int AlignClientStashWithServer(object stashGrid, IEnumerable<ClientRoot> roots, JArray serverRoots)
        {
            var cells = new Dictionary<string, StashCell>(StringComparer.Ordinal);
            foreach (var token in serverRoots)
            {
                var id = (string)token?["Id"] ?? (string)token?["id"];
                var x = (int?)token?["X"] ?? (int?)token?["x"];
                var y = (int?)token?["Y"] ?? (int?)token?["y"];
                var rotated = (bool?)token?["Rotation"] ?? (bool?)token?["rotation"] ?? false;
                if (id != null && x >= 0 && y >= 0)
                {
                    cells[id] = new StashCell(x.Value, y.Value, rotated);
                }
            }

            var misplaced = new List<KeyValuePair<ClientRoot, StashCell>>();
            foreach (var root in roots)
            {
                if (cells.TryGetValue(root.Id, out var cell) && !IsAtStashCell(root.Item, stashGrid, cell))
                {
                    misplaced.Add(new KeyValuePair<ClientRoot, StashCell>(root, cell));
                }
            }

            if (misplaced.Count == 0)
            {
                return 0;
            }

            foreach (var entry in misplaced)
            {
                InvokeAddressAction(ReadMember(entry.Key.Item, "CurrentAddress"), "Remove", entry.Key.Item);
            }

            foreach (var entry in misplaced)
            {
                var item = entry.Key.Item;
                var target = CreateStashAddress(stashGrid, entry.Value);
                if (target != null && TryInvokeAddressAction(target, "Add", item, out _) && IsInContainer(item, stashGrid))
                {
                    if (!IsAtStashCell(item, stashGrid, entry.Value))
                    {
                        LogWarning("Crashsite M3 placed " + entry.Key.Id +
                                                     " in stash, but not exactly on the server cell.");
                    }

                    continue;
                }

                LogWarning(
                    "Crashsite M3 could not use the server stash cell for " + entry.Key.Id +
                    "; it stays in the first free client cell until the profile reloads.");
                if (!TryPlaceInFreeStashCell(stashGrid, item))
                {
                    throw new InvalidOperationException(
                        "The client could not re-place a transferred item in its stash view.");
                }
            }

            return misplaced.Count;
        }

        private static bool IsAtStashCell(object item, object stashGrid, StashCell cell)
        {
            var address = ReadMember(item, "CurrentAddress");
            if (!ReferenceEquals(ReadMember(address, "Container"), stashGrid))
            {
                return false;
            }

            var location = ReadMember(address, "LocationInGrid");
            return location != null &&
                   Convert.ToInt32(ReadMember(location, "x")) == cell.X &&
                   Convert.ToInt32(ReadMember(location, "y")) == cell.Y &&
                   Convert.ToInt32(ReadMember(location, "r")) == (cell.Rotated ? 1 : 0);
        }

        private static object CreateStashAddress(object stashGrid, StashCell cell)
        {
            var gameAssembly = stashGrid.GetType().Assembly;
            var locationType = gameAssembly.GetType("LocationInGrid", false);
            var rotationType = gameAssembly.GetType("ItemRotation", false);
            var create = locationType == null ? null :
                AccessTools.Method(stashGrid.GetType(), "CreateItemAddress", new[] { locationType });
            if (rotationType == null || create == null)
            {
                return null;
            }

            var location = Activator.CreateInstance(
                locationType, cell.X, cell.Y, Enum.ToObject(rotationType, cell.Rotated ? 1 : 0));
            return create.Invoke(stashGrid, new[] { location });
        }

        private static bool IsInContainer(object item, object container) =>
            ReferenceEquals(ReadMember(ReadMember(item, "CurrentAddress"), "Container"), container);

        private static bool ContainerHasItem(object container, string itemId)
        {
            var items = ReadMember(container, "Items") as IEnumerable;
            if (items == null)
            {
                return false;
            }

            foreach (var item in items)
            {
                if (item != null && string.Equals(ReadString(item, "Id"), itemId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static MethodInfo FindOutAddressMethod(Type type, string name, object item)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return string.Equals(method.Name, name, StringComparison.Ordinal) &&
                           parameters.Length == 2 &&
                           parameters[0].ParameterType.IsInstanceOfType(item) &&
                           parameters[1].IsOut &&
                           parameters[1].ParameterType.GetElementType()?.FullName == "EFT.InventoryLogic.ItemAddress";
                });
        }

        private static void InvokeAddressAction(object address, string methodName, object item)
        {
            if (!TryInvokeAddressAction(address, methodName, item, out var error))
            {
                throw new InvalidOperationException(
                    "EFT rejected the Crashsite inventory " + methodName.ToLowerInvariant() + ": " + error);
            }
        }

        private static bool TryInvokeAddressAction(object address, string methodName, object item, out string error)
        {
            if (address == null)
            {
                error = "the item has no inventory address";
                return false;
            }

            var method = address.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return string.Equals(candidate.Name, methodName, StringComparison.Ordinal) &&
                           parameters.Length == 2 &&
                           parameters[0].ParameterType.IsInstanceOfType(item) &&
                           parameters[1].ParameterType == typeof(bool);
                });
            if (method == null)
            {
                throw new MissingMethodException(address.GetType().FullName, methodName);
            }

            var result = method.Invoke(address, new object[] { item, false });
            if (ReadMember(result, "Failed") is bool failed && failed)
            {
                error = ReadString(result, "Error");
                return false;
            }

            error = null;
            return true;
        }

        private sealed class ClientRoot
        {
            public ClientRoot(string id, object item)
            {
                Id = id;
                Item = item;
            }

            public string Id { get; }
            public object Item { get; }
        }

        private sealed class ClientMove
        {
            public ClientMove(string id, object item, object sourceAddress, object sourceContainer)
            {
                Id = id;
                Item = item;
                SourceAddress = sourceAddress;
                SourceContainer = sourceContainer;
            }

            public string Id { get; }
            public object Item { get; }
            public object SourceAddress { get; }
            public object SourceContainer { get; }
        }

        private struct StashCell
        {
            public StashCell(int x, int y, bool rotated)
            {
                X = x;
                Y = y;
                Rotated = rotated;
            }

            public int X { get; }
            public int Y { get; }
            public bool Rotated { get; }
        }

        private static void InitWeatherPostfix(object __instance)
        {
            try
            {
                if (!string.Equals(ReadString(ReadMember(__instance, "Location"), "Id"), CrashsiteId,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var gameDateTime = ReadMember(__instance, "GameDateTime");
                if (gameDateTime == null)
                {
                    throw new InvalidOperationException("The raid has no GameDateTime.");
                }

                var type = gameDateTime.GetType();
                var calculated = (DateTime)AccessTools.Method(type, "Calculate", Type.EmptyTypes).Invoke(gameDateTime, null);
                var realDateTime = (DateTime)ReadMember(gameDateTime, "StatedRealDateTime");
                var nightDateTime = calculated.Date + CrashsiteNightTime;
                var reset = AccessTools.Method(type, "Reset",
                    new[] { typeof(DateTime), typeof(DateTime), typeof(float), typeof(bool) });
                if (reset == null)
                {
                    throw new MissingMethodException(type.FullName, "Reset(DateTime, DateTime, float, bool)");
                }

                reset.Invoke(gameDateTime, new object[] { realDateTime, nightDateTime, 0f, true });
                AccessTools.Method(type, "Lock", Type.EmptyTypes)?.Invoke(gameDateTime, null);
                DebugInfo("M4 Crashsite raid clock frozen at " +
                                          ((DateTime)AccessTools.Method(type, "Calculate", Type.EmptyTypes)
                                              .Invoke(gameDateTime, null)).ToString("yyyy-MM-dd HH:mm:ss") +
                                          " (was " + calculated.ToString("HH:mm:ss") + ").");
            }
            catch (Exception ex)
            {
                LogError("M4 Crashsite night clock failed: " + ex);
            }

            if (string.Equals(ReadString(ReadMember(__instance, "Location"), "Id"), CrashsiteId,
                              StringComparison.OrdinalIgnoreCase))
            {
                ApplyCrashsiteHalloweenWeather();
            }
        }

        private static bool CheckHalloweenWeatherMembers()
        {
            var nodeType = FindType("EFT.Weather.WeatherNode");
            var controllerType = FindType("EFT.Weather.WeatherController");
            var ok = nodeType != null && controllerType != null &&
                     AccessTools.Method(nodeType, "GetHalloweenWeatherNode", new[] { typeof(int) }) != null &&
                     AccessTools.Method(nodeType, "GetRandomTestWeatherNodes", new[] { typeof(int), typeof(int) }) != null &&
                     AccessTools.Field(nodeType, "Time") != null &&
                     AccessTools.Field(controllerType, "Instance") != null &&
                     AccessTools.Method(controllerType, "SetWeatherNodes") != null;
            if (!ok)
            {
                LogError("M9 Halloween weather members were not found in this game build; " +
                                           "Crashsite will use normal weather.");
            }

            return ok;
        }

        private static void ApplyCrashsiteHalloweenWeather()
        {
            if (!_halloweenWeatherPatchReady)
            {
                return;
            }

            try
            {
                var nodeType = FindType("EFT.Weather.WeatherNode");
                var controllerType = FindType("EFT.Weather.WeatherController");
                var controller = AccessTools.Field(controllerType, "Instance").GetValue(null);
                if (controller == null)
                {
                    LogWarning("M9 no WeatherController in this scene; Halloween weather skipped.");
                    return;
                }

                var halloween = AccessTools.Method(nodeType, "GetHalloweenWeatherNode", new[] { typeof(int) })
                    .Invoke(null, new object[] { 0 });
                var nodes = (Array)AccessTools.Method(nodeType, "GetRandomTestWeatherNodes", new[] { typeof(int), typeof(int) })
                    .Invoke(null, new object[] { CrashsiteWeatherNodeSeconds, CrashsiteWeatherNodeCount });
                var timeField = AccessTools.Field(nodeType, "Time");
                var copiedFields = nodeType.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(field => field != timeField)
                    .ToArray();
                foreach (var node in nodes)
                {
                    foreach (var field in copiedFields)
                    {
                        field.SetValue(node, field.GetValue(halloween));
                    }
                }

                AccessTools.Method(controllerType, "SetWeatherNodes").Invoke(controller, new object[] { nodes });
                DebugInfo("M9 Crashsite Halloween weather applied: " + nodes.Length +
                                          " nodes, cloudness " + ReadMember(halloween, "Cloudness") +
                                          ", fog " + ReadMember(halloween, "ScaterringFogDensity") + ".");
            }
            catch (Exception ex)
            {
                LogError("M9 Crashsite Halloween weather failed; normal weather kept: " + ex);
            }
        }

        private static void ForceCrashsiteScavWarsPrefix(object botGame, ref bool freeForAll)
        {
            try
            {
                if (!string.Equals(ReadString(ReadMember(botGame, "Location"), "Id"), CrashsiteId,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var wasOn = freeForAll;
                freeForAll = true;
                DebugInfo("M10 Crashsite Scav War forced on (raid setting was " +
                                          (wasOn ? "on" : "off") + ").");
            }
            catch (Exception ex)
            {
                LogError("M10 Crashsite Scav War failed; the raid setting is used: " + ex);
            }
        }

        private static void LocationConditionsPanelSetPostfix(object __instance, object __1)
        {
            try
            {
                if (!string.Equals(ReadString(ReadMember(__1, "SelectedLocation"), "Id"), CrashsiteId,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var field = AccessTools.Field(__instance.GetType(), "_needTimerUpdate");
                field?.SetValue(__instance, false);
                var label = (DateTime.MinValue + CrashsiteNightTime).ToString("HH:mm:ss");
                SetText(ReadMember(__instance, "_currentPhaseTime"), label);
                SetText(ReadMember(__instance, "_nextPhaseTime"), label);
            }
            catch (Exception ex)
            {
                LogError("M4 Crashsite time-slot display failed: " + ex);
            }
        }

        private static void SetText(object textComponent, string text)
        {
            var property = textComponent?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            property?.SetValue(textComponent, text, null);
        }

        private static void LoadMapAndDataPrefix(object __0)
        {
            try
            {
                if (_instance != null)
                {
                    DebugInfo("M1 LoadMapAndData ScenePresetLoadConfig=" +
                                             DescribeSceneConfig(__0));
                }
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 scene-load probe failed: " + ex);
                }
            }
        }

        private static void OnGameEndPrefix(object __2, object __3)
        {
            try
            {
                var id = ReadString(__2, "Id");
                if (!IsRelevant(id) || _instance == null)
                {
                    return;
                }

                _awaitingCrashsiteReturn = string.Equals(id, CrashsiteId, StringComparison.OrdinalIgnoreCase);

                _crashsiteToxin = null;
                _crashsiteToxinHealth = null;
                DebugInfo("M1 OnGameEnd location Id=" + id +
                                         " result=" + (__3 == null ? "<null>" : __3.ToString()));
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 end probe failed: " + ex);
                }
            }
        }

        private static void ShowSessionResultPrefix(object __2)
        {
            try
            {
                var id = ReadString(__2, "Id");
                if (IsRelevant(id) && _instance != null)
                {
                    DebugInfo("M1 ShowSessionResult location Id=" + id);
                }
            }
            catch (Exception ex)
            {
                if (_instance != null)
                {
                    LogError("M1 result-screen probe failed: " + ex);
                }
            }
        }

        private static void ComebackToMainMenuPostfix(Task __result)
        {
            if (!_awaitingCrashsiteReturn || __result == null)
            {
                return;
            }

            __result.ContinueWith(task =>
            {
                if (_instance != null)
                {
                    DebugInfo("M1 ComebackToMainMenu after Crashsite: " +
                                             (task.Status == TaskStatus.RanToCompletion ? "completed" :
                                              task.IsCanceled ? "canceled" : "faulted: " + task.Exception));
                }
                _awaitingCrashsiteReturn = false;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private static bool IsRelevant(string id)
        {
            return string.Equals(id, CrashsiteId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, CustomsId, StringComparison.OrdinalIgnoreCase);
        }

        private static object ReadMember(object source, string name)
        {
            if (source == null)
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = source.GetType();
            var field = type.GetField(name, flags);
            if (field != null)
            {
                return field.GetValue(source);
            }

            var property = type.GetProperty(name, flags);
            return property == null ? null : property.GetValue(source, null);
        }

        private static string ReadString(object source, string name)
        {
            var value = ReadMember(source, name);
            return value == null ? "<null>" : value.ToString();
        }

        private static string DescribeScene(object scene)
        {
            if (scene == null)
            {
                return "<null>";
            }

            return "path=" + ReadString(scene, "path") + ", rcid=" + ReadString(scene, "rcid");
        }

        private static string DescribeSceneConfig(object config)
        {
            if (config == null)
            {
                return "<null>";
            }

            var parts = new List<string>();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in config.GetType().GetFields(flags))
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string))
                {
                    parts.Add(field.Name + "=" + field.GetValue(config));
                }
                else if (field.FieldType.Name.IndexOf("ResourceKey", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    parts.Add(field.Name + "={" + DescribeScene(field.GetValue(config)) + "}");
                }
            }

            return config.GetType().FullName + " [" + string.Join(", ", parts.ToArray()) + "]";
        }
    }
}

