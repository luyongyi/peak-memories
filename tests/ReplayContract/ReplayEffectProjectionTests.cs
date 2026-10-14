using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PeakReplayLab;

internal static class ReplayEffectProjectionTests
{
    private static readonly Func<char[], int, int, ReplayJsonBufferPool, JObject> Parse =
        (Func<char[], int, int, ReplayJsonBufferPool, JObject>)typeof(ReplayFiles)
            .GetMethod("ParseEffectObject", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<char[], int, int, ReplayJsonBufferPool, JObject>));

    internal static void Run(Action<string, Action> test)
    {
        test("effect projection preserves semantic parser output for all supported schemas and arbitrary field order", () =>
        {
            foreach (int schema in Enumerable.Range(10, 5))
            foreach (string text in new[] {
                "{}", "{\"Type\":\"end\"}",
                "{\"Effects\":[],\"World\":{\"ActiveMapObjects\":[true,false],\"Day\":[1,2,3]},\"T\":1,\"ObjectsFull\":true,\"Type\":\"frame\"}",
                "{\"Actors\":[{\"Name\":\"中文\\n\\u4e00\",\"Position\":[1,-2.5,3e-4]}],\"Type\":\"end\",\"Events\":[null,false,{},[]]}",
                "{\"Type\":\"end\",\"Unknown\":{\"ignored\":1},\"World\":{\"ActiveMapObjects\":[]}}",
                "{\"Items\":[{\"X\":1,\"X\":2}],\"Type\":\"end\"}",
                " { \"RemovedEffects\" : [\"a\",\"b\"] , \"EffectOrder\" : [\"b\",\"a\"] , \"Effects\": [{\"Key\":\"a\",\"Seed\":123}] , \"T\": -0.5e+1 } "
            }) Equivalent(text, schema, requireFast: true);
        });
        test("effect projection retains unknown and duplicate top and world fields for the original validation", () =>
        {
            foreach (string text in new[] {
                "{\"Type\":\"frame\",\"Unknown\":1}",
                "{\"Actors\":[],\"Actors\":[],\"Type\":\"end\"}",
                "{\"World\":{\"ActiveMapObjects\":[],\"ActiveMapObjects\":[]}}",
                "{\"World\":{\"Unknown\":1},\"Type\":\"end\"}",
                "{\"Type\":\"end\",\"World\":{\"Day\":1,\"Day\":2}}",
                "{\"Type\":\"end\",\"Effects\":[{\"Key\":\"a\",\"Key\":\"a\"}]}"
            }) Equivalent(text, 14, requireFast: true);
        });
        test("effect projection validates ignored strings containers and literal boundaries before masking", () =>
        {
            foreach (string value in new[] {
                "[1 2]", "{\"x\" 1}", "{\"x\":1 \"y\":2}", "[}", "{]", "[1,]", "{\"x\":1,}",
                "truex", "false0", "nullx", "tru", "nul", "\"unfinished", "\"bad\\q\"", "\"bad\\u123\"", "\"bad\\u12xz\"",
                "\"raw\nnewline\"", "\"raw\tcontrol\"", "\"raw\u0000control\"", "\"backslash\\", ""
            }) Fallback("{\"Actors\":" + value + ",\"Type\":\"end\"}");
        });
        test("effect projection rejects incomplete and multiple root values without changing source", () =>
        {
            foreach (string text in new[] { "", "[]", "null", "{", "{\"A\"}", "{\"A\":}", "{\"A\":1", "{}{}", "{} 1", "{} garbage", "{\"A\":1,}" })
                Fallback(text);
        });
        test("effect projection defers Newtonsoft extensions and encoded field identities to original input", () =>
        {
            foreach (string text in new[] {
                "{/*comment*/\"Type\":\"end\"}", "{\"Actors\":/*comment*/[],\"Type\":\"end\"}",
                "{'Type':'end'}", "{Actors:[],Type:'end'}", "{\"Actors\":NaN}", "{\"Actors\":Infinity}",
                "{\"Actors\":-Infinity}", "{\"Actors\":undefined}", "{\"Actors\":new Date(1)}", "{\"Actors\":0x10}",
                "{\"\\u0054ype\":\"end\"}", "{\"Actors\":[{\"\\u0041\":1}]}", "{\"World\":{\"\\u0057ind\":1}}",
                "{\"Actors\":\"\\ud83d\\ude00\"}", "{\"Actors\":\"\ud83d\ude00\"}", "{\"Actors\":\"\\ud800\"}",
                "{\"Actors\":1}\u00a0", "{\"World\":null}", "{\"World\":[]}"
            }) Fallback(text);
        });
        test("effect projection admits bounded ordinary numbers and falls back on lexical or range uncertainty", () =>
        {
            foreach (string number in new[] { "0", "-0", "1", "-1", "999999999999999999", "-999999999999999999",
                "1e300", "-1e-300", "1E+003", "123.456", "0.000001", "1.2345678901234567890123", "0e300" })
                Equivalent("{\"Actors\":[" + number + "],\"T\":" + number + "}", 14, requireFast: true);
            foreach (string number in new[] { "01", "-01", "+1", "--1", "1.", ".1", "1e", "1e+", "1e-", "1e301", "1e-301",
                "9999999999999999999", "9223372036854775808", "999999999999999999e300", "0.1e-300",
                "1.23456789012345678901234", "1e00000000000000000000000", "1e999999999999999999999999", "1 2" })
                Fallback("{\"Actors\": [" + number + "]}");
        });
        test("effect projection enforces original nesting depth even entirely inside skipped values", () =>
        {
            foreach (int depth in new[] { 1, 2, 22, 23 })
                Equivalent("{\"Actors\":" + new string('[', depth) + "0" + new string(']', depth) + "}", 14, requireFast: true);
            Fallback("{\"Actors\":" + new string('[', 24) + "0" + new string(']', 24) + "}");
            Equivalent("{\"World\":{\"Day\":" + new string('[', 22) + "0" + new string(']', 22) + "}}", 14, requireFast: true);
            Fallback("{\"World\":{\"Day\":" + new string('[', 23) + "0" + new string(']', 23) + "}}");
        });
        test("effect projection reuses bounded output and allocates nothing for warmed record scans", () =>
        {
            var projection = new ReplayEffectProjection();
            char[] input = ("{\"Actors\":[{\"JointPose\":\"" + new string('a', 8192) + "\"}],\"Type\":\"end\"}").ToCharArray();
            Check(projection.TryProject(input, input.Length)); char[] output = projection.Buffer;
            for (int i = 0; i < 100; i++) Check(projection.TryProject(input, input.Length));
            long before = GC.GetAllocatedBytesForCurrentThread(); bool successful = true;
            for (int i = 0; i < 10000; i++) successful &= projection.TryProject(input, input.Length);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(successful && allocated == 0 && ReferenceEquals(output, projection.Buffer));
            Check(projection.Length < 64 && projection.Buffer.Length <= ReplayRules.MaxLineCharacters);
        });
        test("effect projection false never exposes a partial result and instances have separate scratch", () =>
        {
            var first = new ReplayEffectProjection(); var second = new ReplayEffectProjection();
            char[] valid = "{\"Type\":\"end\"}".ToCharArray(); Check(first.TryProject(valid, valid.Length));
            Check(!first.TryProject(valid, -1) && first.Length == 0);
            Check(!first.TryProject(valid, valid.Length + 1) && first.Length == 0);
            Check(!first.TryProject(first.Buffer, 0) && first.Length == 0);
            Check(second.TryProject(valid, valid.Length) && !ReferenceEquals(first.Buffer, second.Buffer));
            char[] malformed = "{\"Type\":\"end\",\"Actors\":[1e]}".ToCharArray();
            Check(!first.TryProject(malformed, malformed.Length) && first.Length == 0);
            Check(first.TryProject(valid, valid.Length) && first.Length == valid.Length);
        });
        test("effect projection growth and scratch reuse preserve previously parsed effect payloads", () =>
        {
            var projection = new ReplayEffectProjection();
            string text = "{\"Effects\":[{\"Key\":\"" + new string('k', 10000) + "\"}],\"Actors\":[1]}";
            Equivalent(text, 14, requireFast: true);
            char[] input = text.ToCharArray(); Check(projection.TryProject(input, input.Length));
            Check(projection.Buffer.Length >= projection.Length && projection.Buffer.Length <= ReplayRules.MaxLineCharacters);
            var old = Parse(projection.Buffer, projection.Length, 14, new ReplayJsonBufferPool());
            char[] next = "{\"Type\":\"end\"}".ToCharArray(); Check(projection.TryProject(next, next.Length));
            Check((string?)old["Effects"]![0]!["Key"] == new string('k', 10000));
        });
        test("effect projection end to end preserves footer ordering and original invalid-record rejection", () =>
        {
            var header = new ReplayHeader { Schema = ReplayRules.CurrentSchema, Scene = "Level_Synthetic", GameVersion = "test", BuildId = 123 };
            var codec = new ReplayDeltaCodec.Writer();
            string frames = JsonConvert.SerializeObject(header, ReplayFiles.Json) + "\n" +
                JsonConvert.SerializeObject(codec.Encode(new ReplayFrame { T = 0 }), ReplayFiles.Json) + "\n" +
                JsonConvert.SerializeObject(codec.Encode(new ReplayFrame { T = .05 }), ReplayFiles.Json) + "\n";
            var ordinary = ReplayFiles.ReadEffects(new StringReader(frames + "{\"Actors\":[{\"a\":1}],\"Type\":\"end\"}\n"));
            Check(ordinary.Complete && ordinary.Frames.Count == 2);
            Reject(() => ReplayFiles.ReadEffects(new StringReader(frames + "{\"Type\":\"end\"}\n{}\n")));
            Reject(() => ReplayFiles.ReadEffects(new StringReader(frames + "{\"Type\":\"end\",\"Actors\":[1e]}\n")));
            Reject(() => ReplayFiles.ReadEffects(new StringReader(frames + "{\"Type\":\"end\",\"World\":{\"Unknown\":1}}\n")));
        });
    }

    private static void Equivalent(string text, int schema, bool requireFast)
    {
        char[] input = text.ToCharArray(), original = (char[])input.Clone();
        var projection = new ReplayEffectProjection(); bool fast = projection.TryProject(input, input.Length);
        Check(!requireFast || fast); Check(input.SequenceEqual(original));
        JObject? old = null, current = null; bool oldFailed = false, currentFailed = false;
        try { old = Parse(input, input.Length, schema, new ReplayJsonBufferPool()); } catch (Exception e) when (Expected(e)) { oldFailed = true; }
        try { current = Parse(fast ? projection.Buffer : input, fast ? projection.Length : input.Length, schema, new ReplayJsonBufferPool()); }
        catch (Exception e) when (Expected(e)) { currentFailed = true; }
        Check(oldFailed == currentFailed && (oldFailed || JToken.DeepEquals(old, current)));
    }
    private static void Fallback(string text)
    {
        char[] input = text.ToCharArray(); var projection = new ReplayEffectProjection();
        Check(!projection.TryProject(input, input.Length) && projection.Length == 0);
        Equivalent(text, 14, requireFast: false);
    }
    private static bool Expected(Exception e) => e is InvalidDataException or JsonException or OverflowException or ArgumentException;
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (Expected(e)) { return; } throw new Exception("Invalid projected record was accepted."); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Effect projection assertion failed."); }
}
