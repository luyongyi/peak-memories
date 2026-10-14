using System;
using PeakReplayLab;
using Peak.Island;
using UnityEngine;

internal static class Program
{
    private static int passed;
    private static int Main()
    {
        try
        {
            RavenAnimationCapture.EventHooksHealthy = true;
            Test("perched clock includes randomized cycle offset and keeps one immutable checkpoint", () =>
            {
                var raven = Native(); raven.Animator.CycleOffset = .125f;
                var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(100, true, out var first, out bool changed) && changed);
                Check(first!.ClipTime == 1.125 && first.AnchorTime == 100 && first.Speed == 1);
                for (int i = 1; i <= 1800; i++)
                {
                    var state = raven.Animator.State; state.normalizedTime = 12.25f + i / 90f; raven.Animator.State = state;
                    Check(capture.TryObserve(100 + i / 30d, true, out var clock, out changed) && !changed && ReferenceEquals(first, clock));
                }
                Check(capture.Checks == 1801 && raven.Animator.runtimeAnimatorController!.DirectoryReads == 1);
            });
            Test("native fly-away coroutine gap remains captured until the actual flight state", () =>
            {
                var raven = Native(); var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(0, true, out _, out _));
                raven.IsLeaving = true; capture.NotifyNativeEvent();
                Check(!capture.TryObserve(.01, true, out _, out _));
                Fly(raven, .1f); Check(capture.TryObserve(.3, true, out var flight, out bool changed) && changed);
                Check(flight!.Clip == CreatureIdleAnimationRules.RavenFlyAway && Math.Abs(flight.ClipTime - .3) < .000001);
                CreatureIdleAnimationRules.Validate(flight);
                Check(CreatureIdleAnimationRules.SampleTime(flight, 99) == 3 && CreatureIdleAnimationRules.SampleTime(flight, -99) == 0);
            });
            Test("any mouth layer activity immediately falls back and zero weight starts a fresh checkpoint", () =>
            {
                var raven = Native(); var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(0, true, out var original, out _));
                foreach (float weight in new[] { .000001f, .5f, 1f, float.NaN })
                { raven.Animator.MouthWeight = weight; Check(!capture.TryObserve(.1, true, out _, out _)); }
                raven.Animator.MouthWeight = 0;
                Check(capture.TryObserve(.2, true, out var resumed, out bool changed) && changed && !ReferenceEquals(original, resumed));
                Check(capture.MouthFallbacks == 4);
            });
            Test("unsupported animator modes controllers clip blends and unexpected states remain geometry", () =>
            {
                foreach (Action<VoiceThief> corrupt in new Action<VoiceThief>[]
                {
                    r => r.Animator.enabled = false, r => r.Animator.gameObject.activeInHierarchy = false,
                    r => r.Animator.layerCount = 3, r => r.Animator.Transition = true,
                    r => r.Animator.applyRootMotion = true, r => r.Animator.isHuman = true,
                    r => r.Animator.updateMode = AnimatorUpdateMode.UnscaledTime, r => r.Animator.cullingMode = AnimatorCullingMode.CullCompletely,
                    r => r.Animator.runtimeAnimatorController = Native().Animator.runtimeAnimatorController,
                    r => r.Animator.speed = -1, r => r.Animator.speed = float.NaN, r => r.Animator.speed = 9,
                    r => r.Animator.CycleOffset = float.NaN, r => r.Animator.CycleOffset = -1,
                    r => { var s = r.Animator.State; s.fullPathHash = 1; r.Animator.State = s; },
                    r => { var s = r.Animator.State; s.speed = .5f; r.Animator.State = s; },
                    r => { var s = r.Animator.State; s.loop = false; r.Animator.State = s; },
                    r => r.Animator.Current.Add(r.Animator.Current[0]),
                    r => r.Animator.Current[0] = new AnimatorClipInfo { clip = Clip(r, 0), weight = .5f },
                })
                {
                    var raven = Native(); var capture = new RavenAnimationCapture(raven); corrupt(raven);
                    Check(!capture.TryObserve(0, true, out _, out _));
                }
                var valid = new RavenAnimationCapture(Native());
                Check(!valid.TryObserve(0, false, out _, out _) && !valid.TryObserve(double.NaN, true, out _, out _));
            });
            Test("audited controller metadata is shared and all asset variants fail open", () =>
            {
                var raven = Native(); var a = new RavenAnimationCapture(raven); var b = new RavenAnimationCapture(raven);
                Check(a.TryObserve(0, true, out _, out _) && b.TryObserve(0, true, out _, out _));
                Check(raven.Animator.runtimeAnimatorController!.DirectoryReads == 1);
                foreach (var clip in raven.Animator.runtimeAnimatorController.Clips) Check(clip.EventReads == 1);
                foreach (Action<VoiceThief> corrupt in new Action<VoiceThief>[]
                {
                    r => r.Animator.runtimeAnimatorController!.name = "Other",
                    r => r.Animator.runtimeAnimatorController = new AnimatorOverrideController { name = "Raven" },
                    r => Clip(r, 0).length = 2, r => Clip(r, 0).isLooping = false,
                    r => Clip(r, 1).isLooping = true, r => Clip(r, 2).length = 3,
                    r => Clip(r, 0).Events = new[] { new AnimationEvent() }, r => Clip(r, 0).frameRate = 30,
                    r => r.Animator.runtimeAnimatorController!.Clips = new[] { Clip(r, 0), Clip(r, 0), Clip(r, 2) },
                })
                {
                    var wrong = Native(); corrupt(wrong); var capture = new RavenAnimationCapture(wrong);
                    Check(!capture.TryObserve(0, true, out _, out _) && wrong.Animator.StateQueries == 0);
                }
            });
            Test("real edges speed changes and unexpected phase jumps re-anchor without dropping fallback", () =>
            {
                var raven = Native(); var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(1, true, out var first, out _)); capture.NotifyNativeEvent();
                Check(capture.TryObserve(1, true, out var second, out bool changed) && changed && !ReferenceEquals(first, second));
                raven.Animator.speed = 0; Check(capture.TryObserve(2, true, out var paused, out changed) && changed && paused!.Speed == 0);
                Check(capture.TryObserve(100, true, out var held, out changed) && !changed && ReferenceEquals(paused, held));
                raven.Animator.CycleOffset = .2f;
                Check(capture.TryObserve(101, true, out var jumped, out changed) && changed && Math.Abs(jumped!.ClipTime - 1.35) < .000001);
            });
            Test("sterile playback samples only a mapped clone and handles backwards seeking between both clips", () =>
            {
                var raven = Native(); var source = new GameObject(); source.Put(raven);
                var visual = new VisualReplica(); var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(10, true, out var idle, out _));
                Check(RavenAnimationRuntime.TryCreate(visual, source, idle!, out var runtime));
                var evaluator=visual.Clone.GetComponent<Animator>()!;
                runtime!.Apply(idle!, 11); Check(evaluator.PlayedState==RavenAnimationRuntime.IdleState&&evaluator.PlayedLayer==0);
                Check(Math.Abs(evaluator.PlayedNormalized-1.75/3)<.00001&&evaluator.Rebinds==1&&evaluator.UpdatedDelta==0);
                for (int i = 0; i < 100; i++) runtime.Apply(idle!, 11);
                Check(evaluator.Updates==1&&Clip(raven,0).Samples==0);
                raven.IsLeaving = true; Fly(raven, .2f); capture.NotifyNativeEvent();
                Check(capture.TryObserve(12, true, out var fly, out _));
                runtime.Apply(fly!, 12.5); Check(evaluator.PlayedState==RavenAnimationRuntime.FlyState&&Math.Abs(evaluator.PlayedNormalized-1.1/3)<.00001);
                runtime.Apply(idle!, 10); Check(Math.Abs(evaluator.PlayedNormalized-.75/3)<.00001);
                runtime.Invalidate(); runtime.Apply(idle!, 10); Check(evaluator.Updates==4&&evaluator.Rebinds==1);
                runtime.Dispose(); runtime.Apply(fly!, 13); Check(evaluator.Updates==4&&evaluator.runtimeAnimatorController==null&&!evaluator.enabled);
                Check(raven.Animator.Plays==0&&raven.Animator.Updates==0&&raven.Animator.Rebinds==0&&raven.Animator.FloatWrites==0&&raven.Animator.LayerWrites==0);
                Check(visual.MappingReads == 1 && source.ComponentReads == 1);
            });
            Test("playback never samples the original native root or incomplete clone", () =>
            {
                var source = new GameObject(); source.Put(Native());
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.RavenIdle };
                Check(!RavenAnimationRuntime.TryCreate(new VisualReplica { ReturnNative = true }, source, clock, out _));
                Check(!RavenAnimationRuntime.TryCreate(new VisualReplica { IsReady = false }, source, clock, out _));
                Check(!RavenAnimationRuntime.TryCreate(new VisualReplica { MappingExists = false }, source, clock, out _));
            });
            Test("missing native event hooks keep ordinary geometry recording enabled", () =>
            {
                RavenAnimationCapture.EventHooksHealthy = false;
                Check(!new RavenAnimationCapture(Native()).TryObserve(0, true, out _, out _));
                RavenAnimationCapture.EventHooksHealthy = true;
            });
            Test("five Hertz admission reduces cached Animator observations while native idle keeps the original phase", () =>
            {
                var raven = Native(); var capture = new RavenAnimationCapture(raven); var cadence = new CreatureSamplingCadence();
                CreatureIdleAnimation? held = null; int admitted = 0;
                for (int i = 0; i < 1800; i++)
                {
                    var state = raven.Animator.State; state.normalizedTime = 12.25f + i / 90f; raven.Animator.State = state;
                    if (!cadence.Due(i / 30d, true, 0)) continue;
                    Check(capture.TryObserve(i / 30d, true, out var clock, out bool changed));
                    if (held == null) held = clock; else Check(!changed && ReferenceEquals(held, clock));
                    admitted++;
                }
                Check(admitted == 300 && capture.Checks == 300 && raven.Animator.StateQueries == 300);
            });
            Test("sparse mouth poses stay throttled but unsupported native states restore conservative observation", () =>
            {
                var raven = Native(); var capture = new RavenAnimationCapture(raven);
                Check(capture.TryObserve(0, true, out _, out _)); raven.Animator.MouthWeight = .5f;
                Check(!capture.TryObserve(.2, true, out _, out _) && capture.LowRateSafe);
                raven.Animator.MouthWeight = 0; raven.Animator.Transition = true;
                Check(!capture.TryObserve(.4, true, out _, out _) && !capture.LowRateSafe);
                raven.Animator.Transition = false;
                Check(capture.TryObserve(.433, true, out _, out _) && capture.LowRateSafe);
            });
            Test("untrusted StateMachineBehaviours reject animation references before any visual controller evaluation",()=>
            {
                var raven=Native();raven.Animator.runtimeAnimatorController!.Behaviours=new[]{new StateMachineBehaviour()};
                var capture=new RavenAnimationCapture(raven);Check(!capture.TryObserve(0,true,out _,out _)&&!capture.LowRateSafe);
                var source=new GameObject();source.Put(raven);var visual=new VisualReplica();
                Check(!RavenAnimationRuntime.TryCreate(visual,source,new(){Clip=CreatureIdleAnimationRules.RavenIdle},out _)&&visual.Clone.GetComponent<Animator>()==null);
                Check(raven.Animator.Plays==0&&raven.Animator.Updates==0&&raven.Animator.BehaviourQueries>=2);
            });
            Test("visual controller binds after activation zeros the already-recorded offset and can be reused after disposal",()=>
            {
                var raven=Native();raven.Animator.CycleOffset=.2f;var source=new GameObject();source.Put(raven);var visual=new VisualReplica();visual.Clone.activeInHierarchy=false;
                var clock=new CreatureIdleAnimation{Clip=CreatureIdleAnimationRules.RavenIdle,AnchorTime=10,ClipTime=1.35};
                Check(RavenAnimationRuntime.TryCreate(visual,source,clock,out var runtime));var evaluator=visual.Clone.GetComponent<Animator>()!;
                runtime!.Apply(clock,10);Check(evaluator.Rebinds==0&&evaluator.Updates==0);
                visual.Clone.activeInHierarchy=true;runtime.Apply(clock,10);
                Check(evaluator.Rebinds==1&&evaluator.Updates==1&&Math.Abs(evaluator.PlayedNormalized-.45)<.00001&&evaluator.CycleOffset==0&&evaluator.MouthWeight==0&&evaluator.BaseLayerWeight==1);
                Check(raven.Animator.CycleOffset==.2f&&raven.Animator.speed==1&&raven.Animator.enabled);
                runtime.Dispose();Check(RavenAnimationRuntime.TryCreate(visual,source,clock,out var reused));
                reused!.Apply(clock,10);Check(ReferenceEquals(evaluator,visual.Clone.GetComponent<Animator>())&&evaluator.Rebinds==2&&evaluator.Updates==2);reused.Dispose();
            });
            Test("sample invalidation never rebinds a healthy evaluator and actual lost initialization rebinds once",()=>
            {
                var raven=Native();var source=new GameObject();source.Put(raven);var visual=new VisualReplica();
                var clock=new CreatureIdleAnimation{Clip=CreatureIdleAnimationRules.RavenIdle,AnchorTime=10};
                Check(RavenAnimationRuntime.TryCreate(visual,source,clock,out var runtime));var evaluator=visual.Clone.GetComponent<Animator>()!;
                runtime!.Apply(clock,10);
                for(int i=1;i<=300;i++){runtime.Invalidate();runtime.Apply(clock,10+i/5d);}
                Check(evaluator.Rebinds==1&&evaluator.Updates==301);
                evaluator.isInitialized=false;runtime.Apply(clock,70);Check(evaluator.Rebinds==2&&evaluator.Updates==302);
                runtime.Apply(clock,70);Check(evaluator.Rebinds==2&&evaluator.Updates==302);
                runtime.Dispose();
            });
            Test("flight preserves only unanimated beaks from the clone checkpoint across first binding and later state changes",()=>
            {
                var raven=Native();var source=new GameObject();source.Put(raven);var visual=new VisualReplica();
                var bottom=visual.Clone.transform.Find("Armature/Base/Torso/Neck/Head/Bottom-Beak")!;
                var top=visual.Clone.transform.Find("Armature/Base/Torso/Neck/Head/Top-Beak")!;
                bottom.localRotation=new(){x=.2f,w=.98f};top.localRotation=new(){x=.3f,w=.95f};
                var fly=new CreatureIdleAnimation{Clip=CreatureIdleAnimationRules.RavenFlyAway,AnchorTime=10,ClipTime=.3};
                Check(RavenAnimationRuntime.TryCreate(visual,source,fly,out var runtime));runtime!.Apply(fly,10);
                Check(bottom.localRotation.x==.2f&&top.localRotation.x==.3f,"first flight bind overwrote checkpoint");
                runtime.Invalidate();runtime.Apply(fly,10.5);Check(bottom.localRotation.x==.2f&&top.localRotation.x==.3f);
                var idle=new CreatureIdleAnimation{Clip=CreatureIdleAnimationRules.RavenIdle,AnchorTime=11,ClipTime=1.5};
                runtime.Invalidate();runtime.Apply(idle,11);Check(bottom.localRotation.x==.5f&&top.localRotation.x==1.5f,"idle curves were incorrectly masked");
                // A seek applies a different held flight checkpoint before runtime.
                bottom.localRotation=new(){x=.4f,w=.9f};top.localRotation=new(){x=.6f,w=.8f};
                runtime.Invalidate();runtime.Apply(fly,10.1);Check(bottom.localRotation.x==.4f&&top.localRotation.x==.6f);
                Check(raven.Animator.Updates==0&&raven.Animator.Rebinds==0);runtime.Dispose();
            });
            Test("the complete six-property flight binding difference keeps held shoulders legs and beaks without source copying",()=>
            {
                var raven=Native();var source=new GameObject();source.Put(raven);var visual=new VisualReplica();
                var nodes=Animator.DefaultedFlightPaths.Select(path=>visual.Clone.transform.Find(path)!).ToArray();
                for(int i=0;i<nodes.Length;i++)nodes[i].localRotation=new(){x=(i+1)/10f,w=1};
                var flight=new CreatureIdleAnimation{Clip=CreatureIdleAnimationRules.RavenFlyAway,AnchorTime=10,ClipTime=.1};
                Check(RavenAnimationRuntime.TryCreate(visual,source,flight,out var runtime));
                for(int tick=0;tick<30;tick++)
                {
                    runtime!.Invalidate();runtime.Apply(flight,10+tick/30d);
                    for(int i=0;i<nodes.Length;i++)Check(nodes[i].localRotation.x==(i+1)/10f,"unkeyed flight rotation was replaced by clone defaults");
                }
                Check(visual.Clone.GetComponent<Animator>()!.Rebinds==1&&raven.Animator.Updates==0);runtime!.Dispose();
            });
            Test("three hundred actual native phase corrections keep the initial rig and appearance packet with root-only observations",()=>
            {
                var raven=Native();var capture=new RavenAnimationCapture(raven);Check(capture.TryObserve(100,true,out var first,out _));
                var initial=new CreatureReplayFrame{SampleTime=100,IdleAnimation=first,Pose=new(){Nodes=new object[44]},Visuals=new NativeRendererFrame[]{new()}};
                var frame=initial;int rootReads=0,geometryReads=0;
                Func<ObjectPose> root=()=>{rootReads++;return frame.Pose;};
                for(int i=1;i<=300;i++)
                {
                    var state=raven.Animator.State;state.normalizedTime=12.25f+i*.203f/3;raven.Animator.State=state;
                    Check(capture.TryObserve(100+i*.2,true,out var clock,out bool changed)&&changed);
                    if(RavenPhaseCheckpointCapture.TryCorrect(frame,clock!,0,true,false,false,false,root,out var corrected))frame=corrected;
                    else geometryReads++;
                    Check(ReferenceEquals(frame.Pose,initial.Pose)&&ReferenceEquals(frame.Pose.Nodes,initial.Pose.Nodes)&&ReferenceEquals(frame.Visuals,initial.Visuals));
                    Check(frame.SampleTime==100+i*.2&&ReferenceEquals(frame.IdleAnimation,clock)&&!frame.SampleDiscontinuity);
                }
                Check(rootReads==300&&geometryReads==0&&initial.SampleTime==100&&ReferenceEquals(initial.IdleAnimation,first));
            });
            Test("real native events clip speed appearance state and root edges cannot reuse a phase-only checkpoint",()=>
            {
                var raven=Native();var capture=new RavenAnimationCapture(raven);Check(capture.TryObserve(10,true,out var idle,out _));
                var frame=new CreatureReplayFrame{SampleTime=10,IdleAnimation=idle,Pose=new(){Nodes=new object[44]},Visuals=new NativeRendererFrame[]{new()}};
                int roots=0;Func<ObjectPose> sameRoot=()=>{roots++;return frame.Pose;};
                capture.NotifyNativeEvent();Check(capture.TryObserve(10,true,out var action,out bool changed)&&changed);
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,0,true,false,false,true,sameRoot,out _));
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,0,false,false,false,false,sameRoot,out _));
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,0,true,true,false,false,sameRoot,out _));
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,0,true,false,true,false,sameRoot,out _));
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,1,true,false,false,false,sameRoot,out _));
                raven.Animator.speed=.5f;Check(capture.TryObserve(10,true,out var slower,out changed)&&changed);
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,slower!,0,true,false,false,false,sameRoot,out _));
                raven.Animator.speed=1;raven.IsLeaving=true;Fly(raven,.1f);Check(capture.TryObserve(10.3,true,out var flight,out changed)&&changed);
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,flight!,1,true,false,false,false,sameRoot,out _)&&roots==0);
                Check(!RavenPhaseCheckpointCapture.TryCorrect(frame,action!,0,true,false,false,false,()=>{roots++;return new(){Position=new float[]{1,0,0},Nodes=frame.Pose.Nodes};},out _));
                Check(roots==1&&frame.SampleTime==10&&ReferenceEquals(frame.IdleAnimation,idle));
            });
            Console.WriteLine($"{passed} raven animation contract checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static VoiceThief Native()
    {
        var raven = new VoiceThief();
        var idle = new AnimationClip { name = "RavenIdle" };
        raven.Animator.runtimeAnimatorController = new RuntimeAnimatorController { name = "Raven", Clips = new[]
        { idle, new AnimationClip { name = "RavenFlyAway", isLooping = false }, new AnimationClip { name = "RavenMouthOpen", length = .5f } } };
        raven.Animator.Current.Add(new AnimatorClipInfo { clip = idle, weight = 1 }); return raven;
    }
    private static AnimationClip Clip(VoiceThief raven, int index) => raven.Animator.runtimeAnimatorController!.Clips[index];
    private static void Fly(VoiceThief raven, float phase)
    { raven.Animator.State = new AnimatorStateInfo { fullPathHash = RavenAnimationRuntime.FlyState, normalizedTime = phase, speed = 1, speedMultiplier = 1 }; raven.Animator.Current[0] = new AnimatorClipInfo { clip = Clip(raven, 1), weight = 1 }; }
    private static void Check(bool value,string detail="") { if (!value) throw new Exception("Raven animation contract failed: "+detail); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
}
