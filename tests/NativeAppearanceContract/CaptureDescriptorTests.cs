using PeakReplayLab;

internal static class CaptureDescriptorTests
{
    private sealed class MutableMaterial
    {
        internal string Shader = "NativeItem";
        internal int Queue = 2000, ShaderReads, QueueReads;
        internal bool Throw;
    }
    private readonly record struct Descriptor(string Shader, int Queue);
    private static readonly Func<MutableMaterial, string> shaderReader = source =>
    { source.ShaderReads++; return source.Shader; };
    private static readonly Func<MutableMaterial, Descriptor> descriptorReader = source =>
    {
        source.QueueReads++;
        if (source.Throw) throw new InvalidOperationException("Native read unavailable");
        return new Descriptor(source.Shader, source.Queue);
    };

    internal static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("shared native material descriptor reads once for all observers in one capture", () =>
        {
            var source = new MutableMaterial();
            var shader = new CaptureEpochValue<string>();
            var descriptor = new CaptureEpochValue<Descriptor>();
            for (int owner = 0; owner < 16; owner++)
            {
                check(shader.Read(source, 1, shaderReader) == "NativeItem");
                check(descriptor.Read(source, 1, descriptorReader) == new Descriptor("NativeItem", 2000));
            }
            check(source.ShaderReads == 1 && source.QueueReads == 1);
        });
        test("next capture observes native shader and render queue mutations", () =>
        {
            var source = new MutableMaterial();
            var shader = new CaptureEpochValue<string>();
            var descriptor = new CaptureEpochValue<Descriptor>();
            string beforeShader = shader.Read(source, 7, shaderReader);
            var before = descriptor.Read(source, 7, descriptorReader);
            source.Shader = "NativeLitVariant"; source.Queue = 3010;
            check(shader.Read(source, 8, shaderReader) == "NativeLitVariant");
            check(descriptor.Read(source, 8, descriptorReader) == new Descriptor("NativeLitVariant", 3010));
            check(beforeShader == "NativeItem" && before == new Descriptor("NativeItem", 2000));
            check(source.ShaderReads == 2 && source.QueueReads == 2);
        });
        test("animated shader reads leave unused full descriptor getters cold", () =>
        {
            var source = new MutableMaterial();
            var shader = new CaptureEpochValue<string>();
            var descriptor = new CaptureEpochValue<Descriptor>();
            shader.Read(source, 1, shaderReader);
            shader.Read(source, 2, shaderReader);
            check(source.ShaderReads == 2 && source.QueueReads == 0);
            descriptor.Read(source, 2, descriptorReader);
            check(source.ShaderReads == 2 && source.QueueReads == 1);
        });
        test("native descriptor failures do not cache a stale value in the current capture", () =>
        {
            var source = new MutableMaterial();
            var descriptor = new CaptureEpochValue<Descriptor>();
            descriptor.Read(source, 1, descriptorReader);
            source.Throw = true;
            try { descriptor.Read(source, 2, descriptorReader); throw new Exception("Expected native read failure"); }
            catch (InvalidOperationException) { }
            source.Throw = false; source.Queue = 3001;
            check(descriptor.Read(source, 2, descriptorReader).Queue == 3001);
            check(source.QueueReads == 3);
        });
    }
}
