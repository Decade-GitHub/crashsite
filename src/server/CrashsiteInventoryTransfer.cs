using System.Collections.Concurrent;
using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Inventory;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace Crashsite.Server;

internal static class CrashsiteInventoryTransferState
{
    public const string TransferRoute = "/crashsite/inventory/transfer";
    public const string AckRoute = "/crashsite/inventory/ack";

    private static readonly ConcurrentDictionary<MongoId, SemaphoreSlim> SessionLocks = new();
    private static readonly ConcurrentDictionary<string, CachedTransfer> CachedTransfers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<MongoId, byte> ReadySessions = new();

    public static SemaphoreSlim LockFor(MongoId sessionId) =>
        SessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

    public static bool TryGetCached(string operationId, MongoId sessionId, string fingerprint, out CrashsiteTransferResponse response)
    {
        if (CachedTransfers.TryGetValue(operationId, out var cached))
        {
            response = cached.SessionId == sessionId &&
                       string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? cached.Response
                : CrashsiteTransferResponse.Rejected("This transfer request ID was already used for different inventory data.");
            return true;
        }

        response = null!;
        return false;
    }

    public static void Cache(string operationId, MongoId sessionId, string fingerprint, CrashsiteTransferResponse response) =>
        CachedTransfers[operationId] = new CachedTransfer(sessionId, fingerprint, response);

    public static void Acknowledge(string operationId, MongoId sessionId)
    {
        if (CachedTransfers.TryGetValue(operationId, out var cached) && cached.SessionId == sessionId)
        {
            CachedTransfers.TryRemove(operationId, out _);
        }
    }

    public static void MarkReady(MongoId sessionId) => ReadySessions[sessionId] = 1;

    public static bool ConsumeReady(MongoId sessionId) => ReadySessions.TryRemove(sessionId, out _);

    private sealed record CachedTransfer(MongoId SessionId, string Fingerprint, CrashsiteTransferResponse Response);
}

public sealed record CrashsiteTransferRequest : IRequestData
{
    public string? OperationId { get; init; }
    public string? Location { get; init; }
    public List<string>? RootIds { get; init; }
}

public sealed record CrashsiteTransferAckRequest : IRequestData
{
    public string? OperationId { get; init; }
}

public sealed record CrashsiteMovedRoot(string Id, int X, int Y, bool Rotation);

public sealed record CrashsiteTransferResponse(
    bool Accepted,
    string? Reason,
    string? OperationId,
    IReadOnlyList<CrashsiteMovedRoot> MovedRoots)
{
    public static CrashsiteTransferResponse Rejected(string reason) =>
        new(false, reason, null, Array.Empty<CrashsiteMovedRoot>());
}

public sealed record CrashsiteTransferAckResponse(bool Accepted);

[Injectable(TypePriority = OnLoadOrder.Routers + 2)]
public sealed class CrashsiteInventoryTransferRouter(JsonUtil jsonUtil, CrashsiteInventoryTransferCallback callback)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<CrashsiteTransferRequest>(
            CrashsiteInventoryTransferState.TransferRoute,
            (url, request, sessionId, output, cancellationToken) =>
                callback.TransferAsync(request, sessionId, cancellationToken)),
        new RouteAction<CrashsiteTransferAckRequest>(
            CrashsiteInventoryTransferState.AckRoute,
            (url, request, sessionId, output, cancellationToken) =>
                callback.AcknowledgeAsync(request, sessionId)),
    ]);

