using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using PeakReplayLab;
using Peak.Network;
using Photon.Pun;
using Portningsbolaget.Photon;
using UnityEngine;
using UnityEngine.SceneManagement;
using UTime = UnityEngine.Time;

namespace LocalRouteAudit;

[BepInPlugin("local.peakreplay.route-audit.0827", "Protected Native Route Audit 0827", "0.0.1")]
[BepInDependency("cn.mylus.peakreplaylab", BepInDependency.DependencyFlags.HardDependency)]
[DefaultExecutionOrder(31000)]
public sealed class RouteAuditProbe : BaseUnityPlugin
{
    private enum Phase { Title, Disconnect, Load, Arrival, BaselineIdle, WarpBox, HoldBox, EventObservation, FarIdle, WarpFire, HoldFire, Transition, Seal, ReturnTitle, ReplayOpen, Seek, Advance, ReplayClose, Ending, Done }
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static RouteAuditProbe? active;
    private static bool blockWrites;
    private static bool AllowWrite() => !blockWrites;
    private readonly Harmony harmony = new("local.peakreplay.route-audit.0827");
    private readonly ConcurrentQueue<object> rows = new();
    private readonly System.Threading.AutoResetEvent wake = new(false);
    private Task? rowWriter;
    private volatile bool endWriter;
    private string writerError = "";
    private int invalidJointDiagnosticWritten;
    private string invalidJointDiagnosticError = "";
    private int invalidItemDiagnosticWritten;
    private string invalidItemDiagnosticError = "";
    private readonly List<object> actions = new(), census = new(), stages = new(), seeks = new();
    private readonly List<object> baselineStarts = new();
    private readonly List<object> bindings = new();
    private readonly Dictionary<string,string> lastBindings = new(StringComparer.Ordinal);
    private readonly List<double> biomeTimes = new();
    private readonly Dictionary<string, string> configHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Meter captureMeter = new(), sampleMeter = new(), prepareMeter = new(), probeMeter = new(), frameMeter = new();
    private readonly Process process = Process.GetCurrentProcess();
    private Plugin? owner;
    private ReplayAutomation? api;
    private ReplayFrame? latest;
    private ReplayAutomationRecordingResult? result;
    private IEnumerator? disconnect;
    private AsyncOperation? loading;
    private NativeSegmentInfo? segment;
    private GameObject? oldRoot;
    private Luggage? luggage;
    private double holdSeconds, firstCapture = double.NaN, lastCapture, phaseAt, begun, nextPoll, nextHeartbeat, trapAt, requestedAt, playFrom;
    private double arrivalReadySince = double.NaN;
    private object? lastArrivalReadiness;
    private double[] targets = Array.Empty<double>();
    private int seekIndex, observerErrors, completedBiomes, firstWait = 150, laterWait = 45, farWait = 45, finalWait = 120, maxBiomes = 5, observeFrom;
    private long accepted;
    private bool armed, owned, cleaning, restored, completedRoute, oldSave, oldOffline, trapPoison, trapExplo;
    private int oldAscent;
    private UnityEngine.Random.State oldRandom;
    private string label = "", output = "", savePath = "", beforeHash = "", afterHash = "", error = "", cleanupError = "", rollingError = "", recording = "", scene = "Level_0";
    private Phase phase;
    private BakedSceneInfo[] bakedScenes = Array.Empty<BakedSceneInfo>();
    private bool faunaVisualRequested;
    private FaunaVisualAudit? faunaVisual;

