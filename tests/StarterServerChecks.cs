using System;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Crashsite.Server;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

internal static class StarterServerChecks
{
    private static int checks;
    public sealed class RaidConsumer(CrashsiteStarterLoadout store) { public CrashsiteStarterLoadout Store => store; }
    public sealed class RouteConsumer(CrashsiteStarterLoadout store) { public CrashsiteStarterLoadout Store => store; }
    public static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (context,name) => {
            foreach(var dir in new[] { Path.Combine(args[0],"SPT_Runtime"), Path.GetDirectoryName(args[1]) }) {
                var file=Path.Combine(dir,name.Name+".dll"); if(File.Exists(file)) return context.LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            return null;
        };
        Run(args[0]);
        Console.WriteLine($"{checks} isolated server kit checks passed; no profile saved or game started.");
    }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); checks++; Console.WriteLine("PASS: "+message); }
    private static CrashsiteStarterGrant Build(Preset preset,Dictionary<MongoId,TemplateItem> templates,PmcData pmc) =>
        (CrashsiteStarterGrant)typeof(CrashsiteStarterLoadout).GetMethod("Build",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object?[]{preset,templates,pmc})!;
    private static void Reject(Action action,string message) {
        try { action(); } catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) { Check(true,message);return; }
        throw new Exception("Expected rejection: "+message);
    }
    private static void CheckGrantLifetime()
    {
        var services = new ServiceCollection();
        services.AddSingleton((SPTarkov.Server.Core.Helpers.Items.PresetHelper)RuntimeHelpers.GetUninitializedObject(typeof(SPTarkov.Server.Core.Helpers.Items.PresetHelper)));
        services.AddSingleton((TemplateTable)RuntimeHelpers.GetUninitializedObject(typeof(TemplateTable)));
        services.AddSingleton((SPTarkov.Server.Core.Helpers.Profile.ProfileHelper)RuntimeHelpers.GetUninitializedObject(typeof(SPTarkov.Server.Core.Helpers.Profile.ProfileHelper)));
        var handler = new SPTarkov.DI.DependencyInjectionHandler(services);
        handler.AddInjectableTypesFromTypeList(new[] { typeof(CrashsiteStarterLoadout) });
        handler.InjectAll();
        services.AddTransient<RaidConsumer>();
        services.AddTransient<RouteConsumer>();
        using var provider = services.BuildServiceProvider();
        var raid = provider.GetRequiredService<RaidConsumer>();
        var route = provider.GetRequiredService<RouteConsumer>();
        Check(ReferenceEquals(raid.Store,route.Store), "SPT DI gives the raid hook and HTTP consumer the same grant store");
        var session = new MongoId();
        var grant = new CrashsiteStarterGrant(true,null,null,"equipment","pockets","pocket1","weapon","spare",new List<Item>());
        raid.Store.Bind(session,"crashsite.di-test",grant);
        var field = typeof(CrashsiteStarterLoadout).GetField("_active",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<MongoId,CrashsiteStarterGrant>)field.GetValue(route.Store)!;
        Check(cache.TryGetValue(session,out var received) && received.ServerId=="crashsite.di-test", "Grant bound by raid consumer is visible to separate HTTP consumer");
        route.Store.Forget(session,"bigmap.di-test");
        Check(cache.ContainsKey(session),"A different raid ID cannot expire the active grant");
        route.Store.Forget(session,"crashsite.di-test");
        Check(!cache.ContainsKey(session),"HTTP and lifecycle consumers share matching grant expiration");
    }
    private static void Run(string root)
    {
        CheckGrantLifetime();
        var db=Path.Combine(root,"SPT_Runtime/SPT_Data/database");
        var json = new SPTarkov.Server.Core.Utils.JsonUtil(new[] { new SPTarkov.Server.Core.Utils.Json.SptJsonConverterRegistrator() });
        var options = SPTarkov.Server.Core.Utils.JsonUtil.JsonSerializerOptionsNoIndent;
        var templatesRaw=JsonSerializer.Deserialize<Dictionary<string,TemplateItem>>(File.ReadAllText(Path.Combine(db,"templates/items.json")),options)!;
        var templates=templatesRaw.ToDictionary(k=>new MongoId(k.Key), k=>k.Value);
        using var globals=JsonDocument.Parse(File.ReadAllText(Path.Combine(db,"globals.json")));
        var preset=globals.RootElement.GetProperty("ItemPresets").EnumerateObject().Select(p=>JsonSerializer.Deserialize<Preset>(p.Value.GetRawText(),options)!).Single(p=>p.Encyclopedia?.ToString()==CrashsiteStarterLoadout.WeaponTpl);
        var original=JsonSerializer.Serialize(preset,options);
        var harmony = new HarmonyLib.Harmony("crashsite.tests.server.patch-install");
        try {
            var diagnostics = typeof(CrashsiteStarterLoadout).Assembly.GetType("Crashsite.Server.CrashsiteRaidDiagnostics",true)!;
            var methods = harmony.CreateClassProcessor(diagnostics).Patch();
            Check(methods.Count >= 2,"Installed server Harmony lifecycle patches generate successfully");
        } finally { harmony.UnpatchSelf(); }
        foreach(var pocketTpl in new[]{"627a4e6b255f7527fb05a0f6","65e080be269cbd5c5005e529"})
        {
            var equipment=new MongoId(); var pockets=new MongoId();
            var pmc=new PmcData { Inventory=new BotBaseInventory {Equipment=equipment,Items=new List<Item> {
                new() { Id=equipment,Template=new MongoId("55d7217a4bdc2d86028b456d") },
                new() { Id=pockets,Template=new MongoId(pocketTpl),ParentId=equipment.ToString(),SlotId="Pockets" },
                new() { Id=new MongoId(),Template=new MongoId("5cadc190ae921500103bb3b6"),ParentId=pockets.ToString(),SlotId="SpecialSlot1" }
            } } };
            var before=JsonSerializer.Serialize(pmc,options);
            var kit=Build(preset,templates,pmc);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"starter-grant.json"),JsonSerializer.Serialize(kit with{ServerId="crashsite.test"},options));
            Check(kit.Accepted && kit.Items.Count==12, "Complete eight-item preset plus spare and three ammunition stacks for "+pocketTpl);
            Check(kit.EquipmentId==equipment.ToString() && kit.PocketsId==pockets.ToString() && kit.PocketGrid=="pocket1","Retained roots and first pocket selected");
            var rounds=kit.Items.Where(i=>i.Template.ToString()==CrashsiteStarterLoadout.AmmoTpl).ToArray();
            Check(rounds.Length==3 && rounds.Sum(i=>i.Upd!.StackObjectsCount)==35 && rounds.Count(i=>i.Upd!.StackObjectsCount==17)==2,"Exactly 17+1 loaded and 17 spare PBP");
            Check(kit.Items.All(i=>i.Upd!.SpawnedInSession==false) && kit.Items.Single(i=>i.Id.ToString()==kit.WeaponId).Upd!.Repairable!.Durability==100,"Full condition and non-FIR starting equipment");
            Check(JsonSerializer.Serialize(pmc,options)==before && JsonSerializer.Serialize(preset,options)==original,"Builder leaves profile, special items and global preset unchanged");
            var second=Build(preset,templates,pmc);
            Check(!kit.Items.Select(i=>i.Id).Intersect(second.Items.Select(i=>i.Id)).Any(),"Independent raids get fresh IDs");
            var lookup=typeof(CrashsiteStarterLoadout).GetMethod("Lookup",BindingFlags.NonPublic|BindingFlags.Static)!;
            var bound=kit with {ServerId="crashsite.test"};
            Check(ReferenceEquals(lookup.Invoke(null,new object[]{bound,"crashsite.test"}),bound),"Grant retry returns the identical canonical tree");
            Check(!((CrashsiteStarterGrant)lookup.Invoke(null,new object[]{bound,"bigmap.test"})!).Accepted,"Cross-raid grant ID rejected");
            Check(!((CrashsiteStarterGrant)lookup.Invoke(null,new object?[]{null,"crashsite.test"})!).Accepted,"Missing/restarted grant fails closed");
            Reject(()=>Build(null!,templates,pmc),"Missing preset rejects without changes");
            var broken=preset with {Items=preset.Items!.Where(i=>i.SlotId!="mod_barrel").ToList()};
            Reject(()=>Build(broken,templates,pmc),"Missing required attachment fails closed");
            var orphan=preset with {Items=preset.Items!.Select(i=>i.SlotId=="mod_barrel"? i with{ParentId=new MongoId().ToString()}:i).ToList()};
            Reject(()=>Build(orphan,templates,pmc),"Orphaned preset fails closed");
            pmc.Inventory.Items.Add(new Item { Id=new MongoId(),Template=new MongoId(CrashsiteStarterLoadout.WeaponTpl),ParentId=equipment.ToString(),SlotId="Holster" });
            Reject(()=>Build(preset,templates,pmc),"Occupied Holster rejects rather than replacing gear");
        }
    }
}

