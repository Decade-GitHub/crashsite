using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;

namespace Crashsite.Server;

public sealed record CrashsiteStarterRequest : IRequestData { public string? ServerId { get; init; } }
public sealed record CrashsiteStarterGrant(bool Accepted, string? Reason, string? ServerId,
    string? EquipmentId, string? PocketsId, string? PocketGrid, string? WeaponId, string? SpareId, IReadOnlyList<Item> Items)
{
    public static CrashsiteStarterGrant Reject(string reason) => new(false, reason, null, null, null, null, null, null, []);
}

// These item trees are raid equipment, never saved into the hideout profile at ready time.
// SPT defaults to Transient: lifecycle hooks and HTTP consumers must share this store.
[Injectable(InjectionType.Singleton)]
public sealed class CrashsiteStarterLoadout(PresetHelper presets, TemplateTable templates, ProfileHelper profiles)
{
    public const string Route = "/crashsite/raid/starter";
    public const string WeaponTpl = "5cadc190ae921500103bb3b6";
    public const string MagazineTpl = "5cadc2e0ae9215051e1c21e7";
    public const string AmmoTpl = "5efb0da7a29a85116f6ea05f";
    private readonly ConcurrentDictionary<MongoId, CrashsiteStarterGrant> _active = new();

    public CrashsiteStarterGrant Prepare(MongoId sessionId)
    {
        var pmc = profiles.GetFullProfile(sessionId)?.CharacterData?.PmcData;
        return Build(presets.GetDefaultPreset(new MongoId(WeaponTpl)), templates.Items, pmc);
    }

    public void Bind(MongoId sessionId, string? serverId, CrashsiteStarterGrant grant)
    {
        if (string.IsNullOrWhiteSpace(serverId)) throw new InvalidOperationException("Crashsite raid has no server ID.");
        _active[sessionId] = grant with { ServerId = serverId };
    }
    public CrashsiteStarterGrant Get(MongoId sessionId, string? serverId)
    {
        var reason = CrashsiteAccess.GetRejection(profiles, sessionId);
        if (reason is not null) return CrashsiteStarterGrant.Reject(reason);
        if (!CrashsiteClientHandshake.IsReady(sessionId)) return CrashsiteStarterGrant.Reject("The matching Crashsite client is not connected.");
        return Lookup(_active.GetValueOrDefault(sessionId), serverId);
    }
    internal static CrashsiteStarterGrant Lookup(CrashsiteStarterGrant? grant, string? serverId) =>
        grant is not null && !string.IsNullOrWhiteSpace(serverId) && string.Equals(grant.ServerId, serverId, StringComparison.Ordinal)
            ? grant : CrashsiteStarterGrant.Reject("No starter kit exists for this accepted Crashsite raid. Ready again.");
    public void Forget(MongoId sessionId, string? serverId)
    {
        if (_active.TryGetValue(sessionId, out var grant) && grant.ServerId == serverId)
            ((ICollection<KeyValuePair<MongoId, CrashsiteStarterGrant>>)_active).Remove(new(sessionId, grant));
    }

