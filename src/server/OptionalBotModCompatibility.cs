using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;

namespace Crashsite.Server;

// Optional compatibility for the exact installed APBS assembly. Its map-weight
// indexer has a fixed set of vanilla map keys, even though Crashsite uses the
// Customs scene. Translate only this lookup; keep the raid's own ID intact.
internal static class OptionalBotModCompatibility
{
    private const string ApbsAssemblyName = "acidphantasm-progressivebotsystem";
    private const string ApbsMapWeightsType = "ProgressiveBotSystem.Models.MapRangeWeights";
    private static readonly Version SupportedApbsAssemblyVersion = new(2, 0, 0, 0);

    public static void Install(ISptLogger<CrashsiteLocationProof> logger)
    {
        var apbs = AppDomain.CurrentDomain.GetAssemblies()
            .SingleOrDefault(assembly => assembly.GetName().Name == ApbsAssemblyName);
        if (apbs is null)
        {
            CrashsiteLog.DebugInfo(logger, "Crashsite M1: APBS is not loaded; no APBS compatibility patch needed.");
            return;
        }

        if (apbs.GetName().Version != SupportedApbsAssemblyVersion)
        {
            CrashsiteLog.Error(logger, 
                $"Crashsite M1: APBS assembly version {apbs.GetName().Version} is unverified; " +
                "Crashsite APBS map compatibility was not installed.");
            return;
        }

        var weightsType = apbs.GetType(ApbsMapWeightsType);
        var indexer = weightsType?.GetMethod(
            "get_Item", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: [typeof(string)], modifiers: null);
        var prefix = AccessTools.Method(typeof(OptionalBotModCompatibility), nameof(UseCustomsApbsWeights));
        if (indexer is null || prefix is null)
        {
            CrashsiteLog.Error(logger, "Crashsite M1: APBS map-weight indexer was not found; compatibility was not installed.");
            return;
        }

        new Harmony("com.crashsite.proof.server.apbs")
            .Patch(indexer, prefix: new HarmonyMethod(prefix));
        CrashsiteLog.DebugSuccess(logger, 
            "Crashsite M1: APBS 2.0.0 map-weight lookup aliases crashsite to bigmap; " +
            "all other APBS and raid map keys remain unchanged.");
    }

    private static void UseCustomsApbsWeights(ref string __0)
    {
        if (string.Equals(__0, CrashsiteLocationProof.LocationKey, StringComparison.OrdinalIgnoreCase))
        {
            __0 = "bigmap";
        }
    }
}
