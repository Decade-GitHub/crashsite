using System.Collections.Concurrent;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace Crashsite.Server;

internal static class CrashsiteClientHandshake
{
    public const string Route = "/crashsite/client/handshake";
    public const string Version = "1.0.0";

    private static readonly ConcurrentDictionary<MongoId, byte> ReadySessions = new();

    public static bool Accept(MongoId sessionId, string? version)
    {
        if (!string.Equals(version, Version, StringComparison.Ordinal))
        {
            ReadySessions.TryRemove(sessionId, out _);
            return false;
        }

        ReadySessions[sessionId] = 1;
        return true;
    }

    public static bool IsReady(MongoId sessionId) => ReadySessions.ContainsKey(sessionId);
}

public sealed record CrashsiteHandshakeRequest : IRequestData
{
    public string? Version { get; init; }
}

public sealed record CrashsiteHandshakeResponse(string Version, bool Accepted);

[Injectable(TypePriority = OnLoadOrder.Routers + 1)]
public sealed class CrashsiteHandshakeRouter(JsonUtil jsonUtil, CrashsiteHandshakeCallback callback)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<CrashsiteHandshakeRequest>(
            CrashsiteClientHandshake.Route,
            (url, request, sessionId, output, cancellationToken) =>
                callback.HandleAsync(request, sessionId)),
    ]);

[Injectable]
public sealed class CrashsiteHandshakeCallback(
    ISptLogger<CrashsiteHandshakeCallback> logger,
    HttpResponseUtil responseUtil)
{
    public ValueTask<string> HandleAsync(CrashsiteHandshakeRequest request, MongoId sessionId)
    {
        var accepted = CrashsiteClientHandshake.Accept(sessionId, request.Version);
        if (accepted)
        {
            CrashsiteLog.DebugInfo(logger, $"Crashsite M3 client/server handshake accepted for session {sessionId}; version={request.Version}.");
        }
        else
        {
            CrashsiteLog.Error(logger, $"Crashsite M3 client/server handshake rejected for session {sessionId}: " +
                         $"client={request.Version ?? "<missing>"}, server={CrashsiteClientHandshake.Version}.");
        }

        return new ValueTask<string>(responseUtil.GetBody(
            new CrashsiteHandshakeResponse(CrashsiteClientHandshake.Version, accepted)));
    }
}

