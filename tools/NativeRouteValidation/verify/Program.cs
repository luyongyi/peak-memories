using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

if(args.Length!=2)throw new ArgumentException("summary.json, new analysis directory");
var summary=JObject.Parse(File.ReadAllText(args[0]));var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
bool summarySucceeded=(bool?)summary["Success"]==true;
var censusGroups=(summary["NativeCensus"] as JArray??new()).Cast<JObject>().ToArray();
var endpointTargets=censusGroups.Where(group=>(string?)group["Phase"]=="FarIdle"&&(group["SceneNativeCreatures"] is JArray||group["SceneNativeFrogs"] is JArray))
    .Select(group=>new{Census=group,NativeTime=(double)group["NativeTime"]!}).ToArray();
var endpointSamples=new Dictionary<JObject,EndpointSample>();
var path=(string)summary["RecordingFile"]!;var info=FullReplayArchive.ReadInfo(path);
double origin=(double)summary["FirstCapture"]!;
var snowVerifier=SnowRecordingVerification.OpenIfAvailable(args[0],origin);
var actions=(summary["Actions"] as JArray??new()).Cast<JObject>().ToArray();
var bindings=(summary["Bindings"] as JArray??new()).Cast<JObject>().GroupBy(x=>(string)x["Track"]!+":"+(string)x["Key"]!).ToDictionary(g=>g.Key,g=>g.OrderBy(x=>(double)x["NativeTime"]!).ToArray());
var roots=new Dictionary<string,int>(StringComparer.Ordinal);
var heartbeat=Path.Combine(Path.GetDirectoryName(args[0])!,"heartbeat.jsonl");
foreach(var line in File.ReadLines(heartbeat))
{
    var row=JObject.Parse(line);
    foreach(var root in (row["NativeRoots"] as JArray??new()).Cast<JObject>())
    {string key=(string?)root["RootPath"]??"";if(key.Length!=0)roots[key]=(int)root["Index"]!;}
}
var bodyRoots=roots.Keys.Where(x=>Array.IndexOf(info.Header.MapObjects,x)>=0).ToArray();
var transitionTimes=actions.Where(x=>(string?)x["Kind"]=="native-root-transition").Select(x=>(double)x["NativeTime"]!-origin).ToArray();
var assembly=typeof(ReplayFrame).Assembly;
var historyFactory=typeof(FullReplayArchive).GetMethod("CreateEffectPageLoader",BindingFlags.Static|BindingFlags.NonPublic)!;
var historyLoad=(Func<int,CancellationToken,ReplayClip>)historyFactory.Invoke(null,new object?[]{info,null})!;
var effectsOnly=typeof(FullReplayArchive).GetMethod("EffectsOnlyFrame",BindingFlags.Static|BindingFlags.NonPublic)!;
var comparerType=assembly.GetType("ContinuousReplayInspection+ImmutableDataComparer",true)!;var equal=comparerType.GetMethod("Equal")!;
var seen=new HashSet<long>();var creatures=new Dictionary<string,(string Kind,double First,double Last)>();
var creatureKeys=new Dictionary<string,string>(StringComparer.Ordinal);
var bodyCache=new Dictionary<string,string?>(StringComparer.Ordinal);
var kinds=new HashSet<string>();var crateClosed=new HashSet<string>();var crateOpen=new Dictionary<string,double>();
var births=new Dictionary<string,(string Resource,double Time)>();var violations=new List<object>();long leakObservations=0,unknownItems=0,checkedItems=0,checkpoints=0,checkpointBytes=0;
var frogs=new Dictionary<string,(int ItemId,string Prefab,HashSet<int> Uses,double First,double Last)>(StringComparer.Ordinal);
var highUseItems=new Dictionary<string,(int ItemId,string Prefab,HashSet<int> Uses,double First,double Last)>(StringComparer.Ordinal);
void ObserveItem(Dictionary<string,(int ItemId,string Prefab,HashSet<int> Uses,double First,double Last)> observations,ItemFrame item,double time)
{
    if(observations.TryGetValue(item.Key,out var old))
    {old.Uses.Add(item.Uses);observations[item.Key]=(item.ItemId,item.Prefab,old.Uses,Math.Min(old.First,time),Math.Max(old.Last,time));}
    else observations[item.Key]=(item.ItemId,item.Prefab,new HashSet<int>{item.Uses},time,time);
}
string? Owner(string track,string key,double time)
{
    if(!bindings.TryGetValue(track+":"+key,out var values))return null;
    JObject chosen=values[0];foreach(var value in values){if((double)value["NativeTime"]!>time+origin+.001)break;chosen=value;}
    return (string?)chosen["Owner"];
}
string? Body(string? path)
{
    if(string.IsNullOrEmpty(path)||path is "independent/global" or "unresolved")return null;
    if(bodyCache.TryGetValue(path,out var cached))return cached;
    var exact=bodyRoots.Where(root=>path==root||path.StartsWith(root+"/",StringComparison.Ordinal)).OrderByDescending(x=>x.Length).FirstOrDefault();
    if(exact!=null){bodyCache[path]=exact;return exact;}
    var envelope=bodyRoots.Where(root=>root.StartsWith(path+"/",StringComparison.Ordinal)).ToArray();var result=envelope.Length==1?envelope[0]:null;bodyCache[path]=result;return result;
}
void Scope(string track,string key,string? source,ReplayFrame frame,bool carried=false)
{
    var body=Body(source);if(body==null||carried)return;
    int index=Array.IndexOf(info.Header.MapObjects,body);if(index<0||frame.World.ActiveMapObjects[index])return;
    if(transitionTimes.Any(t=>Math.Abs(t-frame.T)<2))return; // native scope and 30Hz scene clocks may differ briefly at a transition
    leakObservations++;if(violations.Count<20)violations.Add(new{Track=track,Time=frame.T,Key=key,NativeBody=body});
}
for(int pageIndex=info.Pages.Length-1;pageIndex>=0;pageIndex--)
{
    var page=FullReplayArchive.ReadPage(path,info,pageIndex);var history=historyLoad(pageIndex,default);
    if(history.Frames.Count!=page.Frames.Count)throw new Exception("History count mismatch.");var comparer=Activator.CreateInstance(comparerType,true)!;
    for(int i=0;i<page.Frames.Count;i++)
    {
        var frame=page.Frames[i];seen.Add(info.Pages[pageIndex].FirstFrameIndex+i);
        snowVerifier?.Observe(frame);
        foreach(var target in endpointTargets)
        {
            double age=target.NativeTime-origin-frame.T;
            if(age<-.000001||age>.25||endpointSamples.TryGetValue(target.Census,out var oldSample)&&age>=oldSample.Age)continue;
            endpointSamples[target.Census]=new EndpointSample
            {
                Time=frame.T,Age=age,
                VoiceThieves=frame.Creatures.Where(creature=>creature.Kind=="voice-thief").Select(creature=>(creature.Key,creature.Pose.Active)).ToArray(),
                Frogs=frame.Items.Where(item=>item.ItemId==172).Select(item=>(item.Key,item.Uses,item.State)).ToArray(),
            };
        }
        if(!(bool)equal.Invoke(comparer,new[]{effectsOnly.Invoke(null,new object[]{frame})!,history.Frames[i]})!)throw new Exception("Independent effect history differs.");
        foreach(var creature in frame.Creatures)
        {
            creatureKeys[creature.Key]=creature.Kind;
            kinds.Add(creature.Kind);if(creatures.TryGetValue(creature.SourcePath,out var old))creatures[creature.SourcePath]=(creature.Kind,Math.Min(old.First,frame.T),Math.Max(old.Last,frame.T));else creatures[creature.SourcePath]=(creature.Kind,frame.T,frame.T);
            Scope("Creatures",creature.Key,creature.SourcePath.Length!=0?creature.SourcePath:Owner("Creatures",creature.Key,frame.T),frame);
        }
        foreach(var crate in frame.Crates)
        {if(!crate.Open)crateClosed.Add(crate.Key);else if(!crateOpen.TryGetValue(crate.Key,out double before)||frame.T<before)crateOpen[crate.Key]=frame.T;Scope("Crates",crate.Key,crate.Key,frame);}
        foreach(var item in frame.Items)
        {
            if(item.Prefab=="Frog"||item.Name=="Frog")ObserveItem(frogs,item,frame.T);
            if(item.Uses>1_000_000)ObserveItem(highUseItems,item,frame.T);
            string? owner=Owner("Items",item.Key,frame.T);if(Body(owner)==null)unknownItems++;else checkedItems++;Scope("Items",item.Key,owner,frame,item.HolderId.Length!=0);
        }
        foreach(var interaction in frame.Interactions)Scope("Interactions",interaction.Key,interaction.SourcePath,frame);
        foreach(var effect in frame.Effects)
        {
            if(!births.TryGetValue(effect.Key,out var old)||frame.T<old.Time)births[effect.Key]=(effect.Resource,frame.T);
            if(effect.Particles!=null)foreach(var particle in effect.Particles)if(particle.Checkpoint!=null){checkpoints++;checkpointBytes+=particle.Checkpoint.Data.Length+particle.Checkpoint.State.Length;}
        }
    }
}
var checks=new List<object>();int failures=0,covered=0;
foreach(var action in actions.Where(x=>(bool?)x["Success"]==true))
{
    string kind=(string?)action["Kind"]??"",key=(string?)action["Key"]??"";double time=(double)action["NativeTime"]!-origin;
    if(kind=="luggage-open")
    {
        bool ok=crateClosed.Contains(key)&&crateOpen.TryGetValue(key,out double at)&&at>=time-.25&&at<=time+3;
        checks.Add(new{Kind=kind,Biome=(string?)action["Biome"],Source=key,Time=time,NativeOpen=(bool?)action["After"],CapturedClosedBefore=crateClosed.Contains(key),FirstCapturedOpen=crateOpen.TryGetValue(key,out at)?(double?)at:null,Passed=ok});if(ok)covered++;else failures++;
    }
    if(kind=="trap-trigger")
    {
        string resource=(string)action["ExpectedResource"]!;
        bool native=actions.Any(x=>(string?)x["Kind"]=="trap-native-birth"&&(string?)x["ExpectedResource"]==resource&&(bool?)x["Success"]==true&&(double)x["NativeTime"]!>=time+origin&&(double)x["NativeTime"]!<=time+origin+3);
        var matches=births.Where(x=>x.Value.Resource.Contains(resource,StringComparison.Ordinal)&&x.Value.Time>=time-.25&&x.Value.Time<=time+3).ToArray();bool ok=native&&matches.Length>0;
        checks.Add(new{Kind=kind,Biome=(string?)action["Biome"],Resource=resource,Time=time,NativeBirthObserved=native,RecordedBirthKeys=matches.Select(x=>x.Key).ToArray(),Passed=ok});if(ok)covered++;else failures++;
    }
}
var nativeCoverage=new List<object>();int missingNative=0,unresolvedNative=0;
var known=new Dictionary<string,string>{{"Spider","spider"},{"MushroomZombie","mushroom-zombie"},{"Condor","condor"},{"FrogTongue","frog-tongue"},{"Scoutmaster","scoutmaster"},{"Beetle","beetle"},{"Antlion","antlion"},{"Capybara","capybara"},{"TumbleWeed","tumbleweed"},{"VenusFlyTrap","venus-flytrap"},{"VoiceThief","voice-thief"},{"Skelleton","skeleton"},{"ScoutmasterGhostOrbiter","scoutmaster-ghost"},{"GhostBall","ghost-ball"},{"PlayerGhost","player-ghost"}};
foreach(var group in censusGroups)
    foreach(var entity in (group["Creatures"] as JArray??new()).Cast<JObject>())
    {
        string type=(string)entity["Type"]!,source=(string)entity["Path"]!;
        if(!known.TryGetValue(type,out string? expected))
        {
            if(type!="Mob")continue;
            expected=source.Contains("Beetle",StringComparison.OrdinalIgnoreCase)?"beetle":source.Contains("Capybara",StringComparison.OrdinalIgnoreCase)?"capybara":source.Contains("Tumbleweed",StringComparison.OrdinalIgnoreCase)?"tumbleweed":null;
            if(expected==null)continue;
        }
        int? nativeId=(int?)entity["GameObjectId"];
        string nativePrefix="creature:"+nativeId+":";
        bool found=nativeId.HasValue&&creatureKeys.Any(x=>x.Key.StartsWith(nativePrefix,StringComparison.Ordinal)&&x.Value==expected) || creatures.TryGetValue(source,out var data)&&data.Kind==expected;
        if(!nativeId.HasValue&&!found){unresolvedNative++;nativeCoverage.Add(new{Biome=(string?)group["Biome"],NativeType=type,Source=source,ExpectedKind=expected,Captured=(bool?)null,Status="Native component ID was logged but its GameObject ID was not; prefab-based recording deliberately omits scene paths. Per-object match is unresolved."});continue;}
        if(!found)missingNative++;
        nativeCoverage.Add(new{Biome=(string?)group["Biome"],NativeType=type,Source=source,ExpectedKind=expected,Captured=found});
    }
