using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Crashsite.Client
{
    public sealed partial class CrashsiteClientPlugin
    {
        private static bool _starterPatchInstalled;
        private static object _starterProfile;
        private static readonly List<object> _starterRoots = new List<object>();
        private static string _starterServerId;
        private static bool _starterWorldStarted;

        private void InstallStarterPatch()
        {
            try
            {
                var settings = FindType("JsonType.LocalSettings");
                var target = AccessTools.Method(FindType("EFT.EftClientBackendSession"), "LocalRaidStarted", new[] { FindType("EFT.LocalRaidSettings") });
                if (target == null || target.ReturnType != typeof(Task<>).MakeGenericType(settings) ||
                    settings.GetField("serverId") == null ||
                    AccessTools.Method(FindType("EFT.ItemFactory"), "FlatItemsToTree") == null ||
                    AccessTools.Method(FindType("EFT.Profile"), "GetAllPrefabPaths") == null)
                    throw new MissingMethodException("The installed raid start or item-tree API does not match SPT 4.1.6.");
                var postfix = typeof(CrashsiteClientPlugin).GetMethod(nameof(StarterRaidPostfix), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(settings);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                _starterPatchInstalled = true;
                DebugInfo("M18 raid-scoped starter-kit hook installed.");
            }
            catch (Exception ex)
            {
                _starterPatchInstalled = false;
                LogError("Crashsite starter-kit patch installation failed: " + ex);
            }
        }

        private static void StarterRaidPostfix<T>(object __instance, object __0, ref Task<T> __result)
        {
            if (IsStarterLocation(ReadString(__0, "location")))
                __result = EquipStarterAsync(__result, __instance);
        }
        private static bool IsStarterLocation(string location) => string.Equals(location, CrashsiteId, StringComparison.OrdinalIgnoreCase);

        private static async Task<T> EquipStarterAsync<T>(Task<T> start, object session)
        {
            var settings = await start;
            var serverId = ReadString(settings, "serverId");
            if (string.IsNullOrEmpty(serverId)) throw new InvalidOperationException("Crashsite returned no accepted server raid ID.");
            var profile = ReadMember(session, "Profile") ?? throw new InvalidOperationException("The active PMC profile is unavailable.");
            if (_starterServerId == serverId && ReferenceEquals(_starterProfile, profile)) return settings;
            CleanupUnstartedStarter();
            var request = new JObject { ["ServerId"] = serverId }.ToString();
            string response;
            try { response = await RequestHandler.PostJsonAsync("/crashsite/raid/starter", request); }
            catch { response = await RequestHandler.PostJsonAsync("/crashsite/raid/starter", request); }
            var data = JObject.Parse(response)["data"] as JObject;
            if (data == null || !((bool?)data["Accepted"] ?? (bool?)data["accepted"] ?? false))
                throw new InvalidOperationException((string)data?["Reason"] ?? (string)data?["reason"] ?? "Crashsite did not accept the starter-kit grant.");
            var inventory = ReadMember(profile, "Inventory");
            var equipment = ReadMember(inventory, "Equipment");
            var slots = (ReadMember(equipment, "Slots") as IEnumerable)?.Cast<object>().ToArray() ?? throw new InvalidOperationException("PMC equipment slots are unavailable.");
            var holster = slots.SingleOrDefault(s => ReadString(s, "ID") == "Holster");
            var pockets = ReadMember(slots.SingleOrDefault(s => ReadString(s, "ID") == "Pockets"), "ContainedItem");
            var items = data["Items"] as JArray ?? data["items"] as JArray;
            ValidateStarterGrant(data, serverId, ReadString(equipment, "Id"), ReadString(pockets, "Id"));
            if (holster == null || ReadMember(holster, "ContainedItem") != null) throw new InvalidOperationException("The Crashsite Holster is not empty.");
            var pocketName = (string)data["PocketGrid"] ?? (string)data["pocketGrid"];
            var grid = (ReadMember(pockets, "Grids") as IEnumerable)?.Cast<object>().SingleOrDefault(g => ReadString(g, "ID") == pocketName);
            if (grid == null || (ReadMember(grid, "Items") as IEnumerable)?.Cast<object>().Any() == true)
                throw new InvalidOperationException("The granted Crashsite pocket is not empty or no longer exists.");
            var serializer = JsonSerializer.CreateDefault();
            serializer.Converters.Add((JsonConverter)Activator.CreateInstance(FindType("EFT.UnparsedDataConverter")));
            var flat = items.ToObject(FindType("JsonType.FlatItem").MakeArrayType(), serializer);
            var factory = Singleton(FindType("EFT.ItemFactory"));
            var treeMethod = factory.GetType().GetMethods().Single(m => m.Name == "FlatItemsToTree" && m.GetParameters().Length == 3);
            var tree = treeMethod.Invoke(factory, new[] { flat, (object)false, null });
            if ((ReadMember(tree, "DeserializationErrors") as IEnumerable)?.Cast<object>().Any() == true)
                throw new InvalidOperationException("The native item factory rejected the Crashsite starter preset.");
            var built = ReadMember(tree, "Items") as IDictionary;
            if (built == null || built.Count != items.Count) throw new InvalidOperationException("The native item factory omitted starter items.");
            var weapon = built[(string)data["WeaponId"] ?? (string)data["weaponId"]];
            var spare = built[(string)data["SpareId"] ?? (string)data["spareId"]];
            _starterProfile = profile;
            _starterWorldStarted = false;
            try
            {

                _starterRoots.Add(weapon);
                var address = AccessTools.Method(holster.GetType(), "CreateItemAddress", Type.EmptyTypes).Invoke(holster, null);
                InvokeAddressAction(address, "Add", weapon);
                if (!IsInContainer(weapon, holster)) throw new InvalidOperationException("The starter pistol did not enter Holster.");
                _starterRoots.Add(spare);
                if (!TryPlaceInFreeStashCell(grid, spare)) throw new InvalidOperationException("The spare magazine did not fit the granted pocket.");

                await LoadStarterPrefabs(profile);
                _starterServerId = serverId;
                DebugInfo("M18 preset M9A3 equipped: 17+1 PBP and 17 spare for raid " + serverId + ".");
                return settings;
            }
            catch
            {
                CleanupUnstartedStarter();
                throw;
            }
        }

        private static object Singleton(Type type) => FindType("Comfort.Common.Singleton`1").MakeGenericType(type)
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null)
            ?? throw new InvalidOperationException(type.Name + " is not initialized.");

        private static async Task LoadStarterPrefabs(object profile)
        {
            var keys = ((IEnumerable)AccessTools.Method(profile.GetType(), "GetAllPrefabPaths", new[] { typeof(bool) }).Invoke(profile, new object[] { true })).Cast<object>().ToArray();
            var resources = Array.CreateInstance(FindType("EFT.ResourceKey"), keys.Length);
            for (var i = 0; i < keys.Length; i++) resources.SetValue(keys[i], i);
            var factoryType = FindType("EFT.ObjectsFactory");
            var method = factoryType.GetMethods().Single(m => m.Name == "LoadBundlesAndCreatePools" && m.GetParameters().Length == 6 && m.GetParameters()[0].ParameterType.Name == "PoolsCategory");
            var p = method.GetParameters();
            var priority = FindType("Diz.Jobs.JobYieldPriority").GetProperty("General", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            var progress = Activator.CreateInstance(typeof(Progress<>).MakeGenericType(FindType("EFT.InitLevelProgress")));
            await (Task)method.Invoke(Singleton(factoryType), new object[] { Enum.ToObject(p[0].ParameterType, 0), Enum.ToObject(p[1].ParameterType, 0), resources, priority, progress, CancellationToken.None });
        }

        private static void CleanupUnstartedStarter()
        {
            if (_starterWorldStarted)
            {

                _starterRoots.Clear(); _starterProfile = null; _starterServerId = null;
                return;
            }
            try
            {
                foreach (var root in _starterRoots.AsEnumerable().Reverse())
                {
                    var address = ReadMember(root, "CurrentAddress");
                    if (address != null) InvokeAddressAction(address, "Remove", root);
                    if (ReadMember(root, "CurrentAddress") != null) throw new InvalidOperationException("A starter item could not be removed after a failed start.");
                }
                _starterRoots.Clear(); _starterProfile = null; _starterServerId = null;
            }
            catch
            {
                _inventoryUntrustedReason = "Crashsite could not clean up an unstarted starter kit. Restart the game to reload the saved profile before entering another raid.";
                throw;
            }
        }

        private static void ValidateStarterGrant(JObject data, string serverId, string equipmentId, string pocketsId)
        {
            string Value(string name) => (string)data[name] ?? (string)data[char.ToLowerInvariant(name[0]) + name.Substring(1)];
            if (Value("ServerId") != serverId || Value("EquipmentId") != equipmentId || Value("PocketsId") != pocketsId || string.IsNullOrEmpty(equipmentId) || string.IsNullOrEmpty(pocketsId))
                throw new InvalidOperationException("The starter grant does not match the accepted raid and retained equipment roots.");
            var items = data["Items"] as JArray ?? data["items"] as JArray;
            if (items == null || items.Count < 5) throw new InvalidOperationException("The starter item tree is missing.");
            var ids = items.Select(i => (string)i["_id"]).ToArray();
            if (ids.Any(string.IsNullOrEmpty) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new InvalidOperationException("The starter item tree has duplicate or missing IDs.");
            var weaponId = Value("WeaponId"); var spareId = Value("SpareId");
            var weapon = items.SingleOrDefault(i => (string)i["_id"] == weaponId);
            var spare = items.SingleOrDefault(i => (string)i["_id"] == spareId);
            if ((string)weapon?["_tpl"] != "5cadc190ae921500103bb3b6" || (string)spare?["_tpl"] != "5cadc2e0ae9215051e1c21e7" || weaponId == spareId ||
                !string.IsNullOrEmpty((string)weapon?["parentId"]) || !string.IsNullOrEmpty((string)spare?["parentId"]))
                throw new InvalidOperationException("The grant does not contain detached M9A3 and spare magazine roots.");
            var magazines = items.Where(i => (string)i["parentId"] == weaponId && (string)i["slotId"] == "mod_magazine" && (string)i["_tpl"] == "5cadc2e0ae9215051e1c21e7").ToArray();
            if (magazines.Length != 1) throw new InvalidOperationException("The starter pistol has no standard magazine.");
            var rounds = items.Where(i => (string)i["_tpl"] == "5efb0da7a29a85116f6ea05f").ToArray();
            bool Stack(string parent, string slot, int count) => rounds.Count(i => (string)i["parentId"] == parent && (string)i["slotId"] == slot && (int?)i["upd"]?["StackObjectsCount"] == count) == 1;
            if (rounds.Length != 3 || !Stack((string)magazines[0]["_id"], "cartridges", 17) || !Stack(weaponId, "patron_in_weapon", 1) || !Stack(spareId, "cartridges", 17))
                throw new InvalidOperationException("The starter ammunition is not exactly 17+1 and 17 PBP rounds.");
            foreach (var item in items)
            {
                var id = (string)item["_id"];
                if (id == weaponId || id == spareId) continue;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = item;
                while ((string)current["_id"] != weaponId && (string)current["_id"] != spareId)
                {
                    if (!visited.Add((string)current["_id"])) throw new InvalidOperationException("The starter graph has a cycle.");
                    var parent = (string)current["parentId"];
                    current = items.SingleOrDefault(i => (string)i["_id"] == parent) ?? throw new InvalidOperationException("The starter graph has an orphan.");
                }
            }
        }
    }
}

