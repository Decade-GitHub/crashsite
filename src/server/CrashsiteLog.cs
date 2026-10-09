using System.Diagnostics;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;

namespace Crashsite.Server;

internal static class CrashsiteLog
{
    [Conditional("CRASHSITE_DEBUG")]
    public static void DebugInfo<T>(ISptLogger<T>? logger, string message) => logger?.Info(message);

    [Conditional("CRASHSITE_DEBUG")]
    public static void DebugSuccess<T>(ISptLogger<T>? logger, string message) => logger?.Success(message);

    public static void Error<T>(ISptLogger<T>? logger, string message) => logger?.Error(PublicMessage(message));
    public static void Warning<T>(ISptLogger<T>? logger, string message) => logger?.Warning(PublicMessage(message));

    public static string PublicMessage(string message)
    {
#if CRASHSITE_DEBUG
        return message;
#else
        return Regex.Replace(message, @"\bM\d+\s*:?\s+", "");
#endif
    }
}
