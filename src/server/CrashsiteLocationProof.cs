using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using LocationPath = SPTarkov.Server.Core.Models.Eft.Common.Tables.Path;

namespace Crashsite.Server;

// Register the independent location and keep its shared Customs scene narrowly scoped.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public sealed class CrashsiteLocationProof(
    LocationTable locations,
    LocationConfig locationConfig,
    BotConfig botConfig,
    LocaleTable locales,
    ProfileHelper profileHelper,
    CrashsiteStarterLoadout starter,
    ICloner cloner,
    ISptLogger<CrashsiteLocationProof> logger) : IOnLoad
{
    public const string LocationKey = "crashsite";
    public const string LocationMongoId = "66f1c5a00000000000000001";
    public const int MinimumPlayerLevel = 21;

    // M5 (user decision 2026-09-29, revised after v0.7.0 testing): Crashsite loot
    // density matches SVM's loot sliders at maximum for both loose loot and
    // containers. SVM writes its slider values into these dictionaries unscaled
    // (Sections.Loot.LootValues), and the user's own preset tops out at 100
    // (Labs). Before v0.6.0 Crashsite copied the SVM Customs values (60/60), which
    // is the "filled to the brim" loot the user first saw; v0.6.0's 7.5/3 was the
    // regression. These are absolute values and do not depend on SVM being installed.
    public const double LooseLootMultiplier = 100.0;
    public const double StaticLootMultiplier = 100.0;

    // M15: below Customs (550,460), left of Interchange (660,360).
    // The client normalizes coordinates to Unity anchors, so lower Y is lower on screen.
    public const double IconX = 550;
    public const double IconY = 240;

    public const string DisplayName = "Crashsite";
    public const string FlavorText =
        "A TerraGroup helicopter crashed in the middle of the Customs industrial zone. " +
        "Since the crash, the air has become poison; bandages and medkits only buy time. " +
        "The danger has caused most of the surviving populace to steer clear, " +
        "leaving all the loot there untouched.";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var catalog = locations.GetDictionary();
        var customs = locations.Bigmap;
        var customsBase = customs.Base;
        var newId = new MongoId(LocationMongoId);

        if (catalog.ContainsKey(LocationKey) ||
            catalog.Values.Any(location => location?.Base?.IdField == newId))
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage(
                $"Crashsite M1: location key or ID already registered ({LocationKey}, {LocationMongoId})."));
        }

        if (customsBase.Id != "bigmap" ||
            customsBase.Scene?.Path != "maps/customs_preset.bundle" ||
            customsBase.Scene.Rcid != "bigmap.scenespreset.asset")
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage(
                "Crashsite M1: the loaded Customs record does not match SPT 4.1.6; refusing to register a map with an unverified scene."));
        }

        if (customs.LooseLoot is null || customs.StaticLoot is null || customs.StaticContainers is null)
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: Customs loot data is incomplete."));
        }

        // 4.1.6's loot generator falls back to a "default" key for unknown maps,
        // but the shipped config has no such key, so Crashsite needs its own entries.
        // Customs' current values are only logged for comparison.
        var customsLoose = locationConfig.LooseLootMultiplier.TryGetValue("bigmap", out var loose) ? loose : double.NaN;
        var customsStatic = locationConfig.StaticLootMultiplier.TryGetValue("bigmap", out var stat) ? stat : double.NaN;

        if (locationConfig.StaticLootMultiplier.ContainsKey(LocationKey) ||
            locationConfig.LooseLootMultiplier.ContainsKey(LocationKey))
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: a Crashsite loot multiplier is already registered."));
        }

        // Logged for comparison only; Crashsite never randomises containers (M5).
        var customsRandomisesContainers = locationConfig.ContainerRandomisationSettings.Enabled &&
            locationConfig.ContainerRandomisationSettings.Maps.ContainsKey("bigmap");
        if (!locationConfig.LootMaxSpawnLimits.TryGetValue("bigmap", out var maxSpawnLimits))
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: Customs loot-limit settings are missing."));
        }

        if (locationConfig.ContainerRandomisationSettings.Maps.ContainsKey(LocationKey) ||
            locationConfig.LootMaxSpawnLimits.ContainsKey(LocationKey))
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: a Crashsite container or loot-limit setting is already registered."));
        }

        var crashsiteBase = cloner.Clone(customsBase)
            ?? throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: could not clone Customs base data."));
        crashsiteBase.IdField = newId;
        crashsiteBase.Id = LocationKey;
        crashsiteBase.Name = DisplayName;
        crashsiteBase.Description = FlavorText;
        crashsiteBase.IconX = IconX;
        crashsiteBase.IconY = IconY;
        // M8: no banners. The client's LocationInfoPanel shows its default image
        // when Banners is empty (not null), which is the generic image Terminal uses.
        crashsiteBase.Banners = new List<Banner>();
        crashsiteBase.Enabled = true;
        crashsiteBase.RequiredPlayerLevelMin = MinimumPlayerLevel;
        // M1 tests the PMC route only. Scav raid-time settings index map IDs directly.
        crashsiteBase.DisabledForScav = true;
        crashsiteBase.Loot = [];

        // The original scene and its spawn/extract geometry are retained by the base clone.
        // Separate lazy wrappers give Crashsite its own transformer lists (for M5) while
        // reading Customs' data, including any transformers SPT/mods added to Customs.
        var crashsiteLooseLoot = DeriveLootData(customs.LooseLoot, cloner, out var looseLootShared);
        var crashsiteStaticLoot = DeriveLootData(customs.StaticLoot, cloner, out var staticLootShared);
        var crashsiteStaticContainers = DeriveLootData(customs.StaticContainers, cloner, out var staticContainersShared);
        var crashsite = customs with
        {
            Base = crashsiteBase,
            LooseLoot = crashsiteLooseLoot,
            StaticLoot = crashsiteStaticLoot,
            StaticContainers = crashsiteStaticContainers,
            StaticAmmo = cloner.Clone(customs.StaticAmmo)!,
            Statics = cloner.Clone(customs.Statics),
            AllExtracts = cloner.Clone(customs.AllExtracts)!,
        };

        // Add the new vertex without changing any of the original graph edges.
        var paths = locations.Base.Paths
            ?? throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: location graph has no paths list."));
        if (paths.Any(path => path.Source == LocationMongoId || path.Destination == LocationMongoId))
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: location graph already refers to the Crashsite ID."));
        }

        locationConfig.StaticLootMultiplier.Add(LocationKey, StaticLootMultiplier);
        locationConfig.LooseLootMultiplier.Add(LocationKey, LooseLootMultiplier);
        CopyCustomsBotSettings();
        // M5: no ContainerRandomisationSettings.Maps entry. SPT 4.1.6
        // LocationRandomisationEnabled(id) is Enabled && Maps.ContainsKey(id), so a
        // missing key spawns every static container on the map, like SVM's AllContainers.
        locationConfig.LootMaxSpawnLimits.Add(LocationKey, cloner.Clone(maxSpawnLimits)!);
        catalog.Add(LocationKey, crashsite);
        paths.Add(new LocationPath
        {
            Source = customsBase.IdField.ToString(),
            Destination = LocationMongoId,
            Event = false,
        });

        // EFT's map label and info-panel text use the Mongo ID locale keys, independent
        // of Base.Name/Description. Register the English text in every loaded locale.
        foreach (var locale in locales.Global.Values)
        {
            locale.AddTransformer(strings =>
            {
                if (strings is null)
                {
                    return strings;
                }

                strings[$"{LocationMongoId} Name"] = DisplayName;
                strings[$"{LocationMongoId} Description"] = FlavorText;
                return strings;
            });
        }

        if (!ReferenceEquals(locations.GetLocation(LocationKey), crashsite) ||
            !ReferenceEquals(locations.Bigmap, customs) ||
            customs.Base.Id != "bigmap")
        {
            throw new InvalidOperationException(CrashsiteLog.PublicMessage("Crashsite M1: location lookup invariant failed."));
        }

        CrashsiteRaidDiagnostics.Initialize(logger, locationConfig, profileHelper, starter);
        new Harmony("com.crashsite.proof.server.diagnostics")
            .PatchAll(typeof(CrashsiteRaidDiagnostics).Assembly);
        OptionalBotModCompatibility.Install(logger);
        logger.Success("Crashsite loaded.");

        CrashsiteLog.DebugSuccess(logger, 
            $"Crashsite M1: registered {LocationKey} ({LocationMongoId}); scene={crashsiteBase.Scene?.Path}/{crashsiteBase.Scene?.Rcid}; " +
            $"exits={crashsiteBase.Exits.Count()}; M5 staticLootMultiplier={StaticLootMultiplier} (Customs now {customsStatic}); " +
            $"M5 looseLootMultiplier={LooseLootMultiplier} (Customs now {customsLoose}); allContainers=true (Customs randomised={customsRandomisesContainers}); " +
            $"lootLimitEntries={maxSpawnLimits.Count}; vanilla Customs remains {customsBase.IdField}; " +
            $"per-raid loot copies skipped (fresh source)={looseLootShared}/{staticLootShared}/{staticContainersShared}.");
        CrashsiteLog.DebugInfo(logger, $"Crashsite M6: {CrashsiteBossRoster.BossNames.Count} boss waves scheduled per raid, last at " +
                    $"{CrashsiteBossRoster.LastSpawnSeconds}s: {CrashsiteBossRoster.Describe()}.");
        CrashsiteLog.DebugInfo(logger, $"Crashsite M3: server version {CrashsiteClientHandshake.Version}; " +
                    $"matching client must complete the handshake and inventory transfer before Crashsite raid start.");
        return Task.CompletedTask;
    }

    // SPT's per-map bot settings are keyed by location ID. Without entries Crashsite
    // got the "default" bot cap (18, with a warning) instead of Customs' (26).
    // Copy Customs' values so Crashsite's normal population matches Customs;
    // the M6 bosses ignore the cap (IgnoreMaxBots).
    private void CopyCustomsBotSettings()
    {
        if (botConfig.MaxBotCap.TryGetValue("bigmap", out var cap))
        {
            botConfig.MaxBotCap.TryAdd(LocationKey, cap);
        }

        if (botConfig.AssaultBrainType.TryGetValue("bigmap", out var assaultBrains))
        {
            botConfig.AssaultBrainType.TryAdd(LocationKey, new Dictionary<string, int>(assaultBrains));
        }

        if (botConfig.PlayerScavBrainType.TryGetValue("bigmap", out var playerScavBrains))
        {
            botConfig.PlayerScavBrainType.TryAdd(LocationKey, new Dictionary<string, int>(playerScavBrains));
        }
    }

    // In SPT 4.1.6 the location loot LazyLoads are created by ImporterUtil with
    // cacheValue=false (LooseLoot, StaticContainerDetails and the static-loot
    // dictionary carry no [CacheLazyLoad]), so every Value read deserializes a new
    // object graph from disk and applies Customs' transformers. That graph is already
    // independent of Customs, and v0.1.x-0.3.0 deep-cloned it again on every raid.
    // Keep the clone only if the source is caching (e.g. replaced by another mod),
    // where the returned instance would otherwise be shared with Customs.
    private static LazyLoad<T> DeriveLootData<T>(LazyLoad<T> source, ICloner cloner, out bool sharesFreshSource)
    {
        sharesFreshSource = typeof(LazyLoad<T>)
            .GetField("<cacheValue>P", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(source) is false;
        return sharesFreshSource
            ? new LazyLoad<T>(() => source.Value, false)
            : new LazyLoad<T>(() => cloner.Clone(source.Value)!, false);
    }
}

