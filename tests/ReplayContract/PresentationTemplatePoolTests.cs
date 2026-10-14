using PeakReplayLab;

internal static class PresentationTemplatePoolTests
{
    private sealed class Model
    {
        public string Key = "";
        public int Cost;
    }
    public static void Run(Action<string, Action> test)
    {
        test("template pool preserves exact identities before cross lifetime reuse", () =>
        {
            var retired = new List<Model>(); var pool = new PresentationTemplatePool<Model>(4, 100, retired.Add);
            var old = new Model { Key = "old", Cost = 10 }; var exact = new Model { Key = "requested", Cost = 10 };
            pool.Return("frog-body", exact, exact.Cost); pool.Return("frog-body", old, old.Cost);
            Check(ReferenceEquals(pool.Take("frog-body", value => value.Key == "requested"), exact));
            Check(ReferenceEquals(pool.Take("frog-body"), old)); Check(pool.Count == 0 && pool.Cost == 0 && retired.Count == 0);
        });
        test("template pool reserves identities requested later in the same cursor", () =>
        {
            var pool = new PresentationTemplatePool<Model>(4, 100, _ => { });
            var reserved = new Model { Key = "later", Cost = 10 }; var reusable = new Model { Key = "gone", Cost = 10 };
            pool.Return("item", reusable, 10); pool.Return("item", reserved, 10);
            Check(ReferenceEquals(pool.Take("item", value => value.Key == "new", value => value.Key != "later"), reusable));
            Check(pool.Take("different-template") == null);
            Check(ReferenceEquals(pool.Take("item", value => value.Key == "later", _ => false), reserved));
        });
        test("template pool bounds both large skeleton cost and object count", () =>
        {
            var retired = new List<Model>(); var pool = new PresentationTemplatePool<Model>(2, 100, retired.Add);
            var a = new Model { Key = "a" }; var b = new Model { Key = "b" }; var c = new Model { Key = "c" }; var giant = new Model { Key = "giant" };
            pool.Return("mesh", a, 60); pool.Return("mesh", b, 60);
            Check(retired.SequenceEqual(new[] { a }) && pool.Cost == 60);
            pool.Return("mesh", giant, 101); Check(retired.SequenceEqual(new[] { a, giant }) && pool.Count == 1);
            pool.Return("mesh", c, 10); pool.Return("mesh", new Model { Key = "d" }, 10);
            Check(retired.SequenceEqual(new[] { a, giant, b }) && pool.Count == 2 && pool.Cost == 20);
        });
        test("repeated distant cursor churn keeps template retention at a plateau", () =>
        {
            var retired = new HashSet<Model>(); var pool = new PresentationTemplatePool<Model>(16, 200, value => Check(retired.Add(value)));
            var live = new List<(string Template, Model Value)>();
            for (int seek = 0; seek < 2000; seek++)
            {
                foreach (var model in live) pool.Return(model.Template, model.Value, model.Value.Cost);
                live.Clear();
                for (int i = 0; i < 12; i++)
                {
                    string template = "prefab-" + ((seek + i) % 9);
                    var model = pool.Take(template) ?? new Model { Cost = 5 + (seek + i) % 20 };
                    Check(!retired.Contains(model)); model.Key = seek + ":" + i; live.Add((template, model));
                }
                Check(pool.Count <= 16 && pool.Cost <= 200);
            }
            foreach (var model in live) pool.Return(model.Template, model.Value, model.Value.Cost);
            pool.Clear(); Check(pool.Count == 0 && pool.Cost == 0);
        });
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Template retention policy failed."); }
}
