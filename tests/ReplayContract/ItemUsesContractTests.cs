using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ItemUsesContractTests
{
    // The native Swamp Frog reports 100M, and a normal ReduceUsesRPC produces
    // 99,999,999. This is an int counter, not a replay array size or byte budget.
    private static readonly int[] NativeCounts = { -1, 0, 1_000_001, 100_000_000, 99_999_999, int.MaxValue };

    public static void Run(Action<string, Action> test)
    {
        test("native large item use counters validate in world player and backpack slots", () =>
        {
            foreach (int uses in NativeCounts)
            {
                var frame = Frame(0, Item(uses), uses);
                ReplayRules.ValidateCapture(frame, -1);
                ReplayRules.Validate(frame, -1);
                ReplayRules.ValidateStored(frame, -1, new ReplayRules.ValidationMemo());
            }
        });
        test("item use counters below the native minus-one marker still fail capture and stored validation", () =>
        {
            foreach (int invalid in new[] { -2, int.MinValue })
            foreach (int location in new[] { 0, 1, 2 })
            {
                var frame = Frame(0, Item(100_000_000), 100_000_000);
                if (location == 0) frame.Items[0].Uses = invalid;
                else frame.Actors[0].Inventory[location - 1].Uses = invalid;
                Reject(() => ReplayRules.ValidateCapture(frame, -1));
                Reject(() => ReplayRules.Validate(frame, -1));
                Reject(() => ReplayRules.ValidateStored(frame, -1, new ReplayRules.ValidationMemo()));
            }
        });
        test("large item use counters roundtrip exactly through tree and typed sparse deltas", () =>
        {
            var writer = new ReplayDeltaCodec.Writer();
            var tree = new ReplayDeltaCodec.Reader();
            var typed = new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema);
            for (int i = 0; i < NativeCounts.Length; i++)
            {
                int expected = NativeCounts[i];
                var record = JObject.FromObject(writer.Encode(Frame(i * .25, Item(expected), expected)));
                foreach (var decoded in new[] { tree.Decode(record), typed.DecodeRecord(record.ToString(Formatting.None))! })
                {
                    ReplayRules.Validate(decoded, -1);
                    EqualCounts(decoded, expected);
                }
            }
        });
        test("item use counts remain Int32 and reject overflowing or fractional JSON values", () =>
        {
            var baseline = JObject.FromObject(new ReplayDeltaCodec.Writer().Encode(Frame(0, Item(100_000_000), 100_000_000)));
            foreach (int location in new[] { 0, 1, 2 })
            foreach (JToken invalid in new JToken[] { new JValue((long)int.MaxValue + 1), new JValue((long)int.MinValue - 1), new JValue(.5) })
            {
                var record = (JObject)baseline.DeepClone();
                if (location == 0) record["Items"]![0]!["Uses"] = invalid.DeepClone();
                else record["Actors"]![0]!["Inventory"]![location - 1]!["Uses"] = invalid.DeepClone();
                Reject(() => new ReplayDeltaCodec.Reader().Decode(record));
                Reject(() => new ReplayDeltaCodec.StreamReader(ReplayRules.CurrentSchema).DecodeRecord(record.ToString(Formatting.None)));
            }
        });
        test("native Frog counters preserve original and decremented values across full recording pages and reverse seeks", () => InRoot(root =>
        {
            int[] values = { 100_000_000, 99_999_999, int.MaxValue, -1, 0 };
            var items = values.Select(Item).ToArray();
            var frames = Enumerable.Range(0, 65).Select(i => Frame(100 + i / 4d, items[(i / 8) % values.Length], values[(i / 8) % values.Length])).ToArray();
            var writer = new FullReplayWriter(root, new ReplayHeader { Scene = "Level_NativeItemUses" });
            foreach (var frame in frames)
            {
                ReplayRules.ValidateCapture(frame, -1);
                Check(writer.TryEnqueue(frame), "Writer rejected a native item counter.");
            }
            var result = writer.CompleteAsync().GetAwaiter().GetResult();
            Check(result.Status == "completed", result.Fault ?? "Full recording did not complete.");
            Check(result.AcceptedFrames == frames.Length && result.WrittenFrames == frames.Length && result.UnwrittenFrames == 0);
            var info = FullReplayArchive.ReadInfo(result.FilePath);
            Check(info.Complete && info.Pages.Length > 3, "Native counter recording did not span independently seekable pages.");
            var observed = new HashSet<int>();
            foreach (var page in info.Pages.AsEnumerable().Reverse())
            {
                var clip = FullReplayArchive.ReadPage(result.FilePath, info, page.Index);
                Check(clip.Complete);
                foreach (var frame in clip.Frames)
                {
                    int index = (int)Math.Round(frame.T * 4);
                    Check(index >= 0 && index < frames.Length);
                    EqualCounts(frame, values[(index / 8) % values.Length]);
                    observed.Add(index);
                }
            }
            Check(observed.Count == frames.Length, "Cross-page decoding lost source frames.");
        }));
    }

    private static ItemFrame Item(int uses) => new() { Key = "native-frog", ItemId = 172, Prefab = "Frog", Name = "Frog", Uses = uses };
    private static ReplayFrame Frame(double time, ItemFrame item, int inventoryUses) => new()
    {
        T = time, Items = new[] { item }, Actors = new[] { new ActorFrame { Id = "synthetic-scout", InventoryKnown = true, Inventory = new[] {
            new InventoryFrame { Empty = false, Slot = 0, ItemId = 172, Uses = inventoryUses },
            new InventoryFrame { Empty = false, Slot = 1, Backpack = true, ItemId = 172, Uses = inventoryUses },
        } } },
    };
    private static void EqualCounts(ReplayFrame frame, int expected)
    {
        Check(frame.Items.Single().Uses == expected);
        Check(frame.Actors.Single().Inventory.Length == 2 && frame.Actors.Single().Inventory.All(slot => slot.Uses == expected));
    }
    private static void Check(bool condition, string message = "Native item use counter assertion failed.")
    { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid item use counter was accepted."); }
    private static void InRoot(Action<string> action)
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(Path.Combine(tempRoot, "PeakItemUsesContract-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe temporary test path.");
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
}
