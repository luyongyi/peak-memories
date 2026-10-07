using PeakReplayLab;

internal static class FullReplayInspection
{
    // Uses one explicitly selected local recording. Never modifies the original
    // or prints participant identity. Not a Unity performance measurement.
    public static void Run(string path)
    {
        var source = ReplayFiles.Read(path);
        if (source.Header.Schema != ReplayRules.CurrentSchema) throw new InvalidDataException("Full inspection requires a current-format source.");
        string root = Path.Combine(Path.GetTempPath(), "peak-full-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writer = new FullReplayWriter(root, source.Header);
            foreach (var frame in source.Frames)
            {
                long bytes = RollingBuffer.Estimate(frame);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (writer.Stats.QueuedFrames >= 8 || writer.Stats.QueuedBytes + bytes > 128L * 1024 * 1024)
                {
                    if (writer.Fault != null) throw writer.Fault;
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Full inspection worker stalled.");
                    Thread.Sleep(1);
                }
                if (!writer.TryEnqueue(frame, bytes)) throw new Exception("Full inspection enqueue failed: " + writer.FailureCode);
            }
            var result = writer.CompleteAsync("offline-verification").GetAwaiter().GetResult();
            if (result.Status != "completed" || result.WrittenFrames != source.Frames.Count || result.UnwrittenFrames != 0)
                throw new Exception("Full inspection failed: " + result.FaultCode + ": " + result.Fault);
            if (Directory.GetFiles(root).Length != 1 || Directory.GetDirectories(root).Length != 0)
                throw new Exception("A continuous recording generated more than one file.");
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            if (!info.Complete || info.FrameCount != source.Frames.Count) throw new Exception("Full recording metadata mismatch.");
            var seen = new HashSet<long>();
            var rebaser = new ContinuousReplayRebaser(source.Frames[0].T);
            // Reverse order exercises truly independent seeking rather than
            // accidentally carrying delta state forward from the previous page.
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                var comparer = new ContinuousReplayInspection.ImmutableDataComparer();
                for (int i = 0; i < clip.Frames.Count; i++)
                {
                    long frameIndex = page.FirstFrameIndex + i;
                    if (!comparer.Equal(rebaser.Apply(source.Frames[checked((int)frameIndex)]), clip.Frames[i]))
                        throw new Exception($"Full inspection field mismatch, frame={frameIndex}.");
                    seen.Add(frameIndex);
                }
            }
            if (seen.Count != source.Frames.Count) throw new Exception("Full recording lost sampled frames.");
            using var timeline = new PagedReplayTimeline(info.Header, info.Duration, info.FrameCount, info.ActorIds,
                info.Pages.Select(p => new ReplayPageRange(p.Start, p.End)).ToArray(),
                (index, cancel) => FullReplayArchive.ReadPage(result.FilePath, info, index, cancel), info.Complete);
            foreach (double time in new[] { 0, info.Duration * .83, info.Duration * .15, info.Duration })
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!timeline.TrySample(time, out _))
                { if (DateTime.UtcNow > deadline) throw new TimeoutException("Full recording seek stalled."); Thread.Sleep(1); }
                if (timeline.Duration != info.Duration) throw new Exception("Seeking changed the global timeline.");
            }
            Console.WriteLine($"FULL-INSPECT seconds={info.Duration:F6} uniqueFrames={seen.Count} files=1 bytes={result.BytesWritten} " +
                $"allFieldsEqual=true reverseReads=true seeks=4 cachedPages={timeline.CachedPageCount} gaps100ms={info.GapOver100ms}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
