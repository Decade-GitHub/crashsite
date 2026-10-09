using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;

namespace Crashsite.Server;

internal static class CrashsiteAccess
{
    public static string? GetRejection(ProfileHelper? profileHelper, MongoId sessionId)
    {
        var level = profileHelper?.GetFullProfile(sessionId)?.CharacterData?.PmcData?.Info?.Level;
        if (level is null)
            return "The active PMC level could not be verified; Crashsite was not started.";
        return level < CrashsiteLocationProof.MinimumPlayerLevel
            ? $"Crashsite requires PMC Level {CrashsiteLocationProof.MinimumPlayerLevel} (current level: {level})."
            : null;
    }
}