bool countOk=seen.Count==info.FrameCount;
bool nativeSwampFrogObserved=censusGroups.Any(group=>(string?)group["Biome"]=="Swamp"&&(group["Creatures"] as JArray??new()).Any(entity=>(string?)entity["Type"]=="FrogTongue"));
bool? knownNativeFrogCounterMatches=nativeSwampFrogObserved?frogs.Values.Any(frog=>frog.Uses.Contains(100_000_000)):null;
var endpointChecks=new List<object>();int endpointFailures=0;
foreach(var target in endpointTargets)
{
    var group=target.Census;endpointSamples.TryGetValue(group,out var sample);
    var nativeVoiceIds=(group["SceneNativeCreatures"] as JArray??new()).Cast<JObject>().Where(entity=>(string?)entity["Type"]=="VoiceThief")
        .Select(entity=>(int)entity["GameObjectId"]!).Distinct().OrderBy(id=>id).ToArray();
    var recordedVoices=sample?.VoiceThieves??Array.Empty<(string Key,bool Active)>();
    var missingVoiceIds=nativeVoiceIds.Where(id=>!recordedVoices.Any(voice=>voice.Active&&voice.Key.StartsWith("creature:"+id+":",StringComparison.Ordinal))).ToArray();
    var unexpectedVoiceKeys=recordedVoices.Where(voice=>voice.Active&&!nativeVoiceIds.Any(id=>voice.Key.StartsWith("creature:"+id+":",StringComparison.Ordinal))).Select(voice=>voice.Key).OrderBy(key=>key,StringComparer.Ordinal).ToArray();
    var recordedFrogs=sample?.Frogs??Array.Empty<(string Key,int Uses,int State)>();
    var frogChecks=(group["SceneNativeFrogs"] as JArray??new()).Cast<JObject>().Select(native=>
    {
        int id=(int)native["GameObjectId"]!,uses=(int)native["Uses"]!;
        int? instance=(int?)native["Instance"];string source=(string?)native["Path"]??"";
        // ItemTrack keys use Item component IDs, while creature keys use
        // GameObject IDs. Old native censuses omitted the item component ID.
        // Associate those legacy samples only through one exact native path
        // in each item's most recent binding at or before this census.
        var pathBindings=instance.HasValue||source.Length==0?Array.Empty<JObject>():bindings.Where(pair=>pair.Key.StartsWith("Items:",StringComparison.Ordinal))
            .Select(pair=>pair.Value.LastOrDefault(binding=>(double)binding["NativeTime"]!<=target.NativeTime+.000001))
            .Where(binding=>binding!=null&&(string?)binding["NativeSourcePath"]==source).Select(binding=>binding!).ToArray();
        var bindingKeys=pathBindings.Select(binding=>(string)binding["Key"]!).Distinct(StringComparer.Ordinal).ToArray();
        bool oracleResolved=instance.HasValue||bindingKeys.Length==1;
        var matches=recordedFrogs.Where(frog=>instance.HasValue?frog.Key.EndsWith("/"+instance.Value,StringComparison.Ordinal):bindingKeys.Length==1&&frog.Key==bindingKeys[0]).ToArray();
        return new{GameObjectId=id,Instance=instance,NativePath=source,MatchedBy=instance.HasValue?"NativeItemInstance":"NativePathBinding",OracleResolved=oracleResolved,
            BindingCandidateKeys=bindingKeys,BindingNativeTime=pathBindings.Length==1?(double?)pathBindings[0]["NativeTime"]:null,
            NativeUses=uses,NativeState=(int?)native["State"],RecordedKeys=matches.Select(frog=>frog.Key).ToArray(),RecordedUses=matches.Select(frog=>frog.Uses).ToArray(),RecordedStates=matches.Select(frog=>frog.State).ToArray(),
            CapturedAtEndpoint=matches.Length!=0,CounterMatches=matches.Any(frog=>frog.Uses==uses),Passed=oracleResolved&&sample!=null&&matches.Length==1&&matches[0].Uses==uses};
    }).ToArray();
    bool endpointPassed=sample!=null&&missingVoiceIds.Length==0&&unexpectedVoiceKeys.Length==0&&frogChecks.All(check=>check.Passed);
    if(!endpointPassed)endpointFailures++;
    endpointChecks.Add(new{Biome=(string?)group["Biome"],Phase=(string?)group["Phase"],NativeTime=target.NativeTime,RecordingTime=target.NativeTime-origin,
        CapturedFrameTime=sample?.Time,FrameAgeSeconds=sample?.Age,MaximumFrameAgeSeconds=.25,NativeVoiceThiefIds=nativeVoiceIds,
        RecordedVoiceThieves=recordedVoices.Select(voice=>new{voice.Key,voice.Active}).ToArray(),MissingVoiceThiefIds=missingVoiceIds,UnexpectedActiveVoiceThiefKeys=unexpectedVoiceKeys,
        NativeFrogCount=frogChecks.Length,FrogChecks=frogChecks,Passed=endpointPassed});
}
var fauna=FaunaRecordingVerification.RunIfAvailable(args[0],summary,path,info,output);
var snow=snowVerifier?.Complete(output);
bool? snowPassed=snow==null?null:(bool?)JObject.FromObject(snow)["Passed"];
if ((bool?)summary["SnowVisual"]?["Requested"] == true && snow == null) snowPassed = false;
var sharedNames=new[]{"Items","Crates","Ropes","Creatures","Interactions","Spawned","Visuals","Lights","PoseNodes","Order","StaticCreatureDirectory"};
var sharedKinds=(info.SharedBlocks??Array.Empty<FullReplaySharedBlock>()).GroupBy(block=>block.Kind).OrderBy(group=>group.Key)
    .Select(group=>new{Kind=group.Key,Type=sharedNames[group.Key],Blocks=group.Count(),RawBytes=group.Sum(block=>(long)block.RawBytes),CompressedBytes=group.Sum(block=>block.Length)}).ToArray();
