using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Crashsite.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.crashsite.proof.server";
    public string Name { get; init; } = "Crashsite";
    public string Author { get; init; } = "Crashsite contributors";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new(CrashsiteClientHandshake.Version);
    public SemanticVersioning.Range SptVersion { get; init; } = new("4.1.6");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "UNLICENSED";
    public bool HasPrepatcher { get; init; }
}
