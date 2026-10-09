using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;

namespace Crashsite.Server;

// M6 (user decisions 2026-09-29): the 12 regular 4.1.6 map bosses, each with its
// vanilla escort composition, spawn on every Crashsite raid, and all of them are
// scheduled by the 8-minute mark. Definitions are copied from the vanilla 4.1.6
// location data (not read from the live tables, which bot mods such as ABPS may
// rebuild) and re-homed to Customs bot zones. Nothing here touches another map.
internal static class CrashsiteBossRoster
{
    // Latest scheduled spawn, in raid seconds. The user's rule is "all bosses
    // present by 8 minutes"; 440 s leaves room for spawn-queue latency.
    public const int LastSpawnSeconds = 440;
    private const int SpawnStepSeconds = 40;

    private static readonly string[] SpawnModes = ["regular", "pve"];

    // Order = spawn order (0 s, 40 s, 80 s, ...). Zones spread the groups across
    // the Customs scene; each zone exists in vanilla bigmap SpawnPointParams.
    private static readonly RosterEntry[] Entries =
    [
        // Reshala: vanilla Customs.
        new("bossBully", "ZoneDormitory", "followerBully", "4"),
        // The Goons: vanilla Customs composition.
        new("bossKnight", "ZoneCrossRoad", "exUsec", "2",
            Supports: [Support("followerBigPipe", "1"), Support("followerBirdEye", "1"), Support("followerGluharScout", "0")]),
        // Killa: vanilla Interchange.
        new("bossKilla", "ZoneTankSquare", "followerTagilla", "0"),
        // Tagilla: vanilla Factory.
        new("bossTagilla", "ZoneFactoryCenter", "followerBully", "0"),
        // Shturman: vanilla Woods.
        new("bossKojaniy", "ZoneWade", "followerKojaniy", "2,3"),
        // Sanitar: vanilla Shoreline.
        new("bossSanitar", "ZoneGasStation", "followerSanitar", "3"),
        // Glukhar: vanilla Reserve.
        new("bossGluhar", "ZoneCustoms", "followerGluharSecurity", "2",
            Supports: [Support("followerGluharAssault", "2"), Support("followerGluharSecurity", "2"), Support("followerGluharScout", "2")]),
        // Kaban: vanilla Streets. His sniper group is a separate boss wave on
        // Streets triggered by "BossBoarBorn"; here it is scheduled with him.
        new("bossBoar", "ZoneBrige", "followerBoar", "6",
            Supports: [Support("followerBoar", "4"), Support("followerBoarClose1", "1"), Support("followerBoarClose2", "1")],
            Companion: new("bossBoarSniper", "ZoneSnipeBrige", "bossBoarSniper", "1,2")),
        // Kollontay: vanilla Streets.
        new("bossKolontay", "ZoneOldAZS", "followerKolontaySecurity", "2",
            Supports: [Support("followerKolontayAssault", "2"), Support("followerKolontaySecurity", "2"), Support("followerGluharScout", "0")]),
        // Zryachiy: vanilla Lighthouse island group, placed on a Customs sniper zone.
        new("bossZryachiy", "ZoneSnipeTower", "followerZryachiy", "2"),
        // Partisan: vanilla Customs, without its karma/area trigger so he is guaranteed.
        new("bossPartisan", "ZoneBlockPost", "sectantWarrior", "0"),
        // Cultist Priest: vanilla Customs (Crashsite is always night, M4).
        new("sectantPriest", "ZoneScavBase", "sectantWarrior", "4"),
    ];

    public static IReadOnlySet<string> BossNames { get; } = Entries
        .SelectMany(entry => entry.Companion is null
            ? new[] { entry.BossName }
            : new[] { entry.BossName, entry.Companion.BossName })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Replaces any roster boss waves (inherited Customs ones, or ones another mod
    // added) with the Crashsite definitions. PMC waves and other non-roster boss
    // waves on the Customs clone are left as they are.
    public static int Apply(LocationBase location)
    {
        var waves = location.BossLocationSpawn ?? [];
        waves.RemoveAll(wave => wave.BossName is not null && BossNames.Contains(wave.BossName));
        var added = 0;
        for (var index = 0; index < Entries.Length; index++)
        {
            var time = index == 0 ? -1 : index * SpawnStepSeconds;
            var entry = Entries[index];
            waves.Add(entry.Create(time));
            added++;
            if (entry.Companion is not null)
            {
                waves.Add(entry.Companion.Create(time));
                added++;
            }
        }

        location.BossLocationSpawn = waves;
        return added;
    }

    public static string Describe() => string.Join(", ", Entries.Select((entry, index) =>
        $"{entry.BossName}@{(index == 0 ? 0 : index * SpawnStepSeconds)}s/{entry.Zone}"));

    private static BossSupport Support(string type, string amount) => new()
    {
        BossEscortType = type,
        BossEscortAmount = amount,
        BossEscortDifficulty = new ListOrT<string>(["normal"], null!),
    };

    private sealed record RosterEntry(
        string BossName,
        string Zone,
        string EscortType,
        string EscortAmount,
        BossSupport[]? Supports = null,
        RosterEntry? Companion = null)
    {
        public BossLocationSpawn Create(int time) => new()
        {
            BossName = BossName,
            BossChance = 100,
            BossDifficulty = "normal",
            BossEscortType = EscortType,
            BossEscortAmount = EscortAmount,
            BossEscortDifficulty = "normal",
            BossZone = Zone,
            IsBossPlayer = false,
            IsRandomTimeSpawn = false,
            ShowOnTarkovMap = false,
            ShowOnTarkovMapPvE = false,
            Time = time,
            Delay = 0,
            TriggerId = "",
            TriggerName = "",
            DependKarma = false,
            DependKarmaPVE = false,
            ForceSpawn = true,
            IgnoreMaxBots = true,
            SpawnMode = SpawnModes,
            // Fresh support objects per raid so no two waves share instances.
            Supports = Supports?.Select(support => new BossSupport
            {
                BossEscortType = support.BossEscortType,
                BossEscortAmount = support.BossEscortAmount,
                BossEscortDifficulty = new ListOrT<string>(["normal"], null!),
            }).ToList(),
        };
    }
}