bool? nativeVisualPassed=summary["FaunaVisual"] is JObject visualSummary?(bool?)visualSummary["Passed"]:null;
var report=new{Version=(string?)summary["InstalledVersion"],Mvid=(string?)summary["InstalledMvid"],SummarySuccess=summarySucceeded,Complete=info.Complete,ContainerVersion=info.Version,Schema=info.Header.Schema,
    SharedBlockKinds=sharedKinds,OfflineRepack=summary["OfflineRepack"],
    Duration=info.Duration,FileBytes=new FileInfo(path).Length,TotalPages=info.Pages.Length,UniqueFrames=seen.Count,ExpectedFrames=info.FrameCount,FrameCountMatches=countOk,AllPagesReverseReadable=true,AllIndependentEffectHistoriesEqual=true,
    NativeActionChecks=checks,ActionChecksPassed=covered,ActionChecksFailed=failures,NativeCensusCoverage=nativeCoverage,NativeCensusMissing=missingNative,NativeCensusUnresolved=unresolvedNative,CoverageComplete=false,
    RecordedCreatureKinds=kinds.OrderBy(x=>x).ToArray(),RecordedEffectResources=births.Values.Select(x=>x.Resource).Distinct().OrderBy(x=>x).ToArray(),
    RecordedFrogs=frogs.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new{Key=x.Key,x.Value.ItemId,x.Value.Prefab,Uses=x.Value.Uses.OrderBy(value=>value).ToArray(),FirstObservation=x.Value.First,LastObservation=x.Value.Last}).ToArray(),
    HighUseItems=highUseItems.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new{Key=x.Key,x.Value.ItemId,x.Value.Prefab,Uses=x.Value.Uses.OrderBy(value=>value).ToArray(),FirstObservation=x.Value.First,LastObservation=x.Value.Last}).ToArray(),
    KnownNativeFrogCounterRequired=nativeSwampFrogObserved,KnownNativeFrogCounterMatches=knownNativeFrogCounterMatches,
    EndpointChecks=endpointChecks,EndpointChecksAvailable=endpointTargets.Length!=0,EndpointChecksFailed=endpointFailures,
    InactiveBodyEntryObservationsOutsideTransitionGrace=leakObservations,FirstScopeViolations=violations,ItemOwnerCheckedObservations=checkedItems,ItemOwnerUnknownOrGlobalObservations=unknownItems,
    ParticleCheckpoints=checkpoints,ParticleCheckpointBytes=checkpointBytes,Seeks=(JArray?)summary["Seeks"],
    FaunaRecording=fauna,NativeVisualPassed=nativeVisualPassed,SnowRecording=snow,
    Passed=info.Complete&&summarySucceeded&&countOk&&failures==0&&leakObservations==0&&missingNative==0&&knownNativeFrogCounterMatches!=false&&endpointFailures==0&&fauna?.Passed!=false&&nativeVisualPassed!=false&&snowPassed!=false,
    Limits="Scoped census covers active native named creature components inside current native parent roots; it can omit detached Photon scene-root births. EndpointChecks exist only when a FarIdle census supplies independent active-scene arrays; missing endpoint arrays yield no endpoint checks, not a completed endpoint comparison. Those checks use the nearest recorded frame at or before the census within 0.25s, matching VoiceThief GameObject IDs against active creature keys. Frog item keys use Item component IDs, not GameObject IDs; when an older census lacks Instance, NativePathBinding requires one unique exact native path association from each key's most recent recorder binding at or before the census. That fallback is explicitly recorder-ledger-assisted identity association, not an independent spawn oracle. Native Uses must equal the endpoint frame's original counter; historical presence alone cannot pass. Census and endpoint checks do not exercise all AI attacks. The Frog counter check verifies only the known native 100,000,000-use fixture when Swamp FrogTongue was independently observed; a match does not establish all Frog actions. HighUseItems includes only observations with Uses > 1,000,000; first/last times refer to those observations. Item owner bindings are sampled recorder diagnostics and may remain unresolved/global; they are not an independent item-spawn oracle. Inactive-root checks allow 2s transition grace. Transient native events must have separate native-birth evidence. No claim of all-map/all-multiplayer completeness."};
File.WriteAllText(Path.Combine(output,"route-verification.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
Console.WriteLine(JsonConvert.SerializeObject(new{report.Passed,report.TotalPages,report.UniqueFrames,report.ActionChecksPassed,report.ActionChecksFailed,ScopeViolations=leakObservations,report.ParticleCheckpoints,NativeCensusEntities=nativeCoverage.Count}));
if(!report.Passed)throw new InvalidDataException("Native route verification failed; inspect route-verification.json and fauna-recording-verification.json.");

sealed class EndpointSample
{
    internal double Time,Age;
    internal (string Key,bool Active)[] VoiceThieves=Array.Empty<(string Key,bool Active)>();
    internal (string Key,int Uses,int State)[] Frogs=Array.Empty<(string Key,int Uses,int State)>();
}
