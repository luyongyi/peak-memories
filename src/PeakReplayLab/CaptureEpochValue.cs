using System;

namespace PeakReplayLab;

// A synchronous capture scope can observe a shared native descriptor many
// times. Retain it only for that exact epoch; a later tick must read again.
// This value type adds no per-read delegate, wrapper or descriptor allocation.
internal struct CaptureEpochValue<T>
{
    private bool known;
    private long epoch;
    private T value;

    internal T Read<TSource>(TSource source, long currentEpoch, Func<TSource, T> read)
    {
        if (!known || epoch != currentEpoch)
        {
            // A failed native getter must remain retryable in this epoch.
            T observed = read(source);
            value = observed; epoch = currentEpoch; known = true;
        }
        return value;
    }
}
