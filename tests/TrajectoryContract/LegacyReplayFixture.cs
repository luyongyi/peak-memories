using System.Text;
using Newtonsoft.Json;
using PeakReplayLab;

// Independently write an actual old-format fixture: production only writes its current schema.
internal static class LegacyReplayFixture
{
    private static string Json(object value) => JsonConvert.SerializeObject(value, ReplayFiles.Json);
    public static string Save(string root, ReplayHeader header, ReplayFrame[] frames, int splitAt = -1)
    {
        string path = Path.Combine(root, "legacy-" + Guid.NewGuid().ToString("N") + ".peakrun");
        var rebased = frames.Select(new ContinuousReplayRebaser(frames[0].T).Apply).ToArray();
        var pages = new List<FullReplayPage>();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var binary = new BinaryWriter(file, Encoding.UTF8, true);
        byte[] head = Encoding.UTF8.GetBytes(Json(header)); binary.Write(Encoding.ASCII.GetBytes("PEAKRUN1")); binary.Write(head.Length); binary.Write(head); binary.Flush();
        int first = 0;
        while (first < rebased.Length)
        {
            int last = splitAt > first ? splitAt - 1 : rebased.Length - 1;
            long offset = file.Position, rawBytes = 0; var budget = new ContinuousReplayBudget();
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest, true))
            using (var text = new StreamWriter(gzip, new UTF8Encoding(false), 4096, true))
            {
                void Write(object value) { string line = Json(value); rawBytes += Encoding.UTF8.GetByteCount(line) + 1; text.Write(line); text.Write('\n'); }
                Write(header); var codec = new ReplayDeltaCodec.Writer(header.Schema);
                for (int i = first; i <= last; i++) { budget.Commit(rebased[i], budget.Additional(rebased[i])); Write(codec.Encode(rebased[i])); }
                Write(new { Type = "end" });
            }
            pages.Add(new FullReplayPage { Index = pages.Count, Start = rebased[first].T, End = rebased[last].T,
                Offset = offset, Length = file.Position - offset, RawBytes = rawBytes, DecodedBytes = budget.Bytes,
                FrameCount = last - first + 1, FirstFrameIndex = first });
            first = last + 1;
        }
        var index = new FullReplayIndex { Version = 1, Header = header, Duration = rebased.Last().T, FrameCount = frames.Length,
            ActorIds = frames.SelectMany(f => f.Actors).Select(a => a.Id).Distinct().ToArray(), Complete = true, Reason = "legacy-test", Pages = pages.ToArray() };
        byte[] encoded = Encoding.UTF8.GetBytes(Json(index)); binary.Write(encoded); binary.Write((long)encoded.Length); binary.Write(Encoding.ASCII.GetBytes("PEAKEND1"));
        return path;
    }
}