    private sealed class Meter
    {
        private readonly double[] recent = new double[2048], sorted = new double[2048];
        private int cursor, count;
        internal long Calls, Over16Ms, Over50Ms, Over100Ms;
        internal double TotalMs, MaximumMs, StageMaximumMs;
        internal void Add(double ms)
        { Calls++; TotalMs += ms; MaximumMs = Math.Max(MaximumMs, ms); StageMaximumMs = Math.Max(StageMaximumMs, ms); if(ms>16.667)Over16Ms++;if(ms>50)Over50Ms++;if(ms>100)Over100Ms++;recent[cursor]=ms;cursor=(cursor+1)%recent.Length;count=Math.Min(count+1,recent.Length); }
        internal void Stage() { cursor=count=0;StageMaximumMs=0; }
        internal object Snapshot()
        { Array.Copy(recent,sorted,count);Array.Sort(sorted,0,count);return new {Calls,TotalMs,MaximumMs,StageMaximumMs,RecentP95Ms=count==0?0:sorted[Math.Max(0,(int)Math.Ceiling(count*.95)-1)],Over16Ms,Over50Ms,Over100Ms}; }
    }
    private readonly struct CaptureStart
    {
        internal readonly long Ticks, Accepted;
        internal CaptureStart(long ticks,long accepted){Ticks=ticks;Accepted=accepted;}
    }
    private static void BeforeCapture(out CaptureStart __state) => __state = new CaptureStart(Stopwatch.GetTimestamp(), active?.accepted ?? 0);
    private static Exception? AfterCapture(CaptureStart __state, Exception? __exception)
    {
        var p=active;if(p==null)return __exception;
        double ms=(Stopwatch.GetTimestamp()-__state.Ticks)*1000d/Stopwatch.Frequency;
        p.captureMeter.Add(ms);if(p.accepted>__state.Accepted)p.sampleMeter.Add(ms);
        if(__exception!=null&&p.rollingError.Length==0)p.rollingError=__exception.GetType().Name+": "+__exception.Message;
        return __exception;
    }
    private static void BeforePrepare(out long __state)=>__state=Stopwatch.GetTimestamp();
    private static Exception? AfterPrepare(long __state,Exception? __exception)
    {var p=active;if(p!=null){p.prepareMeter.Add((Stopwatch.GetTimestamp()-__state)*1000d/Stopwatch.Frequency);if(__exception!=null&&p.rollingError.Length==0)p.rollingError=__exception.GetType().Name+": "+__exception.Message;}return __exception;}
    private static Exception? AfterJointValidation(NodePose[]? __0,Exception? __exception)
    {
        // Failure-only diagnostic: no Unity calls, actor identity or whole frames.
        // The validator also runs on the recording worker; capture its first
        // failed input before native scene cleanup can block the route driver.
        var p=active;
        if(__exception==null||p==null||Interlocked.CompareExchange(ref p.invalidJointDiagnosticWritten,1,0)!=0)return __exception;
        try
        {
            var nodes=__0?.Select((joint,index)=>joint==null
                ? new{Index=index,IsNull=true,Path=(string?)null,SampleTime=(double?)null,Position=(float[]?)null,Rotation=(float[]?)null,Scale=(float[]?)null}
                : new{Index=index,IsNull=false,Path=(string?)joint.Path,SampleTime=(double?)joint.SampleTime,
                    Position=joint.Position==null?null:(float[])joint.Position.Clone(),Rotation=joint.Rotation==null?null:(float[])joint.Rotation.Clone(),Scale=joint.Scale==null?null:(float[])joint.Scale.Clone()}).ToArray();
            File.WriteAllText(Path.Combine(p.output,"invalid-joints-first.json"),JsonConvert.SerializeObject(new
            {
                ManagedThreadId=Thread.CurrentThread.ManagedThreadId,ExceptionType=__exception.GetType().FullName,
                ExceptionMessage=__exception.Message,StackTrace=__exception.StackTrace,JointCount=__0?.Length,Joints=nodes,
            },Formatting.Indented,new JsonSerializerSettings{FloatFormatHandling=FloatFormatHandling.String}));
        }
        catch(Exception diagnosticError){p.invalidJointDiagnosticError=diagnosticError.GetType().Name+": "+diagnosticError.Message;}
        return __exception;
    }
    private static Exception? AfterItemValidation(ItemFrame? __0,Exception? __exception)
    {
        var p=active;
        if(__exception==null||p==null||Interlocked.CompareExchange(ref p.invalidItemDiagnosticWritten,1,0)!=0)return __exception;
        try
        {
            // Store the requested native item values only. Holder identifiers
            // may identify participants, so diagnostics retain only their size.
            var item=__0==null?null:new
            {
                __0.ItemId,__0.Key,__0.Prefab,__0.Name,__0.State,__0.RevealBackpackContents,__0.Progress,
                __0.Uses,__0.Fuel,__0.Cooked,Color=__0.Color==null?null:(float[])__0.Color.Clone(),HolderIdLength=__0.HolderId?.Length,
            };
            File.WriteAllText(Path.Combine(p.output,"invalid-item-first.json"),JsonConvert.SerializeObject(new
            {
                ManagedThreadId=Thread.CurrentThread.ManagedThreadId,ExceptionType=__exception.GetType().FullName,
                ExceptionMessage=__exception.Message,StackTrace=__exception.StackTrace,Item=item,
            },Formatting.Indented,new JsonSerializerSettings{FloatFormatHandling=FloatFormatHandling.String}));
        }
        catch(Exception diagnosticError){p.invalidItemDiagnosticError=diagnosticError.GetType().Name+": "+diagnosticError.Message;}
        return __exception;
    }
    private static void Accepted(object __0)
    {var p=active;if(p==null||!p.armed||p.cleaning||p.phase>=Phase.Seal)return;try{p.latest=(ReplayFrame)__0;p.accepted++;p.lastCapture=p.latest.T;if(double.IsNaN(p.firstCapture))p.firstCapture=p.lastCapture;}catch{p.observerErrors++;}}
    private static object? Read(object? source,string name)
    {if(source==null)return null;for(var type=source.GetType();type!=null;type=type.BaseType){var f=type.GetField(name,Flags|BindingFlags.DeclaredOnly);if(f!=null)return f.GetValue(source);var p=type.GetProperty(name,Flags|BindingFlags.DeclaredOnly);if(p!=null&&p.GetIndexParameters().Length==0)return p.GetValue(source);}return null;}
    private static string Hash(string path)
    {if(!File.Exists(path))return "ABSENT";using var sha=SHA256.Create();using var file=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static int MissingCreatureCount(string diagnostic)
    {
        var match=System.Text.RegularExpressions.Regex.Match(diagnostic,@"\bmissingCreatures=(\d+)");
        return match.Success?int.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):-1;
    }
    private void Awake()
    {
        string expected="",outputRoot="";
        foreach(var arg in System.Environment.GetCommandLineArgs())
        {
            if(arg=="--peak-route-fauna-visual")faunaVisualRequested=true;
            if(arg.StartsWith("--peak-route-label=",StringComparison.Ordinal))label=arg.Substring("--peak-route-label=".Length).Trim('"');
            if(arg.StartsWith("--peak-route-output-root=",StringComparison.Ordinal))outputRoot=arg.Substring("--peak-route-output-root=".Length).Trim('"');
            if(arg.StartsWith("--peak-route-scene=",StringComparison.Ordinal))scene=arg.Substring("--peak-route-scene=".Length).Trim('"');
            if(arg.StartsWith("--peak-route-save-hash=",StringComparison.Ordinal))expected=arg.Substring("--peak-route-save-hash=".Length).Trim('"');
            if(arg.StartsWith("--peak-route-first-wait=",StringComparison.Ordinal))firstWait=int.Parse(arg.Substring("--peak-route-first-wait=".Length));
            if(arg.StartsWith("--peak-route-later-wait=",StringComparison.Ordinal))laterWait=int.Parse(arg.Substring("--peak-route-later-wait=".Length));
            if(arg.StartsWith("--peak-route-far-wait=",StringComparison.Ordinal))farWait=int.Parse(arg.Substring("--peak-route-far-wait=".Length));
            if(arg.StartsWith("--peak-route-final-wait=",StringComparison.Ordinal))finalWait=int.Parse(arg.Substring("--peak-route-final-wait=".Length));
            if(arg.StartsWith("--peak-route-biomes=",StringComparison.Ordinal))maxBiomes=int.Parse(arg.Substring("--peak-route-biomes=".Length));
            if(arg.StartsWith("--peak-route-observe-from-index=",StringComparison.Ordinal))observeFrom=int.Parse(arg.Substring("--peak-route-observe-from-index=".Length));
        }
        if(label.Length==0){enabled=false;return;}
        try
        {
            Require(label.Length<=64&&label.All(c=>char.IsLetterOrDigit(c)||c=='-'),"Invalid unique label.");
            Require(scene.StartsWith("Level_",StringComparison.Ordinal)&&scene.Substring(6).All(char.IsDigit),"Only baked Level_n scenes are admitted.");
            Require(firstWait>=15&&firstWait<=300&&laterWait>=15&&laterWait<=120&&farWait>=15&&farWait<=120&&maxBiomes>=1&&maxBiomes<=5,"Bounded route waits/biomes required.");
            Require(finalWait>=15&&finalWait<=300,"Final wait must be between 15 and 300 seconds.");
            Require(observeFrom>=0&&observeFrom<=3,"Observe-from index must be between 0 and 3.");
            Require(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null,"Graphical native game required.");
            Require(outputRoot.Length!=0&&Path.IsPathFullyQualified(outputRoot),"Explicit absolute --peak-route-output-root is required.");
            output=Path.Combine(Path.GetFullPath(outputRoot),label);Require(!Directory.Exists(output),"Refusing to overwrite a test session.");
            savePath=Path.Combine(Application.persistentDataPath,"quicksave.peak");beforeHash=Hash(savePath);Require(expected.Length==64&&beforeHash==expected,"Protected save proof mismatch.");
            foreach(var file in Directory.GetFiles(Paths.ConfigPath,"*",SearchOption.AllDirectories))configHashes[file]=Hash(file);
            Directory.CreateDirectory(output);begun=phaseAt=UTime.realtimeSinceStartupAsDouble;active=this;armed=true;
            if(faunaVisualRequested)faunaVisual=new FaunaVisualAudit(output);
            rowWriter=Task.Run(()=>WriteRows());Progress();
        }
        catch(Exception e){error=e.ToString();Logger.LogError("Route probe refused: "+e);enabled=false;}
    }
    private void WriteRows()
    {
        try
        {using var stream=new StreamWriter(Path.Combine(output,"heartbeat.jsonl"),false,new System.Text.UTF8Encoding(false),65536);while(!endWriter||!rows.IsEmpty){while(rows.TryDequeue(out var row))stream.WriteLine(JsonConvert.SerializeObject(row));stream.Flush();if(!endWriter)wake.WaitOne(500);} }
        catch(Exception e){writerError=e.GetType().Name+": "+e.Message;}
    }
    private void Protect()
    {
        oldSave=Peak.Quicksave.ShouldUseSaveData;oldOffline=PhotonNetwork.OfflineMode;oldAscent=Ascents.currentAscent;oldRandom=UnityEngine.Random.state;
        foreach(var name in new[]{"SaveNow","DestroySaveData","FinalizeRunSetup"})Prefix(typeof(Peak.Quicksave),name);
        foreach(var name in new[]{"SetSteamStatInternal","IncrementSteamStat","ThrowAchievement"})Prefix(typeof(AchievementManager),name);
        Prefix(typeof(ConfigFile),"Save");Prefix(typeof(NetworkConnector),"OnLeftRoom");
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Plugin),"AcceptFullRunFrame"),postfix:new HarmonyMethod(typeof(RouteAuditProbe),nameof(Accepted)));
        var captureType=typeof(Plugin).Assembly.GetType("PeakReplayLab.RollingCapture",true)!;
        harmony.Patch(AccessTools.DeclaredMethod(captureType,"Tick"),prefix:new HarmonyMethod(typeof(RouteAuditProbe),nameof(BeforeCapture)),finalizer:new HarmonyMethod(typeof(RouteAuditProbe),nameof(AfterCapture)));
        harmony.Patch(AccessTools.DeclaredMethod(captureType,"PrepareBootstrap"),prefix:new HarmonyMethod(typeof(RouteAuditProbe),nameof(BeforePrepare)),finalizer:new HarmonyMethod(typeof(RouteAuditProbe),nameof(AfterPrepare)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ActorJointReplayRules),nameof(ActorJointReplayRules.Validate)),finalizer:new HarmonyMethod(typeof(RouteAuditProbe),nameof(AfterJointValidation)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ReplayRules),"ValidateItem"),finalizer:new HarmonyMethod(typeof(RouteAuditProbe),nameof(AfterItemValidation)));
        blockWrites=true;Peak.Quicksave.ShouldUseSaveData=false;Ascents.currentAscent=0;UnityEngine.Random.InitState(231013);
    }
    private void Prefix(Type type,string method)=>harmony.Patch(AccessTools.DeclaredMethod(type,method)??throw new MissingMethodException(type.FullName,method),prefix:new HarmonyMethod(typeof(RouteAuditProbe),nameof(AllowWrite)));
    private void Progress()=>File.WriteAllText(Path.Combine(output,"phase.json"),JsonConvert.SerializeObject(new{Phase=phase.ToString(),Biome=segment?.Biome,Index=segment?.Index,Elapsed=UTime.realtimeSinceStartupAsDouble-begun,StageSeconds=UTime.realtimeSinceStartupAsDouble-phaseAt,Error=error,CleanupError=cleanupError}));
    private void Enter(Phase next,double now)
    {
        stages.Add(new{Phase=phase.ToString(),Index=segment?.Index,Biome=segment?.Biome,StartedAt=phaseAt-begun,EndedAt=now-begun,NativeTime=lastCapture});
        phase=next;phaseAt=now;captureMeter.Stage();sampleMeter.Stage();prepareMeter.Stage();probeMeter.Stage();frameMeter.Stage();Progress();
        if(next==Phase.Arrival){arrivalReadySince=double.NaN;lastArrivalReadiness=null;}
    }
    private void Deadline(double now,double limit){if(now-phaseAt>limit)throw new TimeoutException(phase+" exceeded "+limit+" seconds.");}
    private void LateUpdate()
    {
        if(!armed)return;double now=UTime.realtimeSinceStartupAsDouble;frameMeter.Add(UTime.unscaledDeltaTime*1000d);
        if(faunaVisual!=null&&phase>=Phase.Arrival&&phase<Phase.Seal)faunaVisual.Tick(UTime.timeAsDouble,phase.ToString(),segment);
        if(now-begun>1400){error="Hard route bound exceeded.";WriteSummary(false);endWriter=true;wake.Set();Application.Quit();armed=false;return;}
        if(now<nextPoll&&phase!=Phase.Disconnect)return;nextPoll=now+.1;long started=Stopwatch.GetTimestamp();
        try
        {
            if(api==null){if(!Chainloader.PluginInfos.TryGetValue("cn.mylus.peakreplaylab",out var info)||info.Instance is not Plugin plugin){Deadline(now,60);return;}owner=plugin;api=plugin.Automation;}
            var status=api.GetStatus();if(status.RecordingFile.Length!=0)recording=status.RecordingFile;if(status.LatestRecording!=null)result=status.LatestRecording;
            if(status.LastError.Length!=0&&!cleaning)throw new InvalidOperationException(status.LastError);
            if(rollingError.Length!=0&&!cleaning)throw new InvalidOperationException(rollingError);
            if(now>=nextHeartbeat){nextHeartbeat=now+1;Heartbeat(status,now);}
            switch(phase)
            {
                case Phase.Title:
                    if(!status.CanBeginSession){Deadline(now,180);break;}bakedScenes=NativeRouteActions.ListBakedScenes();Require(bakedScenes.Any(x=>x.Scene==scene),"Requested baked scene is not in native catalogue.");
                    Protect();api.BeginSession(output);owned=true;NetworkingUtilities.EnteringRoom=true;disconnect=MainMenu.DisconnectForOfflineMode();Enter(Phase.Disconnect,now);break;
                case Phase.Disconnect:
                    if(disconnect!.MoveNext()){Deadline(now,30);break;}(disconnect as IDisposable)?.Dispose();disconnect=null;loading=SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);Enter(Phase.Load,now);break;
                case Phase.Load:
                    if(loading!=null&&loading.isDone&&status.CaptureReady&&status.Recording&&accepted>0){NetworkingUtilities.EnteringRoom=false;segment=NativeRouteActions.Current();Require(segment.Index==0&&segment.RootActive,"Fresh route must start on native beach.");Enter(Phase.Arrival,now);}else Deadline(now,120);break;
                case Phase.Arrival:
                    Deadline(now,90);Require(status.Recording&&status.CaptureReady,"Recording unavailable on native arrival.");if(now-phaseAt<8||Character.localCharacter.warping){arrivalReadySince=double.NaN;break;}
                    segment=NativeRouteActions.Current();Require(segment.RootActive,"Selected native root not active.");
                    if(segment.Index<observeFrom){completedBiomes=segment.Index+1;oldRoot=segment.Root;NativeRouteActions.TeleportToCampfire(segment);Enter(Phase.WarpFire,now);break;}
                    if(!ArrivalBaselinesReady(now))break;
                    baselineStarts.Add(new{Index=segment.Index,Biome=segment.Biome,AtRunSeconds=now-begun,NativeTime=lastCapture,ArrivalWaitSeconds=now-phaseAt,ReadyStableSeconds=now-arrivalReadySince,Readiness=lastArrivalReadiness});
                    NativeCensus();biomeTimes.Add(lastCapture-firstCapture);trapPoison=trapExplo=false;
                    NativeRouteActions.TeleportToBaseCamp();actions.Add(new{Kind="arrive-basecamp",Biome=segment.Biome,NativeTime=lastCapture,Key="",Source=segment.RootPath,Success=true});Enter(Phase.BaselineIdle,now);break;
                case Phase.BaselineIdle:
                    Require(status.Recording,"Recording stopped while idle.");if(now-phaseAt<(completedBiomes==0?firstWait:laterWait))break;
                    luggage=NativeRouteActions.FindCurrentCandidates<Luggage>(segment!,x=>x.GetType()==typeof(Luggage)&&!x.IsOpen&&!x.CompareTag("Trap")).OrderBy(x=>Vector3.Distance(x.transform.position,Character.localCharacter.Center)).FirstOrDefault();
                    if(luggage){NativeRouteActions.TeleportTo(luggage!.Center()+Vector3.up*1.5f);Enter(Phase.WarpBox,now);}else{actions.Add(new{Kind="luggage-unavailable",Biome=segment!.Biome,NativeTime=lastCapture,Success=false});Enter(Phase.EventObservation,now);}break;
                case Phase.WarpBox:
                    if(now-phaseAt<1||Character.localCharacter.warping){Deadline(now,20);break;}
                    if(Vector3.Distance(Character.localCharacter.Center,luggage!.Center())>5){actions.Add(new{Kind="luggage-out-of-reach",Biome=segment!.Biome,NativeTime=lastCapture,Key=NativeRouteActions.PathOf(luggage.transform),Success=false});Enter(Phase.EventObservation,now);break;}
                    holdSeconds=NativeRouteActions.BeginLuggage(segment!,luggage);actions.Add(new{Kind="luggage-begin",Biome=segment!.Biome,NativeTime=lastCapture,Key=NativeRouteActions.PathOf(luggage.transform),Before=luggage.IsOpen,HoldSeconds=holdSeconds,Success=true});Enter(Phase.HoldBox,now);break;
                case Phase.HoldBox:
                    if(now-phaseAt<holdSeconds+.15)break;NativeRouteActions.FinishLuggage(segment!,luggage!);actions.Add(new{Kind="luggage-open",Biome=segment!.Biome,NativeTime=lastCapture,Key=NativeRouteActions.PathOf(luggage!.transform),After=luggage.IsOpen,Success=luggage.IsOpen});Enter(Phase.EventObservation,now);break;
                case Phase.EventObservation:
                    if(!trapPoison&&now-phaseAt>=3){TriggerNativeTrap(segment!.Biome=="Roots"?"Forest_SporeFungus":"Jungle_SporeMushroom");trapPoison=true;trapAt=now;}
                    if(!trapExplo&&now-phaseAt>=7){if(segment!.Biome!="Roots")TriggerNativeTrap("Jungle_SporeMushroomExplo");trapExplo=true;trapAt=now;}
                    if(now-phaseAt<18)break;NativeRouteActions.TeleportToBaseCamp();actions.Add(new{Kind="leave-event-site",Biome=segment!.Biome,NativeTime=lastCapture,Success=true});Enter(Phase.FarIdle,now);break;
                case Phase.FarIdle:
                    Require(status.Recording,"Recording stopped in far idle.");if(now-phaseAt<(completedBiomes+1>=maxBiomes?Math.Max(farWait,finalWait):farWait))break;NativeCensus();completedBiomes++;
                    if(completedBiomes>=maxBiomes||segment!.Index>=4){completedRoute=true;api.SealChapter("native-route-complete");Enter(Phase.Seal,now);break;}
                    oldRoot=segment!.Root;NativeRouteActions.TeleportToCampfire(segment);Enter(Phase.WarpFire,now);break;
                case Phase.WarpFire:
                    if(now-phaseAt<1||Character.localCharacter.warping){Deadline(now,20);break;}holdSeconds=NativeRouteActions.BeginCampfire(segment!);actions.Add(new{Kind="campfire-begin",Biome=segment!.Biome,NativeTime=lastCapture,Key=NativeRouteActions.PathOf(segment.Campfire!.transform),Before=segment.Campfire.state.ToString(),HoldSeconds=holdSeconds,Success=true});Enter(Phase.HoldFire,now);break;
                case Phase.HoldFire:
                    if(now-phaseAt<holdSeconds+.15)break;NativeRouteActions.FinishCampfire(segment!);actions.Add(new{Kind="campfire-light",Biome=segment!.Biome,NativeTime=lastCapture,Key=NativeRouteActions.PathOf(segment.Campfire!.transform),After=segment.Campfire.state.ToString(),NextIndex=segment.AdvanceToIndex,Success=segment.Campfire.Lit});Enter(Phase.Transition,now);break;
                case Phase.Transition:
                    var next=NativeRouteActions.Current();if(next.Index==segment!.Index+1&&next.RootActive&&oldRoot&&!oldRoot!.activeInHierarchy&&!Character.localCharacter.warping){actions.Add(new{Kind="native-root-transition",Biome=next.Biome,NativeTime=lastCapture,OldRoot=segment.RootPath,NewRoot=next.RootPath,OldActive=oldRoot.activeInHierarchy,NewActive=next.RootActive,Success=true});segment=next;Enter(Phase.Arrival,now);}else Deadline(now,90);break;
                case Phase.Seal:
                    if(!status.Recording&&!status.Sealing){Require(result!=null&&result.Status=="completed"&&result.WrittenFrames==accepted,"Chapter did not save all accepted frames.");Enter(Phase.ReturnTitle,now);}else Deadline(now,60);break;
                case Phase.ReturnTitle:
                    if(ToTitle(status,now)){api.OpenReplay(recording);Enter(Phase.ReplayOpen,now);}break;
                case Phase.ReplayOpen:
                    if(status.PlaybackReady&&!status.PlaybackBuffering){var eventTimes=actions.Where(x=>Read(x,"Kind") is string kind&&(kind=="luggage-open"||kind=="trap-trigger"||kind=="campfire-light")&&Read(x,"Success") is true).Select(x=>Convert.ToDouble(Read(x,"NativeTime"))-firstCapture+.2);targets=biomeTimes.Select(t=>t+15).Concat(biomeTimes.AsEnumerable().Reverse().Select(t=>t+2)).Concat(eventTimes).Select(t=>Math.Max(0,Math.Min(status.PlaybackDuration-.5,t))).Distinct().ToArray();Require(targets.Length>0,"No route replay targets.");api.SetPlaybackPaused(true);Seek(now);}else Deadline(now,150);break;
                case Phase.Seek:
                    if(status.PlaybackReady&&!status.PlaybackBuffering&&Math.Abs(status.PlaybackTime-targets[seekIndex])<.05&&now-requestedAt>=.3){playFrom=status.PlaybackTime;api.SetPlaybackPaused(false);Enter(Phase.Advance,now);}else Deadline(now,50);break;
                case Phase.Advance:
                    if(now-phaseAt<2)break;Require(status.PlaybackReady&&!status.PlaybackBuffering&&status.PlaybackTime>playFrom+.5,"Replay did not resume.");
                    int missingCreatures=MissingCreatureCount(status.PresentationDiagnostic);
                    seeks.Add(new{Index=seekIndex,Target=targets[seekIndex],SettledSeconds=phaseAt-requestedAt,Advanced=status.PlaybackTime-playFrom,status.MissingItems,status.MissingJoints,MissingCreatures=missingCreatures,status.PresentationDiagnostic});
                    Require(status.MissingItems==0&&status.MissingJoints==0&&missingCreatures==0,"Replay seek has missing item, actor joint, or creature visuals.");
                    api.SetPlaybackPaused(true);seekIndex++;if(seekIndex<targets.Length)Seek(now);else{api.CloseReplay();Enter(Phase.ReplayClose,now);}break;
                case Phase.ReplayClose:
                    if(!status.PlaybackReady&&!status.PlaybackBusy){api.EndSession();cleaning=true;Enter(Phase.Ending,now);}else Deadline(now,60);break;
                case Phase.Ending:
                    api.EndSession();if(ToTitle(status,now)&&!api.GetStatus().SessionActive)Finish();break;
            }
        }
        catch(Exception e){Fail(e,now);}
        finally{probeMeter.Add((Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency);}
    }
    private void Seek(double now){requestedAt=now;api!.SeekReplay(targets[seekIndex]);Enter(Phase.Seek,now);}
    private void TriggerNativeTrap(string name)
    {
        var trigger=NativeRouteActions.FindCurrentCandidates<TriggerEvent>(segment!,x=>x.name==name&&x.onlyOnce&&x.GetComponent<SpawnGameObject>()&&x.GetComponent<SpawnGameObject>().toSpawn).FirstOrDefault();
        if(!trigger){actions.Add(new{Kind="trap-unavailable",Biome=segment!.Biome,NativeTime=lastCapture,Source=name,Success=false});return;}
        string resource=trigger!.GetComponent<SpawnGameObject>().toSpawn.name;int before=NativeParticleCount(resource);trigger.Trigger();
        actions.Add(new{Kind="trap-trigger",Biome=segment!.Biome,NativeTime=lastCapture,Source=NativeRouteActions.PathOf(trigger.transform),ExpectedResource=resource,BeforeNativeSystems=before,Success=true});
        StartCoroutine(ObserveNativeTrap(resource,before));
    }
    private IEnumerator ObserveNativeTrap(string resource,int before)
    {yield return new WaitForSecondsRealtime(.75f);int after=NativeParticleCount(resource);actions.Add(new{Kind="trap-native-birth",Biome=segment?.Biome,NativeTime=lastCapture,ExpectedResource=resource,Before=before,After=after,Success=after>before});}
    private static int NativeParticleCount(string name)=>UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude,FindObjectsSortMode.None).Count(x=>x&&x.gameObject.scene==SceneManager.GetActiveScene()&&x.name.StartsWith(name,StringComparison.Ordinal));
    private bool ArrivalBaselinesReady(double now)
    {
        var capture=Read(owner,"capture");var scope=Read(capture,"SegmentDiagnostics");var bootstrap=Read(capture,"BootstrapDiagnostics");
        bool scopeReady=Read(scope,"DirectoryReliable") is true&&Read(scope,"Pending") is int pending&&pending==0&&
            Read(scope,"FailedBaselines") is int failed&&failed==0&&Read(scope,"DeferredOrigins") is int deferred&&deferred==0&&
            Read(scope,"PendingRebind") is int rebind&&rebind==0;
        bool tracksReady=new[]{"Items","Crates","Ropes","Spawned","Balloons","Creatures","Interactions","Effects","Audio","Environment"}.All(name=>Read(bootstrap,name) is true);
        bool ready=scopeReady&&tracksReady;
        if(!ready)arrivalReadySince=double.NaN;else if(double.IsNaN(arrivalReadySince))arrivalReadySince=now;
        lastArrivalReadiness=new{Scope=scope,TracksReady=tracksReady,Ready=ready,StableSeconds=ready?now-arrivalReadySince:0};
        return ready&&now-arrivalReadySince>=1;
    }
    private static int NativeItemUses(Item item)
    {
        if(item.data?.data==null||!item.data.data.TryGetValue(DataEntryKey.ItemUses,out var value))return -1;
        return value switch{IntItemData number=>number.Value,OptionableIntItemData number when number.HasData=>number.Value,_=>-1};
    }
    private void NativeCensus()
    {
        var known=new HashSet<string>(new[]{"Spider","MushroomZombie","Condor","FrogTongue","Beetle","Scoutmaster","Mob","Antlion","BeeSwarm","VenusFlyTrap","TumbleWeed","VoiceThief","Skelleton","ScoutmasterGhostOrbiter","Capybara","MirageLuggage","GhostBall","PlayerGhost"},StringComparer.Ordinal);
        var activeScene=SceneManager.GetActiveScene();
        var sceneCreatures=UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude,FindObjectsSortMode.None)
            .Where(x=>x&&x.gameObject.activeInHierarchy&&x.gameObject.scene==activeScene&&known.Contains(x.GetType().Name))
            .Select(x=>new{Type=x.GetType().Name,Path=NativeRouteActions.PathOf(x.transform),Instance=x.GetInstanceID(),GameObjectId=x.gameObject.GetInstanceID(),CurrentScope=NativeRouteActions.IsWithinCurrentScope(segment!,x)}).ToArray();
        var components=sceneCreatures.Where(x=>x.CurrentScope).Select(x=>new{x.Type,x.Path,x.Instance,x.GameObjectId}).ToArray();
        var sceneFrogs=UnityEngine.Object.FindObjectsByType<Item>(FindObjectsInactive.Exclude,FindObjectsSortMode.None)
            .Where(x=>x&&x.gameObject.activeInHierarchy&&x.gameObject.scene==activeScene&&x.itemID==172)
            .Select(x=>new{ItemId=(int)x.itemID,Name=x.name,Instance=x.GetInstanceID(),GameObjectId=x.gameObject.GetInstanceID(),Path=NativeRouteActions.PathOf(x.transform),State=(int)x.itemState,Uses=NativeItemUses(x)}).ToArray();
        var recordedVoiceThieves=latest?.Creatures.Where(x=>x.Kind=="voice-thief").Select(x=>new{x.Key,Active=x.Pose.Active}).ToArray();
        var boxes=NativeRouteActions.FindCurrentCandidates<Luggage>(segment!).Select(x=>new{Path=NativeRouteActions.PathOf(x.transform),Type=x.GetType().Name,x.IsOpen}).ToArray();
        census.Add(new{Index=segment!.Index,Biome=segment.Biome,Phase=phase.ToString(),AtRunSeconds=UTime.realtimeSinceStartupAsDouble-begun,NativeTime=lastCapture,RootPath=segment.RootPath,Creatures=components,SceneNativeCreatures=sceneCreatures,SceneNativeFrogs=sceneFrogs,RecordedVoiceThieves=recordedVoiceThieves,Luggage=boxes});
    }
    private void Heartbeat(ReplayAutomationStatus status,double now)
    {
        var capture=Read(owner,"capture");var buffer=Read(capture,"Buffer");var full=Read(owner,"fullRun");var stats=Read(full,"Stats");process.Refresh();
        if(status.Recording&&capture!=null)ObserveBindings(capture);
        var nativeRoots=MapHandler.ExistsAndInitialized?MapHandler.Instance.segments.Select((x,i)=>new{Index=i,RootPath=x.segmentParent?NativeRouteActions.PathOf(x.segmentParent.transform):"",Active=x.segmentParent&&x.segmentParent.activeInHierarchy,CampActive=x.segmentCampfire&&x.segmentCampfire.activeInHierarchy}).ToArray():null;
        var frame=latest;
        var local=Character.localCharacter;
        var center=local?local.Center:Vector3.zero;
        rows.Enqueue(new{Phase=phase.ToString(),StageSeconds=now-phaseAt,RunSeconds=now-begun,NativeTime=lastCapture,NativeSegment=segment,AcceptedFrames=accepted,
            WriterBytes=Read(stats,"BytesWritten"),QueueFrames=status.QueuedFrames,QueueBytes=status.QueuedBytes,RollingBytes=Read(buffer,"EstimatedBytes"),RollingCount=Read(buffer,"Count"),
            Memory=new{ManagedBytes=GC.GetTotalMemory(false),WorkingSetBytes=process.WorkingSet64,PrivateBytes=process.PrivateMemorySize64,UnityAllocatedBytes=UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),UnityReservedBytes=UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong()},
            GlobalGcCounts=Enumerable.Range(0,GC.MaxGeneration+1).Select(GC.CollectionCount).ToArray(),
            MainThread=new{Capture=captureMeter.Snapshot(),SampledCapture=sampleMeter.Snapshot(),Prepare=prepareMeter.Snapshot(),Probe=probeMeter.Snapshot(),RenderedFrame=frameMeter.Snapshot()},
            Work=new{Items=Read(capture,"ItemDiagnostics"),Ropes=Read(capture,"RopeDiagnostics"),Creatures=Read(capture,"CreatureDiagnostics"),Interactions=Read(capture,"InteractionDiagnostics"),Spawned=Read(capture,"SpawnedDiagnostics"),Effects=Read(capture,"EffectDiagnostics"),Audio=Read(capture,"AudioDiagnostics"),Scope=Read(capture,"SegmentDiagnostics")},
            Counts=frame==null?null:new{Items=frame.Items.Length,Crates=frame.Crates.Length,Ropes=frame.Ropes.Length,Creatures=frame.Creatures.Length,Effects=frame.Effects.Length,Audio=frame.Audio.Length,Interactions=frame.Interactions.Length},
            CreatureKinds=frame?.Creatures.GroupBy(x=>x.Kind).ToDictionary(g=>g.Key,g=>g.Count()),
            CreatureClocks=frame?.Creatures.Where(x=>x.Kind!="spore-trap").Take(24).Select(x=>new{x.Key,x.Kind,x.SourcePath,x.ClockMode,x.SampleTime,Idle=x.IdleAnimation!=null}).ToArray(),NativeRoots=nativeRoots,
            LocalActor=local?new{Position=new[]{center.x,center.y,center.z},Dead=local.data.dead,FullyPassedOut=local.data.fullyPassedOut,GameOver=local.gameOver,Warping=local.warping,Grounded=local.data.isGrounded,Stamina=local.data.currentStamina}:null,
            Recording=status.Recording,BootstrapPending=status.BootstrapPending,Error=status.LastError});wake.Set();Progress();
    }
    private static IEnumerable<object> Values(object? value)
    {if(value is IDictionary map){foreach(var entry in map.Values)if(entry!=null)yield return entry;}else if(value is IEnumerable sequence){foreach(var entry in sequence)if(entry!=null)yield return entry;}}
    private static GameObject? NativeRoot(object? value,int depth=0)
    {if(depth>3)return null;if(value is GameObject go)return go?go:null;if(value is Component component)return component?component.gameObject:null;return NativeRoot(Read(value,"Root")??Read(value,"ScopeRoot")??Read(value,"Source")??Read(value,"Audio"),depth+1);}
    private void ObserveBindings(object capture)
    {
        foreach(var name in new[]{"Items","Crates","Ropes","Creatures","Interactions","Spawned","Effects","Audio"})
        {
            var track=Read(capture,name.ToLowerInvariant());if(track==null)continue;
            var owners=new Dictionary<int,string>();var client=Read(track,"segments");
            foreach(var bound in Values(Read(client,"entries")))
            {var root=NativeRoot(Read(Read(bound,"Owner"),"Source"));owners[Convert.ToInt32(Read(bound,"Id"))]=root?NativeRouteActions.PathOf(root!.transform):"independent/global";}
            void Observe(object entry)
            {
                var frame=Read(entry,"Frame")??Read(entry,"Last")??Read(entry,"Previous");var key=Read(frame,"Key")as string;if(string.IsNullOrEmpty(key))return;
                var native=NativeRoot(entry);if(!native)return;
                string ownerPath=owners.TryGetValue(native!.GetInstanceID(),out string path)?path:"unresolved";
                string identity=name+":"+key;if(lastBindings.TryGetValue(identity,out var old)&&old==ownerPath)return;
                if(lastBindings.Count>=16384)return;lastBindings[identity]=ownerPath;bindings.Add(new{Track=name,Key=key,Owner=ownerPath,NativeSourcePath=NativeRouteActions.PathOf(native.transform),NativeTime=lastCapture});
            }
            foreach(var entry in Values(Read(track,"entries")??Read(track,"tracked")??Read(track,"sources")))Observe(entry);
            foreach(var entry in Values(Read(Read(track,"staticEntries"),"entries")))Observe(entry);
        }
    }
    private bool ToTitle(ReplayAutomationStatus status,double now)
    {
        Deadline(now,100);if(loading!=null&&!loading.isDone||status.Recording||status.Sealing)return false;
        if(PhotonNetwork.InRoom){PhotonNetwork.LeaveRoom(false);return false;}PhotonNetwork.Disconnect();if(PhotonNetwork.IsConnected&&!PhotonNetwork.OfflineMode)return false;
        PhotonNetwork.OfflineMode=oldOffline;GameHandler.ClearStatus<IsDisconnectingForOfflineMode>();NetworkingUtilities.EnteringRoom=false;NetworkConnector.ChangeConnectionState<DefaultConnectionState>();
        if(status.Scene!="Title"){loading=SceneManager.LoadSceneAsync("Title",LoadSceneMode.Single);return false;}return !LoadingScreenHandler.loading;
    }
    private void Fail(Exception e,double now)
    {
        Logger.LogError("Route audit: "+e);if(error.Length==0)error=e.ToString();else cleanupError=e.ToString();
        WriteSummary(false);
        if(!owned||cleaning){endWriter=true;wake.Set();armed=false;Application.Quit();return;}
        cleaning=true;api!.EndSession();Enter(Phase.Ending,now);
    }
    private void Finish()
    {
        restored=api!=null&&!api.GetStatus().SessionActive&&SceneManager.GetActiveScene().name=="Title"&&!PhotonNetwork.InRoom;
        if(restored){Peak.Quicksave.ShouldUseSaveData=oldSave;PhotonNetwork.OfflineMode=oldOffline;Ascents.currentAscent=oldAscent;UnityEngine.Random.state=oldRandom;GameHandler.ClearStatus<IsDisconnectingForOfflineMode>();NetworkingUtilities.EnteringRoom=false;}
        afterHash=Hash(savePath);bool configsSame=configHashes.All(x=>Hash(x.Key)==x.Value);
        bool success=restored&&completedRoute&&seekIndex==targets.Length&&targets.Length>0&&observerErrors==0&&error.Length==0&&rollingError.Length==0&&beforeHash==afterHash&&configsSame;
        WriteSummary(success);armed=false;phase=Phase.Done;endWriter=true;wake.Set();if(rowWriter!=null)rowWriter.Wait(3000);Application.Quit();
        // Leave save/stat/config protection patches installed through shutdown.
    }
    private void WriteSummary(bool success)
    {
        faunaVisual?.Flush();
        File.WriteAllText(Path.Combine(output,"summary.json"),JsonConvert.SerializeObject(new{Success=success,RestorationConfirmed=restored,CompletedRoute=completedRoute,CompletedBiomes=completedBiomes,InstalledVersion=typeof(Plugin).Assembly.GetName().Version?.ToString(),InstalledMvid=typeof(Plugin).Assembly.ManifestModule.ModuleVersionId,
            FaunaVisual=faunaVisual?.Summary,PerformanceMeasurementValid=!faunaVisualRequested,
            SpiderRuntimeRejection=typeof(Plugin).Assembly.GetType("PeakReplayLab.CreatureIdleAnimationRuntime")?.GetProperty("LastRejection",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(null),
            Error=error,CleanupError=cleanupError,RollingError=rollingError,WriterError=writerError,InvalidJointDiagnostic=File.Exists(Path.Combine(output,"invalid-joints-first.json"))?"invalid-joints-first.json":null,InvalidJointDiagnosticError=invalidJointDiagnosticError,InvalidItemDiagnostic=File.Exists(Path.Combine(output,"invalid-item-first.json"))?"invalid-item-first.json":null,InvalidItemDiagnosticError=invalidItemDiagnosticError,ObserverErrors=observerErrors,AcceptedFrames=accepted,RecordingFile=recording,RecordingResult=result,Scene=scene,BakedScenes=bakedScenes,
            FirstCapture=firstCapture,LastCapture=lastCapture,ObserveFromIndex=observeFrom,FinalWait=finalWait,BaselineStarts=baselineStarts,LastArrivalReadiness=lastArrivalReadiness,Actions=actions,NativeCensus=census,Bindings=bindings,Stages=stages,Seeks=seeks,QuicksaveUnchanged=Hash(savePath)==beforeHash,ConfigurationsUnchanged=configHashes.All(x=>Hash(x.Key)==x.Value),
            Measurement="Opt-in protected native route. Teleport and complete normal campfire/luggage interactions; native events, recorder and playback run normally. SampledCapture includes only Tick calls accepting a frame. Diagnostics run at 1Hz and serialize on a worker; Probe time records their main-thread cost. Memory includes game/runtime and is not recorder-owned allocation. No forced GC. Only exercised native classes/actions are coverage evidence."},Formatting.Indented));
    }
}
