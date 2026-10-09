using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils.Json;

namespace Crashsite.Server;

internal static class CrashsiteBossRoster
{

    public const int LastSpawnSeconds = 440;
    private const int SpawnStepSeconds = 40;

    private static readonly string[] SpawnModes = ["regular", "pve"];

    private static readonly RosterEntry[] Entries =
    [

        new("bossBully", "ZoneDormitory", "followerBully", "4"),

        new("bossKnight", "ZoneCrossRoad", "exUsec", "2",
            Supports: [Support("followerBigPipe", "1"), Support("followerBirdEye", "1"), Support("followerGluharScout", "0")]),

        new("bossKilla", "ZoneTankSquare", "followerTagilla", "0"),

        new("bossTagilla", "ZoneFactoryCenter", "followerBully", "0"),

        new("bossKojaniy", "ZoneWade", "followerKojaniy", "2,3"),

        new("bossSanitar", "ZoneGasStation", "followerSanitar", "3"),

        new("bossGluhar", "ZoneCustoms", "followerGluharSecurity", "2",
            Supports: [Support("followerGluharAssault", "2"), Support("followerGluharSecurity", "2"), Support("followerGluharScout", "2")]),

        new("bossBoar", "ZoneBrige", "followerBoar", "6",
            Supports: [Support("followerBoar", "4"), Support("followerBoarClose1", "1"), Support("followerBoarClose2", "1")],
            Companion: new("bossBoarSniper", "ZoneSnipeBrige", "bossBoarSniper", "1,2")),

        new("bossKolontay", "ZoneOldAZS", "followerKolontaySecurity", "2",
            Supports: [Support("followerKolontayAssault", "2"), Support("followerKolontaySecurity", "2"), Support("followerGluharScout", "0")]),

        new("bossZryachiy", "ZoneSnipeTower", "followerZryachiy", "2"),

        new("bossPartisan", "ZoneBlockPost", "sectantWarrior", "0"),

        new("sectantPriest", "ZoneScavBase", "sectantWarrior", "4"),
    ];

    public static IReadOnlySet<string> BossNames { get; } = Entries
        .SelectMany(entry => entry.Companion is null
            ? new[] { entry.BossName }
            : new[] { entry.BossName, entry.Companion.BossName })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            Supports = Supports?.Select(support => new BossSupport
            {
                BossEscortType = support.BossEscortType,
                BossEscortAmount = support.BossEscortAmount,
                BossEscortDifficulty = new ListOrT<string>(["normal"], null!),
            }).ToList(),
        };
    }
}
