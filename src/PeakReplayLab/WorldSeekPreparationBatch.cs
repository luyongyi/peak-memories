using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PeakReplayLab;

// Synchronous Unity calls cannot be interrupted. This keeps them apart from
// each other and from destination-pose reconstruction, and lets a newer seek
// discard all remaining calls before the next frame.
internal sealed class WorldSeekPreparationBatch
{
    private readonly struct Step
    {
        public readonly Action Apply;
        public readonly bool YieldAfter;
        public Step(Action apply, bool yieldAfter) { Apply = apply; YieldAfter = yieldAfter; }
    }

    private readonly List<Step> steps = new(32);
    private int next, lastFrame = -1;
    public int Count => steps.Count;

    public void Add(Action action, bool yieldAfter) => steps.Add(new Step(action, yieldAfter));

    public void Clear()
    {
        steps.Clear(); next = 0;
        // Preserve lastFrame. A superseding seek in the same render frame must
        // not run another expensive native switch on top of the old one.
    }

    // true means all world work completed, but callers still yield this frame.
    public bool Advance(int renderedFrame)
    {
        if (next >= steps.Count) return true;
        if (lastFrame == renderedFrame) return false;
        lastFrame = renderedFrame;
        long started = Stopwatch.GetTimestamp();
        int applied = 0;
        while (next < steps.Count && applied < 8)
        {
            var step = steps[next++];
            step.Apply(); applied++;
            if (step.YieldAfter || (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency >= 2) break;
        }
        return next >= steps.Count;
    }
}
