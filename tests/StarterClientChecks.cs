using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class StarterClientChecks
{
    private static Type plugin; private static int checks;
    private const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Static;
    public sealed class Settings { public string serverId="bigmap.test"; }
    public sealed class Raid { public string location="bigmap"; }
    public sealed class Session { public Task<Settings> LocalRaidStarted(Raid settings) => Task.FromResult(new Settings()); }
    public sealed class Root { public Address CurrentAddress; }
    public sealed class Address { public object Container=new object(); public object Remove(Root root,bool simulate) {root.CurrentAddress=null; return new {Failed=false};} }
    private static void Check(bool ok,string name){if(!ok) throw new Exception(name);checks++;Console.WriteLine("PASS: "+name);}
    private static object Call(string name,params object[] args)=>plugin.GetMethod(name,Flags).Invoke(null,args);
    private static string Value(JObject data,string name)=>(string)data[name]??(string)data[char.ToLowerInvariant(name[0])+name.Substring(1)];
    private static void Validate(JObject grant)=>Call("ValidateStarterGrant",grant,"crashsite.test",Value(grant,"EquipmentId"),Value(grant,"PocketsId"));
    private static void Reject(JObject grant,string name){try{Validate(grant);}catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException){Check(true,name);return;}throw new Exception("Expected rejection: "+name);}
    public static int Main(string[] args)
    {
        var probes=new[]{Path.Combine(args[0],"BepInEx/core"),Path.Combine(args[0],"EscapeFromTarkov_Data/Managed"),Path.Combine(args[0],"BepInEx/plugins/spt")};
        AppDomain.CurrentDomain.AssemblyResolve+=(sender,request)=>{
            var name=new AssemblyName(request.Name).Name;var loaded=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==name);
            if(loaded!=null)return loaded;
            foreach(var dir in probes){var file=Path.Combine(dir,name+".dll");if(File.Exists(file))return Assembly.Load(File.ReadAllBytes(file));}return null;
        };
        return Run(args[1],args[2]);
    }
    private static int Run(string dll,string fixture)
    {
        plugin=Assembly.LoadFrom(dll).GetType("Crashsite.Client.CrashsiteClientPlugin",true);
        var grant=JObject.Parse(File.ReadAllText(fixture));
        Validate(grant);Check(true,"Canonical server grant accepted by compiled client");
        var serializer=JsonSerializer.CreateDefault();
        var eft=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Assembly-CSharp")??Assembly.Load("Assembly-CSharp");
        serializer.Converters.Add((JsonConverter)Activator.CreateInstance(eft.GetType("EFT.UnparsedDataConverter",true)));
        var flat=(Array)((JArray)grant["Items"]).ToObject(eft.GetType("JsonType.FlatItem",true).MakeArrayType(),serializer);
        Check(flat.Length==12,"All 12 native FlatItem DTOs deserialize with installed converters");
        var part=flat.Cast<object>().First(i=>i.GetType().GetProperty("StringParentId").GetValue(i,null)!=null);
        Check(((JArray)grant["Items"]).Any(i=>(string)i["parentId"]==(string)part.GetType().GetProperty("StringParentId").GetValue(part,null)),"Native DTO parent links survive server JSON transport");
        var changed=(JObject)grant.DeepClone();changed["ServerId"]="bigmap.test";Reject(changed,"Different accepted raid ID rejected");
        changed=(JObject)grant.DeepClone();changed["EquipmentId"]=null;Reject(changed,"Missing equipment root rejected");
        changed=(JObject)grant.DeepClone();((JArray)changed["Items"])[1]["_id"]=((JArray)changed["Items"])[0]["_id"];Reject(changed,"Duplicate item IDs rejected");
        changed=(JObject)grant.DeepClone();((JArray)changed["Items"]).First(i=>(string)i["_tpl"]=="5efb0da7a29a85116f6ea05f")["upd"]["StackObjectsCount"]=16;Reject(changed,"Underfilled PBP magazine rejected");
        changed=(JObject)grant.DeepClone();var child=((JArray)changed["Items"]).First(i=>i["parentId"]!=null);child["parentId"]=child["_id"];Reject(changed,"Cyclic attachment graph rejected");
        changed=(JObject)grant.DeepClone();((JArray)changed["Items"]).First(i=>i["parentId"]!=null)["parentId"]="missing";Reject(changed,"Orphan attachment graph rejected");
        changed=(JObject)grant.DeepClone();((JArray)changed["Items"]).First(i=>(string)i["_id"]==Value(changed,"WeaponId"))["parentId"]="original-inventory";Reject(changed,"Grant cannot parent the pistol into original inventory");
        Check(!(bool)Call("IsStarterLocation","bigmap")&&(bool)Call("IsStarterLocation","CRASHSITE"),"Starter hook is Crashsite-only");
        var harmony=new Harmony("crashsite.tests.starter.postfix");
        try {
            var postfix=plugin.GetMethod("StarterRaidPostfix",Flags).MakeGenericMethod(typeof(Settings));
            var wrapper=harmony.Patch(typeof(Session).GetMethod("LocalRaidStarted"),postfix:new HarmonyMethod(postfix));
            var task=(Task<Settings>)wrapper.Invoke(null,new object[]{new Session(),new Raid()});
            Check(task.Result.serverId=="bigmap.test","Harmony-generated typed async postfix leaves Customs task intact");
        }finally{harmony.UnpatchSelf();}
        var installed = new Harmony("crashsite.tests.starter.installed-postfix");
        try {
            var actual = eft.GetType("EFT.EftClientBackendSession",true).GetMethod("LocalRaidStarted");
            var postfix = plugin.GetMethod("StarterRaidPostfix",Flags).MakeGenericMethod(eft.GetType("JsonType.LocalSettings",true));
            Check(installed.Patch(actual,postfix:new HarmonyMethod(postfix))!=null,"Installed client raid-start postfix generates successfully");
        } finally { installed.UnpatchSelf(); }
        var roots=(IList)plugin.GetField("_starterRoots",Flags).GetValue(null);
        var one=new Root{CurrentAddress=new Address()};var two=new Root{CurrentAddress=new Address()};var original=new Root{CurrentAddress=new Address()};
        roots.Add(one);roots.Add(two);Call("CleanupUnstartedStarter");
        Check(one.CurrentAddress==null&&two.CurrentAddress==null&&original.CurrentAddress!=null&&roots.Count==0,"Failed-start cleanup removes only recorded grant roots");
        roots.Add(original);plugin.GetField("_starterWorldStarted",Flags).SetValue(null,true);Call("CleanupUnstartedStarter");
        Check(original.CurrentAddress!=null&&roots.Count==0,"Started raid equipment is left to normal result reconciliation");
        Console.WriteLine(checks+" managed starter client checks passed; gameplay not exercised.");return 0;
    }
}