// Diagnostics are limited to Crashsite and do not alter normal raid arguments or results.
[HarmonyPatch]
internal static class CrashsiteRaidDiagnostics
{
    private static ISptLogger<CrashsiteLocationProof>? _logger;
    private static LocationConfig? _locationConfig;
    private static ProfileHelper? _profileHelper;

    private static CrashsiteStarterLoadout _starter = null!;

    public static void Initialize(ISptLogger<CrashsiteLocationProof> logger, LocationConfig locationConfig, ProfileHelper profileHelper, CrashsiteStarterLoadout starter)
    {
        _logger = logger;
        _locationConfig = locationConfig;
        _profileHelper = profileHelper;
        _starter = starter;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync))]
    private static void StartRequested(MongoId sessionId, StartLocalRaidRequestData request, out CrashsiteStarterGrant? __state)
    {
        __state = null;
        if (IsCrashsite(request.Location))
        {
            var accessRejection = CrashsiteAccess.GetRejection(_profileHelper, sessionId);
            if (accessRejection is not null)
            {
                CrashsiteLog.Warning(_logger, accessRejection);
                throw new InvalidOperationException(CrashsiteLog.PublicMessage(accessRejection));
            }

            if (!CrashsiteClientHandshake.IsReady(sessionId))
            {
                var message = "Crashsite M2: blocked Crashsite raid start because this session has no matching " +
                              $"client version {CrashsiteClientHandshake.Version} handshake. " +
                              "Install both Crashsite DLLs from the same package and reselect Crashsite.";
                CrashsiteLog.Error(_logger, message);
                throw new InvalidOperationException(CrashsiteLog.PublicMessage(message));
            }

            if (!string.Equals(request.PlayerSide, "Pmc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Crashsite starter equipment is available for PMC raids only.");
            __state = _starter.Prepare(sessionId);

            if (!CrashsiteInventoryTransferState.ConsumeReady(sessionId))
            {
                const string message = "Crashsite M3: blocked raid start because the ready-time inventory transfer was not accepted. " +
                                       "Install matching Crashsite DLLs and ready again from the Crashsite location screen.";
                CrashsiteLog.Error(_logger, message);
                throw new InvalidOperationException(CrashsiteLog.PublicMessage(message));
            }

            // M5: re-assert Crashsite's own multipliers right before loot generation,
            // in case another mod rewrote the location config after startup.
            if (_locationConfig is not null)
            {
                _locationConfig.LooseLootMultiplier[CrashsiteLocationProof.LocationKey] = CrashsiteLocationProof.LooseLootMultiplier;
                _locationConfig.StaticLootMultiplier[CrashsiteLocationProof.LocationKey] = CrashsiteLocationProof.StaticLootMultiplier;
                _locationConfig.ContainerRandomisationSettings.Maps.Remove(CrashsiteLocationProof.LocationKey);
            }

            CrashsiteLog.DebugInfo(_logger, $"Crashsite M1 start request: location={request.Location}, side={request.PlayerSide}, " +
                          $"timeVariant={request.TimeVariant} (M4: night is forced for Crashsite).");
        }
    }

    // M17: announce initialization only after the server start task succeeds.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync))]
    private static void ReportInitialization(MongoId sessionId, StartLocalRaidRequestData request, CrashsiteStarterGrant? __state, ref Task<StartLocalRaidResponseData> __result)
    {
        if (IsCrashsite(request.Location))
            __result = ReportInitializationAsync(__result, sessionId, __state!);
    }

    private static async Task<StartLocalRaidResponseData> ReportInitializationAsync(Task<StartLocalRaidResponseData> start, MongoId sessionId, CrashsiteStarterGrant grant)
    {
        var response = await start.ConfigureAwait(false);
        _starter.Bind(sessionId, response.ServerId, grant);
        _logger?.Success("Crashsite successfully initialized.");
        return response;
    }

    // M4: Crashsite is always night; the matching client freezes its raid clock at
    // 01:00. SPT asks this helper for the raid configuration's IsNightRaid flag
    // (bot flashlight/NVG state) and night bot equipment. Other maps keep SPT's
    // time-based answer.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WeatherHelper), nameof(WeatherHelper.IsNightTime))]
    private static void CrashsiteIsAlwaysNight(string mapLocation, ref bool __result)
    {
        if (IsCrashsite(mapLocation))
        {
            if (!__result)
            {
                CrashsiteLog.DebugInfo(_logger, "Crashsite M4: SPT's time-based night check said day; Crashsite is forced to night.");
            }

            __result = true;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot))]
    private static void LocationGenerated(string name, LocationBase __result)
    {
        if (IsCrashsite(name))
        {
            // M6: applied to the per-raid clone sent to the client, after SPT's own
            // wave generation, so every raid gets the full roster.
            var bossWaves = CrashsiteBossRoster.Apply(__result);
            CrashsiteLog.DebugInfo(_logger, $"Crashsite M6 boss roster applied: {bossWaves} boss waves (chance 100, forced), " +
                          $"total boss-spawn entries {__result.BossLocationSpawn?.Count ?? 0}.");
            CrashsiteLog.DebugInfo(_logger, 
                $"Crashsite M1 generated: id={__result.Id}, mongoId={__result.IdField}, " +
                $"scene={__result.Scene?.Path}/{__result.Scene?.Rcid}, exits={__result.Exits.Count()}, " +
                $"lootPoints={__result.Loot?.Count() ?? 0}.");
        }
    }

    // M7: the Crashsite toxin is raid-only. The client reports its effects in the
    // post-raid profile, and SPT's HandlePostRaidPmc would copy them into the saved
    // profile, where the toxin would keep ticking out of raid and follow the player
    // into later raids. On a Crashsite raid the only toxin is the Crashsite one, so
    // strip intoxication effects from the reported health before SPT processes it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaidAsync))]
    private static void StripCrashsiteToxin(EndLocalRaidRequestData request)
    {
        if (!IsCrashsiteServerId(request.ServerId))
        {
            return;
        }

        var removed = 0;
        foreach (var bodyPart in request.Results?.Profile?.Health?.BodyParts?.Values ?? Enumerable.Empty<BodyPartHealth>())
        {
            var effects = bodyPart?.Effects;
            if (effects is null)
            {
                continue;
            }

            foreach (var key in effects.Keys.Where(key => key.Contains("Intoxication", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                effects.Remove(key);
                removed++;
            }
        }

        CrashsiteLog.DebugInfo(_logger, $"Crashsite M7: removed {removed} toxin effect(s) from the post-raid profile ({request.ServerId}).");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaidAsync))]
    private static void RaidFinished(MongoId sessionId, EndLocalRaidRequestData request, ref Task __result)
    {
        if (!IsCrashsiteServerId(request.ServerId))
        {
            return;
        }

        CrashsiteLog.DebugInfo(_logger, 
            $"Crashsite M1 end request: serverId={request.ServerId}, result={request.Results?.Result}, " +
            $"exit={request.Results?.ExitName}.");
        __result = LogCompletionAsync(__result, request.ServerId, sessionId);
    }

    private static async Task LogCompletionAsync(Task raidEnd, string? serverId, MongoId sessionId)
    {
        try
        {
            await raidEnd.ConfigureAwait(false);
            _starter.Forget(sessionId, serverId);
            CrashsiteLog.DebugSuccess(_logger, $"Crashsite M1 raid end and profile save completed: {serverId}.");
        }
        catch (Exception exception)
        {
            CrashsiteLog.Error(_logger, $"Crashsite M1 raid end failed for {serverId}: {exception}");
            throw;
        }
    }

    private static bool IsCrashsite(string? location) =>
        string.Equals(location, CrashsiteLocationProof.LocationKey, StringComparison.OrdinalIgnoreCase);

    private static bool IsCrashsiteServerId(string? serverId) =>
        serverId?.StartsWith(CrashsiteLocationProof.LocationKey + ".", StringComparison.OrdinalIgnoreCase) == true;
}



