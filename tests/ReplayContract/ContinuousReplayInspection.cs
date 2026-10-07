using System.Reflection;
using System.Runtime.CompilerServices;
using PeakReplayLab;

internal static class ContinuousReplayInspection
{
    // Offline verification of a user-selected file. Does not modify the source,
    // emit participant identity, or imply a Unity/60 Hz performance result.
    public static void Run(string sourcePath)
    {
        var source = ReplayFiles.Read(sourcePath);
        if (source.Header.Schema != ReplayRules.CurrentSchema) throw new InvalidDataException("Continuous inspection requires a current-format source.");
        string root = Path.Combine(Path.GetTempPath(), "peak-continuous-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writer = new ContinuousReplayWriter(root, source.Header);
            foreach (var frame in source.Frames)
            {
                long size = RollingBuffer.Estimate(frame);
                // Test input is deliberately paced to the worker; this is a
                // lossless resegmentation check, not a synthetic disk overload.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (writer.Stats.QueuedFrames >= 8 || writer.Stats.QueuedBytes + size > 128L * 1024 * 1024)
                {
                    if (writer.Fault != null) throw writer.Fault;
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Continuous inspection worker stalled.");
                    Thread.Sleep(1);
                }
                if (!writer.TryEnqueue(frame, size)) throw new Exception("Inspection enqueue failed: " + writer.FailureCode);
            }
            var result = writer.CompleteAsync("offline-verification").GetAwaiter().GetResult();
            if (result.Status != "completed" || result.WrittenFrames != source.Frames.Count || result.UnwrittenFrames != 0)
                throw new Exception("Continuous inspection not complete: " + result.FaultCode + ": " + result.Fault);
            var catalog = DebugRunCatalog.Scan(root);
            if (catalog.Runs.Length != 1 || !catalog.Runs[0].Complete) throw new Exception("Continuous catalog did not report a complete run.");
            var covered = new HashSet<int>(); int decodedFrames = 0;
            foreach (var part in catalog.Runs[0].Parts)
            {
                if (!part.Playable) throw new Exception("Segment was not catalog-playable: " + part.Error);
                var actual = ReplayFiles.Read(part.Path);
                if (!actual.Complete || actual.Frames.Count != part.FrameCount) throw new Exception("Segment failed full validation.");
                var rebaser = new ContinuousReplayRebaser(part.NativeStart);
                var comparer = new ImmutableDataComparer();
                var expected = source.Frames.Select((frame, index) => (frame, index))
                    .Where(v => v.frame.T >= part.NativeStart && v.frame.T <= part.NativeEnd).ToArray();
                if (expected.Length != actual.Frames.Count) throw new Exception("Frame count changed across segment boundary.");
                for (int i = 0; i < expected.Length; i++)
                {
                    if (!comparer.Equal(rebaser.Apply(expected[i].frame), actual.Frames[i]))
                        throw new Exception($"Lossless frame comparison failed in part {part.Index}, sample {i}.");
                    covered.Add(expected[i].index); decodedFrames++;
                }
                Console.WriteLine($"CONTINUOUS-INSPECT part={part.Index} samples={actual.Frames.Count} allFieldsEqual=true");
            }
            if (covered.Count != source.Frames.Count) throw new Exception("Some source frames are absent from sealed segments.");
            Console.WriteLine($"CONTINUOUS-INSPECT sourceSeconds={source.Duration:F6} sourceFrames={source.Frames.Count} " +
                $"sourceBytes={new FileInfo(sourcePath).Length} segments={result.CompletedSegments} storedBytes={result.BytesWritten} " +
                $"decodedFramesWithBoundaryDuplicates={decodedFrames} uniqueFrames={covered.Count} allFieldsEqual=true " +
                $"gaps100ms={result.GapOver100ms} maxGapMs={result.LargestGapSeconds * 1000:F3}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Compare every public data property/field, including exact float/double
    // values. Both graphs are immutable: already-compared object pairs can be
    // reused without serializing large identical static hierarchies 4,000 times.
    internal sealed class ImmutableDataComparer
    {
        private sealed class Pairs : IEqualityComparer<(object Left, object Right)>
        {
            public bool Equals((object Left, object Right) a, (object Left, object Right) b) =>
                ReferenceEquals(a.Left, b.Left) && ReferenceEquals(a.Right, b.Right);
            public int GetHashCode((object Left, object Right) value) =>
                HashCode.Combine(RuntimeHelpers.GetHashCode(value.Left), RuntimeHelpers.GetHashCode(value.Right));
        }
        private readonly HashSet<(object, object)> visited = new(new Pairs());
        private readonly Dictionary<Type, (PropertyInfo[] Properties, FieldInfo[] Fields)> members = new();
        public bool Equal(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.GetType() != right.GetType()) return false;
            var type = left.GetType();
            if (type.IsValueType || left is string) return left.Equals(right);
            if (!visited.Add((left, right))) return true;
            if (left is Array a && right is Array b)
            {
                if (a.Rank != 1 || b.Rank != 1 || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (!Equal(a.GetValue(i), b.GetValue(i))) return false;
                return true;
            }
            if (type.Namespace != "PeakReplayLab") throw new Exception("Unrecognized inspection data type: " + type.Name);
            if (!members.TryGetValue(type, out var cached))
                members.Add(type, cached = (type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray(),
                    type.GetFields(BindingFlags.Public | BindingFlags.Instance)));
            foreach (var property in cached.Properties) if (!Equal(property.GetValue(left), property.GetValue(right))) return false;
            foreach (var field in cached.Fields) if (!Equal(field.GetValue(left), field.GetValue(right))) return false;
            return true;
        }
    }
}
