using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakReplayLab;

// PEAK's Waterfall_Spline material uses W/Peak_Waterfall and _TimeParameters.x.
// With replay physics stopped, Unity's scaled time is stopped too. URP overwrites
// shader time inside its rendering commands, after ordinary Update/LateUpdate.
// Change only that audited time argument, leaving native vector construction,
// all simulation callbacks, command buffers, delta time and audio untouched.
internal sealed class ReplayShaderClock : IDisposable
{
    private static readonly string[] shaderGlobals =
        { "_Time", "_SinTime", "_CosTime", "unity_DeltaTime", "_TimeParameters", "_LastTimeParameters" };
    private static ReplayShaderClock? current;
    private readonly ReplayVisualClock clock = new(UnityEngine.Time.time);
    private readonly Vector4[] initial = new Vector4[shaderGlobals.Length];
    private readonly Harmony harmony = new("cn.mylus.peakreplaylab.shader-clock." + Guid.NewGuid().ToString("N"));
    private bool installed, disposed;

    public ReplayShaderClock(Action<string>? warning)
    {
        for (int i = 0; i < shaderGlobals.Length; i++) initial[i] = Shader.GetGlobalVector(shaderGlobals[i]);
        try
        {
            if (current != null) throw new InvalidOperationException("A replay shader clock already owns rendering.");
            var type = AccessTools.TypeByName("UnityEngine.Rendering.Universal.ScriptableRenderer");
            MethodInfo? method = type?.GetMethod("SetShaderTimeValues", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var parameters = method?.GetParameters();
            if (method == null || method.ReturnType != typeof(void) || parameters == null || parameters.Length != 4 ||
                parameters[0].ParameterType.FullName != "UnityEngine.Rendering.IBaseCommandBuffer" ||
                parameters[1].ParameterType != typeof(float) || parameters[2].ParameterType != typeof(float) || parameters[3].ParameterType != typeof(float))
                throw new MissingMethodException("The installed URP shader-time writer does not match the audited signature.");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ReplayShaderClock), nameof(BeforeShaderTime)));
            installed = true; current = this;
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            warning?.Invoke("回放流水动画无法接入原生渲染时钟：" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    public void SetTime(double recordingTime) { if (!disposed && installed) clock.SetTime(recordingTime); }

    // Positional injection does not depend on a game's original argument names.
    // All cameras drawing the replay world receive the same phase. Outside the
    // active presentation, including loading/return, URP receives native time.
    private static void BeforeShaderTime(ref float __1)
    {
        current?.clock.Override(ReplaySafety.Active, ReplaySafety.Presenting, ref __1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; clock.Close();
        bool ownsGlobals = ReferenceEquals(current, this);
        if (ownsGlobals) current = null;
        harmony.UnpatchSelf(); installed = false;
        // Restore the pre-replay globals immediately; the next native render
        // continues to regenerate them normally. No replay hook remains live.
        if (ownsGlobals)
            for (int i = 0; i < shaderGlobals.Length; i++) Shader.SetGlobalVector(shaderGlobals[i], initial[i]);
    }
}
