using System;
using System.Collections.Generic;
using PeakReplayLab;
using UnityEngine;

internal static class Program
{
    private static int passed;
    public static int Main()
    {
        try
        {
            CreatureIdleAnimationTests.Run(Test);
            Test("capture records native clip phase effective speed and silk phase once", () =>
            {
                var spider = Native(); spider.SetSinceGrab(.4f); spider.anim.speed = 2;
                var state = spider.anim.State; state.speedMultiplier = .5f; spider.anim.State = state;
                var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web");
                Check(capture.TryCapture(100, out var clock)); CreatureIdleAnimationRules.Validate(clock!);
                Check(clock!.AnchorTime == 100 && clock.ClipTime == .25 && clock.Speed == .33f && clock.RopePhase == .4f);
                Check(spider.anim.StateQueries == 1 && spider.anim.ClipQueries == 1 && spider.anim.runtimeAnimatorController!.DirectoryReads == 1);
                for (int i = 0; i < 6000; i++) CreatureIdleAnimationRules.SampleTime(clock, 100 + i / 60d);
                Check(spider.anim.StateQueries == 1 && spider.anim.ClipQueries == 1 && spider.anim.runtimeAnimatorController!.DirectoryReads == 1);
            });
            Test("idle clip metadata is shared without rereading native controller directory", () =>
            {
                var spider = Native();
                var a = new CreatureIdleAnimationRuntime.Capture(spider, "spider-normal");
                var b = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web");
                Check(a.TryCapture(0, out _) && b.TryCapture(0, out _));
                var controller = spider.anim.runtimeAnimatorController!;
                Check(controller.DirectoryReads == 1 && controller.Clips[0].EventReads == 1);
            });
            Test("unsupported resources and assets remain on captured geometry without Animator queries", () =>
            {
                foreach (Action<Spider> corrupt in new Action<Spider>[]
                {
                    s => s.anim.runtimeAnimatorController!.name = "Other", s => s.anim.runtimeAnimatorController = null,
                    s => s.anim.runtimeAnimatorController = new AnimatorOverrideController { name = "Spider (1)" },
                    s => Clip(s).name = "Attack", s => Clip(s).legacy = true, s => Clip(s).isLooping = false,
                    s => Clip(s).length = 2, s => Clip(s).frameRate = 30, s => Clip(s).Events = new[] { new AnimationEvent() },
                    s => s.anim.runtimeAnimatorController!.Clips = new[] { Clip(s), Clip(s) },
                    s => s.anim.runtimeAnimatorController!.Clips = new AnimationClip[17],
                })
                {
                    var spider = Native(); corrupt(spider);
                    var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web");
                    Check(!capture.TryCapture(0, out _) && spider.anim.StateQueries == 0 && spider.anim.ClipQueries == 0);
                }
                var other = Native(); var unsupported = new CreatureIdleAnimationRuntime.Capture(other, "mushroom-zombie-npc");
                Check(!unsupported.TryCapture(0, out _) && other.anim.runtimeAnimatorController!.DirectoryReads == 0);
            });
            Test("transition blends attacks culling disabled animators and invalid states never stop sampling", () =>
            {
                foreach (Action<Spider> corrupt in new Action<Spider>[]
                {
                    s => s.spiderState = Spider.SpiderState.Dropped, s => s.anim.enabled = false,
                    s => s.anim.gameObject.activeInHierarchy = false, s => s.anim.layerCount = 2,
                    s => s.anim.updateMode = AnimatorUpdateMode.UnscaledTime,
                    s => s.anim.cullingMode = AnimatorCullingMode.CullCompletely,
                    s => s.anim.applyRootMotion = true, s => s.anim.isHuman = true,
                    s => s.startPoint = null, s => s.spiderButt = null, s => s.ropeRender = null, s => s.line = null,
                    s => s.line!.useWorldSpace = false, s => s.line!.positionCount = 39,
                    s => s.ropeRender!.wobbleCurve = null, s => s.ropeRender!.wobbleOverLineCurve = null,
                    s => s.anim.Transition = true, s => { var v = s.anim.State; v.fullPathHash = 1; s.anim.State = v; },
                    s => { var v = s.anim.State; v.loop = false; s.anim.State = v; },
                    s => { var v = s.anim.State; v.normalizedTime = float.NaN; s.anim.State = v; },
                    s => { var v = s.anim.State; v.speed = 1; s.anim.State = v; },
                    s => s.anim.Current.Add(s.anim.Current[0]), s => s.anim.Current[0] = new AnimatorClipInfo { clip = new AnimationClip(), weight = 1 },
                    s => s.anim.Current[0] = new AnimatorClipInfo { clip = Clip(s), weight = .5f },
                    s => s.anim.speed = float.NaN, s => s.anim.speed = -1, s => s.anim.speed = 30,
                    s => s.SetSinceGrab(float.NaN),
                })
                {
                    var spider = Native(); var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web"); corrupt(spider);
                    Check(!capture.TryCapture(0, out _));
                }
                var valid = Native(); var clock = new CreatureIdleAnimationRuntime.Capture(valid, "spider-normal");
                Check(!clock.TryCapture(double.NaN, out _) && !clock.TryCapture(1_000_001, out _));
            });
            Test("silk phase clamps native timer and controller changes invalidate capture admission", () =>
            {
                var spider = Native(); var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web");
                spider.SetSinceGrab(99); Check(capture.TryCapture(0, out var a) && a!.RopePhase == 1);
                spider.SetSinceGrab(-9); Check(capture.TryCapture(0, out var b) && b!.RopePhase == 0);
                spider.anim.runtimeAnimatorController = Native().anim.runtimeAnimatorController;
                Check(!capture.TryCapture(0, out _));
            });
            Test("unknown state behaviours or controller parameter graphs retain captured geometry", () =>
            {
                foreach (Action<Spider> corrupt in new Action<Spider>[]
                {
                    s => s.anim.Behaviours = new[] { new StateMachineBehaviour() },
                    s => s.anim.runtimeAnimatorController!.Behaviours = new[] { new StateMachineBehaviour() },
                    s => s.anim.runtimeAnimatorController!.Parameters = Array.Empty<AnimatorControllerParameter>(),
                    s => s.anim.runtimeAnimatorController!.Parameters[0].type = AnimatorControllerParameterType.Trigger,
                    s => s.anim.runtimeAnimatorController!.Parameters[1].nameHash = 9,
                    s => s.anim.runtimeAnimatorController!.Parameters[2].nameHash = 1080829965,
                })
                {
                    var spider = Native(); corrupt(spider);
                    var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web");
                    Check(!capture.TryCapture(0, out _) && spider.anim.StateQueries == 0 && spider.anim.ClipQueries == 0);
                    var source = new GameObject(); source.Put(spider);
                    Check(!CreatureIdleAnimationRuntime.TryCreate(new VisualReplica(), source,
                        new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle }, out _));
                }
                var queuedWeave = Native(); var observer = new CreatureIdleAnimationRuntime.Capture(queuedWeave, "spider-web");
                queuedWeave.anim.Weaving = true; Check(!observer.TryCapture(0, out _));
            });
            Test("disabled visual Animator evaluates absolute native phase without a second state speed or source updates", () =>
            {
                var spider = Native(); var source = new GameObject(); source.Put(spider); var visual = new VisualReplica();
                var capture = new CreatureIdleAnimationRuntime.Capture(spider, "spider-web"); Check(capture.TryCapture(10, out var clock));
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock!, out var runtime));
                int queries = spider.anim.StateQueries, clips = spider.anim.ClipQueries;
                runtime!.Apply(clock!, 10.5); var clip = Clip(spider);
                var evaluator = visual.Clone.GetComponent<Animator>()!;
                Check(evaluator != spider.anim && !evaluator.enabled && !evaluator.fireEvents && !evaluator.applyRootMotion && evaluator.speed == 0);
                Check(ReferenceEquals(evaluator.runtimeAnimatorController, spider.anim.runtimeAnimatorController));
                Check(Math.Abs(evaluator.PlayedPhase - (.25 + .5 * .33)) < .000001 && evaluator.ExplicitUpdates == 1 && clip.Samples == 0);
                Check(Math.Abs(visual.Clone.EvaluatedPose - Math.Sin((.25 + .5 * .33) * Math.PI * 2)) < .000001 && evaluator.Rebinds == 1);
                for (int i = 0; i < 1000; i++) runtime.Apply(clock!, 10.5);
                Check(evaluator.ExplicitUpdates == 1 && source.ComponentReads == 1 && visual.MappingReads == 1);
                runtime.Apply(clock!, 9); Check(Math.Abs(evaluator.PlayedPhase - .92) < .000001);
                runtime.Invalidate(); runtime.Apply(clock!, 9); Check(evaluator.ExplicitUpdates == 3);
                runtime.Dispose(); runtime.Apply(clock!, 20); Check(evaluator.ExplicitUpdates == 3 && evaluator.runtimeAnimatorController == null && !evaluator.enabled);
                Check(spider.anim.StateQueries == queries && spider.anim.ClipQueries == clips);
            });
            Test("explicit idle evaluation clears all AnyState action inputs after bind and pooled reuse", () =>
            {
                var spider = Native(); var source = new GameObject(); source.Put(spider); var visual = new VisualReplica();
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle, ClipTime = .25, Speed = .33f };
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var runtime)); runtime!.Apply(clock, .5);
                var evaluator = visual.Clone.GetComponent<Animator>()!;
                Check(!evaluator.Weaving && !evaluator.AttackTrigger && !evaluator.BonkTrigger && evaluator.TriggerResets == 2);
                evaluator.Weaving = evaluator.AttackTrigger = evaluator.BonkTrigger = true;
                runtime.Invalidate(); runtime.Apply(clock, .5);
                Check(!evaluator.Weaving && !evaluator.AttackTrigger && !evaluator.BonkTrigger && evaluator.TriggerResets == 4);
                runtime.Dispose(); Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var replacement));
                replacement!.Apply(clock, 1); Check(ReferenceEquals(visual.Clone.GetComponent<Animator>(), evaluator) && evaluator.Rebinds == 2 && evaluator.ExplicitUpdates == 3);
                replacement.Dispose();
            });
            Test("existing live clone evaluators and missing native idle states cannot be admitted", () =>
            {
                var spider = Native(); var source = new GameObject(); source.Put(spider);
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle };
                var visual = new VisualReplica(); visual.Clone.Put(new Animator());
                Check(!CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out _));
                visual = new VisualReplica(); visual.Clone.Put(new Animator { enabled = false, runtimeAnimatorController = spider.anim.runtimeAnimatorController });
                Check(!CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out _));
                spider.anim.runtimeAnimatorController!.HasIdleState = false;
                visual = new VisualReplica(); Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var missingState));
                bool rejected = false; try { missingState!.Apply(clock, 0); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected); missingState!.Dispose(); Check(visual.Clone.GetComponent<Animator>()!.runtimeAnimatorController == null);
            });
            Test("hidden clone defers binding and repeated pose invalidation only rebinds after actual initialization loss", () =>
            {
                var spider = Native(); var source = new GameObject(); source.Put(spider); var visual = new VisualReplica();
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle, ClipTime = .25, Speed = .33f };
                visual.Clone.activeInHierarchy = false;
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var runtime));
                var evaluator = visual.Clone.GetComponent<Animator>()!;
                Check(!evaluator.isInitialized && evaluator.parameters.Length == 0);
                runtime!.Apply(clock, 10); Check(evaluator.Rebinds == 0 && evaluator.ExplicitUpdates == 0);
                visual.Clone.activeInHierarchy = true; runtime.Apply(clock, 10);
                for (int i = 1; i <= 300; i++) { runtime.Invalidate(); runtime.Apply(clock, 10 + i / 5d); }
                Check(evaluator.Rebinds == 1 && evaluator.ExplicitUpdates == 301);
                evaluator.isInitialized = false; runtime.Apply(clock, 70);
                Check(evaluator.Rebinds == 2 && evaluator.ExplicitUpdates == 302);
                runtime.Apply(clock, 70); Check(evaluator.Rebinds == 2 && evaluator.ExplicitUpdates == 302);
                runtime.Dispose();
            });
            Test("clone parameter signature is deferred to active bind without weakening the native source gate", () =>
            {
                var spider = Native(); var source = new GameObject(); source.Put(spider); var visual = new VisualReplica();
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle };
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var runtime));
                var evaluator = visual.Clone.GetComponent<Animator>()!;
                Check(!evaluator.isInitialized && evaluator.ParameterQueries == 0);
                runtime!.Apply(clock, 0); Check(evaluator.isInitialized && evaluator.ParameterQueries == 1 && evaluator.ExplicitUpdates == 1);
                runtime.Dispose();
                spider = Native(); source = new GameObject(); source.Put(spider); visual = new VisualReplica();
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out runtime));
                spider.anim.runtimeAnimatorController!.Parameters = Array.Empty<AnimatorControllerParameter>();
                bool rejected = false; try { runtime!.Apply(clock, 0); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && visual.Clone.GetComponent<Animator>()!.ExplicitUpdates == 0); runtime!.Dispose();
                spider = Native(); spider.anim.isInitialized = false;
                Check(!new CreatureIdleAnimationRuntime.Capture(spider, "spider-web").TryCapture(0, out _));
            });
            Test("cold replay template creates an inert evaluator without source initialization or writes", () =>
            {
                var spider = Native(); spider.anim.isInitialized = false;
                var source = new GameObject(); source.Put(spider); var visual = new VisualReplica(); visual.Clone.activeInHierarchy = false;
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle, ClipTime = .25, Speed = .33f };
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out var runtime));
                runtime!.Apply(clock, 1); var evaluator = visual.Clone.GetComponent<Animator>()!;
                Check(evaluator.Rebinds == 0 && spider.anim.ParameterQueries == 0 && !spider.anim.isInitialized);
                visual.Clone.activeInHierarchy = true; runtime.Apply(clock, 1);
                Check(evaluator.ParameterQueries == 1 && evaluator.ExplicitUpdates == 1 && evaluator.Rebinds == 1);
                Check(spider.anim.Rebinds == 0 && spider.anim.ExplicitPlays == 0 && spider.anim.ExplicitUpdates == 0 && spider.anim.BoolSets == 0 && spider.anim.TriggerResets == 0 && !spider.anim.isInitialized);
                runtime.Dispose();
                spider = Native(); spider.anim.isInitialized = false; spider.anim.runtimeAnimatorController!.Parameters = Array.Empty<AnimatorControllerParameter>();
                source = new GameObject(); source.Put(spider); visual = new VisualReplica();
                Check(CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out runtime));
                bool rejected = false; try { runtime!.Apply(clock, 1); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && visual.Clone.GetComponent<Animator>()!.ExplicitUpdates == 0 && !spider.anim.isInitialized); runtime!.Dispose();
            });
            Test("cold replay assets retain complete clip and behaviour guards before controller state entry", () =>
            {
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle };
                foreach (Action<Spider> corrupt in new Action<Spider>[]
                {
                    s => s.anim.runtimeAnimatorController!.Clips[1].Events = new[] { new AnimationEvent() },
                    s => s.anim.runtimeAnimatorController!.Clips[1].length = 1,
                    s => s.anim.runtimeAnimatorController!.Clips[2].name = "Unknown",
                    s => s.anim.runtimeAnimatorController!.Clips[3].isLooping = false,
                    s => s.anim.runtimeAnimatorController!.Behaviours = new[] { new StateMachineBehaviour() },
                })
                {
                    var spider = Native(); spider.anim.isInitialized = false; corrupt(spider);
                    var source = new GameObject(); source.Put(spider); var visual = new VisualReplica();
                    Check(!CreatureIdleAnimationRuntime.TryCreate(visual, source, clock, out _) && CreatureIdleAnimationRuntime.LastRejection.Length > 0);
                    Check(spider.anim.Rebinds == 0 && spider.anim.ExplicitUpdates == 0);
                }
                var valid = Native(); valid.anim.isInitialized = false; var native = new GameObject(); native.Put(valid); var clone = new VisualReplica();
                Check(CreatureIdleAnimationRuntime.TryCreate(clone, native, clock, out var runtime));
                var evaluator = clone.Clone.GetComponent<Animator>()!; evaluator.Behaviours = new[] { new StateMachineBehaviour() };
                bool rejected = false; try { runtime!.Apply(clock, 0); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && evaluator.Rebinds == 0 && evaluator.ExplicitUpdates == 0); runtime!.Dispose();
                clone = new VisualReplica(); Check(CreatureIdleAnimationRuntime.TryCreate(clone, native, clock, out runtime));
                evaluator = clone.Clone.GetComponent<Animator>()!; evaluator.Transition = true;
                rejected = false; try { runtime!.Apply(clock, 0); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && evaluator.ExplicitUpdates == 1); runtime!.Dispose();
            });
            Test("playback cannot sample an original native Transform or an unfinished clone", () =>
            {
                var source = new GameObject(); source.Put(Native());
                var clock = new CreatureIdleAnimation { Clip = CreatureIdleAnimationRules.SpiderIdle };
                Check(!CreatureIdleAnimationRuntime.TryCreate(new VisualReplica { IsReady = false }, source, clock, out _));
                Check(!CreatureIdleAnimationRuntime.TryCreate(new VisualReplica { MappingExists = false }, source, clock, out _));
                Check(!CreatureIdleAnimationRuntime.TryCreate(new VisualReplica { ReturnNative = true }, source, clock, out _));
                Check(!CreatureIdleAnimationRuntime.TryCreate(new VisualReplica(), new GameObject(), clock, out _));
            });
            Console.WriteLine($"{passed} native idle animation contract checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static Spider Native()
    {
        var clip = new AnimationClip { name = "SpiderIdle" };
        var spider = new Spider(); spider.anim.isInitialized = true;
        spider.anim.runtimeAnimatorController = new RuntimeAnimatorController { name = "Spider (1)", Clips = new[] { clip,
            new AnimationClip { name = "SpiderAttack", length = .75f, isLooping = false },
            new AnimationClip { name = "SpiderBonk", length = .5833333134651184f },
            new AnimationClip { name = "SpiderWeave", length = .5833333134651184f } } };
        spider.anim.Current.Add(new AnimatorClipInfo { clip = clip, weight = 1 }); return spider;
    }
    private static AnimationClip Clip(Spider spider) => spider.anim.runtimeAnimatorController!.Clips[0];
    private static void Check(bool value) { if (!value) throw new Exception("Native idle animation contract failed."); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
}