[Injectable]
public sealed class CrashsiteInventoryTransferCallback(
    InventoryHelper inventoryHelper,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    ICloner cloner,
    ISptLogger<CrashsiteInventoryTransferCallback> logger,
    HttpResponseUtil responseUtil)
{
    public async ValueTask<string> TransferAsync(
        CrashsiteTransferRequest request,
        MongoId sessionId,
        CancellationToken cancellationToken)
    {
        var operationId = request.OperationId?.Trim();
        if (!CrashsiteClientHandshake.IsReady(sessionId))
        {
            return Body(CrashsiteTransferResponse.Rejected("The matching Crashsite client is not connected."));
        }

        if (!string.Equals(request.Location, CrashsiteLocationProof.LocationKey, StringComparison.OrdinalIgnoreCase))
        {
            return Body(CrashsiteTransferResponse.Rejected("Inventory transfer is only available for Crashsite."));
        }

        if (!Guid.TryParse(operationId, out _))
        {
            return Body(CrashsiteTransferResponse.Rejected("The Crashsite inventory request ID is invalid."));
        }

        if (request.RootIds is null || request.RootIds.Any(string.IsNullOrWhiteSpace) ||
            request.RootIds.Distinct(StringComparer.Ordinal).Count() != request.RootIds.Count)
        {
            return Body(CrashsiteTransferResponse.Rejected("The client inventory move list is invalid."));
        }

        var requestedIds = request.RootIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var fingerprint = string.Join("|", requestedIds);
        var gate = CrashsiteInventoryTransferState.LockFor(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check before replaying cached success or staging/saving any profile changes.
            var accessRejection = CrashsiteAccess.GetRejection(profileHelper, sessionId);
            if (accessRejection is not null)
                return Body(CrashsiteTransferResponse.Rejected(accessRejection));

            if (CrashsiteInventoryTransferState.TryGetCached(operationId!, sessionId, fingerprint, out var cached))
            {
                if (cached.Accepted)
                {
                    CrashsiteInventoryTransferState.MarkReady(sessionId);
                }

                return Body(cached);
            }

            var profile = profileHelper.GetFullProfile(sessionId);
            var pmc = profile?.CharacterData?.PmcData;
            if (pmc?.Inventory?.Items is null)
            {
                return Body(CrashsiteTransferResponse.Rejected("The active PMC inventory is unavailable."));
            }

            if (HasQuestRaidItems(pmc.Inventory))
            {
                return Body(CrashsiteTransferResponse.Rejected(
                    "Crashsite cannot start while Quest Raid Items are present. Remove them or finish the related raid first."));
            }

            var plan = BuildMovePlan(pmc);
            if (plan.Error is not null)
            {
                return Body(CrashsiteTransferResponse.Rejected(plan.Error));
            }

            var planIds = plan.Roots.Select(root => root.Id).ToHashSet(StringComparer.Ordinal);
            if (planIds.Any(id => !requestedIds.Contains(id, StringComparer.Ordinal)))
            {
                return Body(CrashsiteTransferResponse.Rejected(
                    "The client and server inventories do not match. Return to the menu, reload the profile, and ready again."));
            }

            var alreadyMovedIds = requestedIds.Where(id => !planIds.Contains(id)).ToArray();
            var existingItems = pmc.Inventory.Items.ToDictionary(item => item.Id.ToString(), StringComparer.Ordinal);
            var stashId = pmc.Inventory.Stash?.ToString();
            var alreadyMovedRoots = new List<Item>(alreadyMovedIds.Length);
            foreach (var id in alreadyMovedIds)
            {
                if (string.IsNullOrWhiteSpace(stashId) ||
                    !existingItems.TryGetValue(id, out var existingItem) ||
                    !string.Equals(existingItem.ParentId, stashId, StringComparison.Ordinal) ||
                    !string.Equals(existingItem.SlotId, "hideout", StringComparison.OrdinalIgnoreCase))
                {
                    return Body(CrashsiteTransferResponse.Rejected(
                        "The client and server inventories do not match. Return to the menu, reload the profile, and ready again."));
                }

                alreadyMovedRoots.Add(existingItem);
            }

            if (requestedIds.Length == 0 || (plan.Roots.Count == 0 && alreadyMovedRoots.Count == 0))
            {
                var emptySuccess = new CrashsiteTransferResponse(true, null, operationId, Array.Empty<CrashsiteMovedRoot>());
                CrashsiteInventoryTransferState.Cache(operationId!, sessionId, fingerprint, emptySuccess);
                CrashsiteInventoryTransferState.MarkReady(sessionId);
                CrashsiteLog.DebugInfo(logger, $"Crashsite M3 inventory transfer ready for session {sessionId}; no carried item trees required moving.");
                return Body(emptySuccess);
            }

            if (plan.Roots.Count > 0)
            {
                var originalRootStates = plan.Roots.ToDictionary(
                    root => root.Id,
                    root => new RootState(root.Item.ParentId, root.Item.SlotId, cloner.Clone(root.Item.Location)),
                    StringComparer.Ordinal);

                try
                {
                    foreach (var root in plan.Roots)
                    {
                        root.Item.ParentId = pmc.Inventory.Stash?.ToString();
                        root.Item.SlotId = "hideout";
                        root.Item.Location = cloner.Clone(root.PlacementItem.Location);
                    }

                    await saveServer.SaveProfileAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RestoreRoots(plan.Roots, originalRootStates);
                    try
                    {
                        await saveServer.SaveProfileAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception restoreException)
                    {
                        CrashsiteLog.Error(logger, $"Crashsite M3 profile save and in-memory restore both failed for {sessionId}: {restoreException}");
                    }

                    CrashsiteLog.Error(logger, $"Crashsite M3 transfer was rolled back for {sessionId} because the profile save failed: {exception}");
                    return Body(CrashsiteTransferResponse.Rejected(
                        "Crashsite could not safely save the inventory transfer. The raid was blocked; retry after checking the server log."));
                }
            }

            var moved = plan.Roots
                .Select(root => new CrashsiteMovedRoot(
                    root.Id,
                    root.Placement.X ?? -1,
                    root.Placement.Y ?? -1,
                    root.Placement.Rotation ?? false))
                .Concat(alreadyMovedRoots.Select(DescribeStashedRoot))
                .OrderBy(root => root.Id, StringComparer.Ordinal)
                .ToArray();
            var success = new CrashsiteTransferResponse(true, null, operationId, moved);
            CrashsiteInventoryTransferState.Cache(operationId!, sessionId, fingerprint, success);
            CrashsiteInventoryTransferState.MarkReady(sessionId);
            CrashsiteLog.DebugSuccess(logger, 
                $"Crashsite M3 moved {moved.Length} carried item trees into stash for session {sessionId}; " +
                "item IDs and descendant records were preserved.");
            return Body(success);
        }
        catch (Exception exception)
        {
            CrashsiteLog.Error(logger, $"Crashsite M3 could not prepare inventory for session {sessionId}: {exception}");
            return Body(CrashsiteTransferResponse.Rejected(
                "Crashsite could not validate the inventory safely. The raid was blocked; retry after checking the server log."));
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask<string> AcknowledgeAsync(CrashsiteTransferAckRequest request, MongoId sessionId)
    {
        if (!string.IsNullOrWhiteSpace(request.OperationId))
        {
            CrashsiteInventoryTransferState.Acknowledge(request.OperationId.Trim(), sessionId);
        }

        return new ValueTask<string>(responseUtil.GetBody(new CrashsiteTransferAckResponse(true)));
    }

    private string Body(CrashsiteTransferResponse response) => responseUtil.GetBody(response);

    private MovePlan BuildMovePlan(PmcData pmc)
    {
        var inventory = pmc.Inventory;
        var items = inventory?.Items;
        if (inventory is null || items is null)
        {
            return MovePlan.Failed("The PMC inventory item list is unavailable; no items were changed.");
        }

        var byId = new Dictionary<string, Item>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = item.Id.ToString();
            if (string.IsNullOrWhiteSpace(id) || !byId.TryAdd(id, item))
            {
                return MovePlan.Failed("The PMC inventory contains duplicate or invalid item IDs; no items were changed.");
            }
        }

        // One pass over the whole PMC item list (stash included) replaces the
        // per-node full-list scans used to find children in v0.3.0.
        var childrenByParent = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ParentId))
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(item.ParentId, out var siblings))
            {
                siblings = [];
                childrenByParent.Add(item.ParentId, siblings);
            }

            siblings.Add(item);
        }

        // Equipment roots are handled in three ways:
        //  - Scabbard, Dogtag, ArmBand: retained with their contents.
        //  - SecuredContainer and Pockets: the root item stays equipped (neither can be
        //    placed in a stash), its grid contents move. In 4.1.6 the Special Inventory
        //    slots (SpecialSlot1-3) are slots on the Pockets item, not equipment slots,
        //    so they are retained here as Pockets children.
        //  - Everything else moves as one intact tree.
        var equipmentId = inventory.Equipment.ToString();
        var roots = new List<Item>();
        foreach (var equipped in ChildrenOf(childrenByParent, equipmentId))
        {
            if (IsRetainedEquipmentSlot(equipped.SlotId))
            {
                continue;
            }

            if (IsContentsOnlyEquipmentSlot(equipped.SlotId))
            {
                roots.AddRange(ChildrenOf(childrenByParent, equipped.Id.ToString())
                    .Where(child => !IsRetainedChildSlot(child.SlotId)));
                continue;
            }

            roots.Add(equipped);
        }

        roots = roots
            .GroupBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        if (roots.Count == 0)
        {
            return new MovePlan([], null);
        }

        var stashId = inventory.Stash?.ToString();
        if (string.IsNullOrWhiteSpace(stashId))
        {
            return MovePlan.Failed("The PMC stash is unavailable; no items were changed.");
        }

        // v0.3.0 also ran InventoryHelper.CanPlaceItemsInInventory first. In 4.1.6 that
        // helper builds the same stash map and runs the same FindSlotForItem/fill steps
        // as PlaceItemInContainer below, so it duplicated the whole calculation (plus a
        // deep clone of every tree) without adding any safety. The loop below already
        // fails before any mutation if one tree does not fit.
        var stashMap = GetStashSlotMap(pmc);
        var planned = new List<PlannedRoot>(roots.Count);
        foreach (var root in roots)
        {
            if (!TryBuildTree(root, childrenByParent, byId, out var tree))
            {
                return MovePlan.Failed("An item tree has a missing parent or cycle; no items were changed.");
            }

            // PlaceItemInContainer writes ParentId/SlotId/Location only on element [0]
            // and reads the rest to size the tree, so only the root needs a copy.
            var placementRoot = cloner.Clone(root)
                ?? throw new InvalidOperationException("Could not copy an item root for stash planning.");
            tree[0] = placementRoot;
            var placement = inventoryHelper.PlaceItemInContainer(stashMap, tree, stashId, "hideout");
            if (placement.Success != true || placement.X is null || placement.Y is null)
            {
                return MovePlan.Failed("There is not enough stash space for all carried Crashsite items. Free stash space and ready again.");
            }

            // In 4.1.6 a caught map-fill exception still returns the successful search
            // result without writing the root, so verify the write itself.
            if (placementRoot.Location is null ||
                !string.Equals(placementRoot.ParentId, stashId, StringComparison.Ordinal) ||
                !string.Equals(placementRoot.SlotId, "hideout", StringComparison.Ordinal))
            {
                return MovePlan.Failed("SPT could not calculate a stash position for a carried item. No items were changed.");
            }

            planned.Add(new PlannedRoot(root.Id.ToString(), root, placementRoot, placement));
        }

        return new MovePlan(planned, null);
    }

    private static IReadOnlyList<Item> ChildrenOf(IReadOnlyDictionary<string, List<Item>> childrenByParent, string parentId) =>
        childrenByParent.TryGetValue(parentId, out var children) ? children : (IReadOnlyList<Item>)Array.Empty<Item>();

    private static bool TryBuildTree(
        Item root,
        IReadOnlyDictionary<string, List<Item>> childrenByParent,
        IReadOnlyDictionary<string, Item> byId,
        out List<Item> tree)
    {
        // The root is always element [0]; PlaceItemInContainer relies on that order.
        tree = [];
        var pending = new Stack<Item>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var id = current.Id.ToString();
            if (!visited.Add(id) || !byId.ContainsKey(id))
            {
                return false;
            }

            tree.Add(current);
            foreach (var child in ChildrenOf(childrenByParent, id))
            {
                pending.Push(child);
            }
        }

        return true;
    }

    private static bool HasQuestRaidItems(BotBaseInventory inventory)
    {
        var questRaidRootId = inventory.QuestRaidItems.ToString();
        return inventory.Items?.Any(item =>
            string.Equals(item.ParentId, questRaidRootId, StringComparison.Ordinal)) == true;
    }

    // Report the real stash cell of a root the server had already moved, so the
    // client can align its local stash view with the server instead of keeping
    // its own placement.
    private static CrashsiteMovedRoot DescribeStashedRoot(Item item)
    {
        var location = SPTarkov.Server.Core.Extensions.ItemExtensions.GetParsedLocation(item);
        return location?.X is int x && location.Y is int y
            ? new CrashsiteMovedRoot(item.Id.ToString(), x, y, SPTarkov.Server.Core.Extensions.ItemExtensions.IsVertical(location))
            : new CrashsiteMovedRoot(item.Id.ToString(), -1, -1, false);
    }

    // The 4.1.6 helper is protected, while its public placement helpers accept
    // the resulting grid. Keep this reflection target narrow and version-pinned;
    // resolve it once instead of on every ready request.
    private static readonly MethodInfo? StashSlotMapMethod = typeof(InventoryHelper).GetMethod(
        "GetStashSlotMap",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(PmcData)],
        modifiers: null);

    private int[,] GetStashSlotMap(PmcData pmc)
    {
        var method = StashSlotMapMethod
            ?? throw new MissingMethodException(typeof(InventoryHelper).FullName, "GetStashSlotMap(PmcData)");

        return method.Invoke(inventoryHelper, [pmc]) as int[,]
            ?? throw new InvalidOperationException("SPT did not return a stash occupancy map.");
    }

    // Equipment roots kept with all of their contents.
    private static bool IsRetainedEquipmentSlot(string? slotId) =>
        string.Equals(slotId, "Scabbard", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(slotId, "Dogtag", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(slotId, "ArmBand", StringComparison.OrdinalIgnoreCase) ||
        IsSpecialSlot(slotId);

    // Equipment roots that stay equipped while their grid contents move to stash.
    private static bool IsContentsOnlyEquipmentSlot(string? slotId) =>
        string.Equals(slotId, "SecuredContainer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(slotId, "Pockets", StringComparison.OrdinalIgnoreCase);

    // Children of a contents-only root that stay carried (Special Inventory on Pockets).
    private static bool IsRetainedChildSlot(string? slotId) => IsSpecialSlot(slotId);

    private static bool IsSpecialSlot(string? slotId) =>
        slotId?.StartsWith("SpecialSlot", StringComparison.OrdinalIgnoreCase) == true;

    private static void RestoreRoots(IReadOnlyList<PlannedRoot> roots, IReadOnlyDictionary<string, RootState> states)
    {
        foreach (var root in roots)
        {
            if (!states.TryGetValue(root.Id, out var state))
            {
                continue;
            }

            root.Item.ParentId = state.ParentId;
            root.Item.SlotId = state.SlotId;
            root.Item.Location = state.Location;
        }
    }

    private sealed record MovePlan(List<PlannedRoot> Roots, string? Error)
    {
        public static MovePlan Failed(string error) => new([], error);
    }

    private sealed record PlannedRoot(string Id, Item Item, Item PlacementItem, FindSlotResult Placement);
    private sealed record RootState(string? ParentId, string? SlotId, object? Location);
}
