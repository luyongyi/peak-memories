using PeakReplayLab;

internal static class NativeCaptureDirectoryTests
{
    private sealed record State(string Key, bool Invalid = false);
    public static void Run(Action<string, Action> test)
    {
        test("sparse immutable directory updates validate changed states and retain collection checks", () =>
        {
            int checkedStates = 0, collections = 0, identities = 0;
            var validator = new ReplayTrackValidation<State>(s => { identities++; return s.Key; }, 600,
                s => { checkedStates++; if (s.Invalid) throw new InvalidDataException(); }, _ => collections++);
            var first = Enumerable.Range(0, 479).Select(i => new State(i.ToString())).ToArray();
            validator.Validate(first);
            int initialKeys = identities;
            var moved = (State[])first.Clone(); moved[120] = new State("120");
            validator.Validate(moved);
            Check(checkedStates == 480 && collections == 2 && identities - initialKeys == 2);
            var invalid = (State[])moved.Clone(); invalid[120] = new State("120", true);
            Reject(() => validator.Validate(invalid));
            var next = (State[])moved.Clone(); next[120] = new State("120");
            validator.Validate(next);
            Check(collections == 3);
        });
        test("sparse validation still rejects duplicate null and over-capacity directory changes", () =>
        {
            var validator = new ReplayTrackValidation<State>(s => s.Key, 3, s => { if (s.Invalid) throw new InvalidDataException(); });
            var a = new State("a"); var b = new State("b");
            validator.Validate(new[] { a, b });
            Reject(() => validator.Validate(new[] { a, new State("a") }));
            Reject(() => validator.Validate(new[] { a, null! }));
            Reject(() => validator.Validate(new[] { a, b, new State("c"), new State("d") }));
            validator.Validate(new[] { b, a });
            validator.Validate(new[] { a });
            Reject(() => validator.Validate(new[] { a, new State("b", true) }));
            validator.Validate(new[] { a, b });
        });
        test("incremental directory budgets preserve sparse changes reorder and reintroduction charges", () =>
        {
            var a = Creature("a"); var b = Creature("b"); var moved = Creature("b");
            var budget = new ContinuousReplayBudget();
            void Commit(CreatureReplayFrame[] values, long expectedNew)
            {
                var frame = new ReplayFrame { Creatures = values };
                long bytes = budget.Additional(frame);
                Check(bytes == RollingBuffer.EstimateFrame(frame) + expectedNew);
                budget.Commit(frame, bytes);
            }
            Commit(new[] { a, b }, CreatureReplayRules.Estimate(a) + CreatureReplayRules.Estimate(b));
            Commit(new[] { a, moved }, CreatureReplayRules.Estimate(moved));
            Commit(new[] { moved, a }, 0);
            Commit(new[] { a }, 0);
            Commit(new[] { a, moved }, CreatureReplayRules.Estimate(moved));
            Commit(new[] { a, moved }, 0);
            Commit(Array.Empty<CreatureReplayFrame>(), 0);
            Commit(new[] { moved, a }, CreatureReplayRules.Estimate(a) + CreatureReplayRules.Estimate(moved));
        });
    }
    private static CreatureReplayFrame Creature(string key) => new() { Key = key, Kind = "spider", Resource = "spider" };
    private static void Check(bool value) { if (!value) throw new Exception("Native capture directory assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid native capture directory accepted."); }
}