    internal static CrashsiteStarterGrant Build(Preset? preset, IDictionary<MongoId, TemplateItem> templates, PmcData? pmc)
    {
        static InvalidOperationException Bad(string reason) => new("Crashsite starter kit unavailable: " + reason);
        var inventory = pmc?.Inventory;
        var equipment = inventory?.Equipment?.ToString() ?? throw Bad("PMC equipment is missing.");
        var carried = inventory!.Items ?? throw Bad("PMC inventory is missing.");
        if (carried.Any(i => i.ParentId == equipment && i.SlotId == "Holster")) throw Bad("the Holster is not empty after transfer.");
        var pockets = carried.SingleOrDefault(i => i.ParentId == equipment && i.SlotId == "Pockets") ?? throw Bad("Pockets are missing.");
        TemplateItem Template(MongoId id) => templates.TryGetValue(id, out var t) ? t : throw Bad("an item template is missing.");
        var gunTemplate = Template(new MongoId(WeaponTpl));
        var magTemplate = Template(new MongoId(MagazineTpl));
        var ammoTemplate = Template(new MongoId(AmmoTpl));
        var pocketTemplate = Template(pockets.Template);
        var cartridge = magTemplate.Properties?.Cartridges?.SingleOrDefault(s => s.Name == "cartridges");
        var chamber = gunTemplate.Properties?.Chambers?.SingleOrDefault(s => s.Name == "patron_in_weapon");
        bool Accepts(Slot? slot, MongoId tpl) => slot?.Properties?.Filters?.Any(f => f.Filter?.Contains(tpl) == true) == true;
        if (cartridge?.MaxCount != 17 || !Accepts(cartridge, ammoTemplate.Id) || !Accepts(chamber, ammoTemplate.Id) || ammoTemplate.Properties?.StackMaxSize < 17)
            throw Bad("the standard 17-round magazine or PBP filters/capacity were changed.");
        if (magTemplate.Properties?.Width != 1 || magTemplate.Properties?.Height != 1) throw Bad("the spare magazine no longer fits a 1x1 pocket.");
        var grid = pocketTemplate.Properties?.Grids?.FirstOrDefault(g => g.Name?.StartsWith("pocket", StringComparison.Ordinal) == true &&
            g.Properties?.CellsH >= 1 && g.Properties.CellsV >= 1 &&
            (g.Properties.Filters?.All(f => (f.Filter?.Count == 0 || f.Filter?.Any(id => InCategory(magTemplate.Id, id, templates)) == true) &&
                f.ExcludedFilter?.Any(id => InCategory(magTemplate.Id, id, templates)) != true) ?? true) &&
            !carried.Any(i => i.ParentId == pockets.Id.ToString() && i.SlotId == g.Name));
        if (grid is null) throw Bad("no free pocket grid exists after transfer.");
        var source = preset?.Items ?? throw Bad("the loaded global default M9A3 preset is missing.");
        if (source.Count == 0 || source.Select(i => i.Id).Distinct().Count() != source.Count) throw Bad("the preset has duplicate or missing items.");
        var root = source.SingleOrDefault(i => i.Id == preset!.Parent && i.Template.ToString() == WeaponTpl) ?? throw Bad("the default preset has no M9A3 root.");
        var ids = source.ToDictionary(i => i.Id.ToString(), _ => new MongoId());
        var seen = new HashSet<string> { root.Id.ToString() };
        var pending = source.Where(i => i.Id != root.Id).ToList();
        while (pending.Count > 0)
        {
            var linked = pending.Where(i => i.ParentId is not null && seen.Contains(i.ParentId)).ToList();
            if (linked.Count == 0) throw Bad("the preset has an orphan or cyclic parent link.");
            foreach (var i in linked) { seen.Add(i.Id.ToString()); pending.Remove(i); }
        }
        foreach (var parent in source)
        {
            var template = Template(parent.Template);
            foreach (var child in source.Where(i => i.ParentId == parent.Id.ToString()))
            {
                var target = template.Properties?.Slots?.SingleOrDefault(s => s.Name == child.SlotId);
                if (target?.Properties?.Filters?.Any(f => f.Filter?.Any(id => InCategory(child.Template, id, templates)) == true) != true)
                    throw Bad("a preset attachment has an incompatible slot/filter.");
            }
            foreach (var slot in template.Properties?.Slots?.Where(s => s.Required == true) ?? [])
                if (!source.Any(i => i.ParentId == parent.Id.ToString() && i.SlotId == slot.Name)) throw Bad("a required preset attachment is missing.");
        }
        var magazine = source.SingleOrDefault(i => i.ParentId == root.Id.ToString() && i.SlotId == "mod_magazine" && i.Template.ToString() == MagazineTpl)
            ?? throw Bad("the preset lacks its standard M9A3 magazine.");
        if (source.Any(i => i.ParentId == magazine.Id.ToString() || i.ParentId == root.Id.ToString() && i.SlotId == "patron_in_weapon"))
            throw Bad("the default preset unexpectedly contains ammunition.");
        var items = source.Select(i => new Item {
            Id = ids[i.Id.ToString()], Template = i.Template,
            ParentId = i.Id == root.Id ? null : ids[i.ParentId!].ToString(),
            SlotId = i.Id == root.Id ? null : i.SlotId,
            Upd = Fresh(Template(i.Template))
        }).ToList();
        var weapon = ids[root.Id.ToString()];
        var spare = new MongoId();
        items.Add(new Item { Id=spare, Template=new MongoId(MagazineTpl), Upd=Fresh(magTemplate) });
        Item Rounds(MongoId parent, string slot, int count, object? location) => new() {
            Id=new MongoId(), Template=new MongoId(AmmoTpl), ParentId=parent.ToString(), SlotId=slot, Location=location,
            Upd=new Upd { StackObjectsCount=count, SpawnedInSession=false }
        };
        items.Add(Rounds(ids[magazine.Id.ToString()], "cartridges", 17, 0));
        items.Add(Rounds(weapon, "patron_in_weapon", 1, null));
        items.Add(Rounds(spare, "cartridges", 17, 0));
        if (items.Any(i => carried.Any(c => c.Id == i.Id))) throw Bad("new item IDs collide with the profile.");
        return new(true, null, null, equipment, pockets.Id.ToString(), grid.Name, weapon.ToString(), spare.ToString(), items);
    }
    private static bool InCategory(MongoId item, MongoId filter, IDictionary<MongoId, TemplateItem> templates)
    {
        var seen = new HashSet<MongoId>();
        while (seen.Add(item))
        {
            if (item == filter) return true;
            if (!templates.TryGetValue(item, out var template)) return false;
            item = template.Parent;
        }
        return false;
    }
    private static Upd Fresh(TemplateItem template) => new() {
        StackObjectsCount=1, SpawnedInSession=false,
        Repairable=template.Properties?.MaxDurability is > 0 and var max
            ? new UpdRepairable { Durability=max, MaxDurability=max } : null
    };
}

[Injectable(TypePriority = OnLoadOrder.Routers + 3)]
public sealed class CrashsiteStarterRouter(JsonUtil jsonUtil, CrashsiteStarterLoadout starter, HttpResponseUtil response)
    : StaticRouter(jsonUtil, [new RouteAction<CrashsiteStarterRequest>(CrashsiteStarterLoadout.Route,
        (url, request, sessionId, output, cancellationToken) => new ValueTask<string>(response.GetBody(starter.Get(sessionId, request.ServerId))))]);


