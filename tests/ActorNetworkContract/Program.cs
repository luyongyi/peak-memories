using PeakReplayLab;

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Remote model cadence assertion failed."); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error); Environment.Exit(1); }
}

Test("native rates retain their requested cadence under a 60 Hz recording clock", () =>
{
    foreach (int rate in new[] { 1, 5, 10, 20, 24, 30, 60 })
    {
        var policy = new ActorNetworkSamplingPolicy(); int count = 0;
        for (int frame = 0; frame <= 3600; frame++)
            if (policy.ShouldCapture(frame / 60d, rate)) count++;
        Check(count == rate * 60 + 1);
    }
});
Test("network rates above recording rate never invent extra real frames", () =>
{
    foreach (int rate in new[] { 61, 120, 1000, int.MaxValue })
    {
        var policy = new ActorNetworkSamplingPolicy(); int count = 0;
        for (int frame = 0; frame <= 600; frame++)
            if (policy.ShouldCapture(frame / 60d, rate)) count++;
        Check(count == 601);
    }
});
Test("phase remains stable at 58 rendered fps and long absolute game times", () =>
{
    foreach (int rate in new[] { 10, 20, 24, 30 })
        foreach (double origin in new[] { 0d, 1048576d })
        {
            var policy = new ActorNetworkSamplingPolicy(); int count = 0;
            for (int frame = 0; frame <= 3480; frame++)
                if (policy.ShouldCapture(origin + frame / 58d, rate)) count++;
            Check(count == rate * 60 + 1);
        }
});
Test("jittered global samples retain the native rate without last-plus-interval drift", () =>
{
    var policy = new ActorNetworkSamplingPolicy(); int count = 0;
    for (int frame = 0; frame <= 3600; frame++)
    {
        double time = frame / 60d + (frame == 0 || frame == 3600 ? 0 : (frame % 3 - 1) * .0015);
        if (policy.ShouldCapture(time, 20)) count++;
    }
    Check(count == 1201);
});
Test("stalls yield one actual snapshot with no catch-up backlog", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(0, 20)); Check(policy.ShouldCapture(10, 20));
    Check(!policy.ShouldCapture(10.001, 20)); Check(!policy.ShouldCapture(10.02, 20));
    Check(policy.ShouldCapture(10.05, 20));
});
Test("paused and duplicate timestamps do not generate refreshed poses", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(5, 20));
    for (int i = 0; i < 100; i++) Check(!policy.ShouldCapture(5, 20, boundary: true));
    Check(!policy.ShouldCapture(5.01, 20)); Check(policy.ShouldCapture(5.05, 20));
});
Test("discrete state wakes once and does not create a high-frequency tail", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(0, 20)); Check(policy.ShouldCapture(.016, 20, boundary: true));
    Check(!policy.ShouldCapture(.032, 20)); Check(!policy.ShouldCapture(.048, 20));
    Check(policy.ShouldCapture(.064, 20)); Check(!policy.ShouldCapture(.080, 20));
    Check(policy.ShouldCapture(.112, 20));
});
Test("native rate changes establish one fresh boundary without carrying old debt", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(0, 20)); Check(!policy.ShouldCapture(.01, 20));
    Check(policy.ShouldCapture(.02, 10)); Check(!policy.ShouldCapture(.07, 10));
    Check(policy.ShouldCapture(.12, 10)); Check(policy.ShouldCapture(.13, 30));
    Check(!policy.ShouldCapture(.14, 30)); Check(policy.ShouldCapture(.17, 30));
});
Test("no packet prerequisite can freeze ongoing native animation", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(0, 20)); Check(policy.ShouldCapture(.05, 20));
    Check(policy.ShouldCapture(.1, 20)); Check(policy.ShouldCapture(.15, 20));
});
Test("explicit reset and backwards scene clock establish fresh snapshots", () =>
{
    var policy = new ActorNetworkSamplingPolicy();
    Check(policy.ShouldCapture(20, 20)); Check(!policy.ShouldCapture(20.01, 20));
    policy.Reset(); Check(policy.ShouldCapture(20.01, 20));
    Check(policy.ShouldCapture(1, 20)); Check(!policy.ShouldCapture(1.01, 20));
    Check(policy.ShouldCapture(1.05, 20));
});
Test("invalid native rate safely follows rendered recording capacity", () =>
{
    foreach (int rate in new[] { 0, -20, int.MinValue })
    {
        var policy = new ActorNetworkSamplingPolicy(); int count = 0;
        for (int frame = 0; frame <= 60; frame++)
            if (policy.ShouldCapture(frame / 60d, rate)) count++;
        Check(count == 61);
    }
});
Test("invalid recording times are rejected before changing cadence state", () =>
{
    var policy = new ActorNetworkSamplingPolicy(); Check(policy.ShouldCapture(1, 20));
    foreach (double time in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        bool threw = false;
        try { policy.ShouldCapture(time, 20); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw);
    }
    Check(!policy.ShouldCapture(1.01, 20)); Check(policy.ShouldCapture(1.05, 20));
});
Test("actual capture retains local core bones on every global recording tick", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 20;
    var character = new Character(local: true); var capture = new ActorJointCapture();
    NodePose[]? previous = null;
    for (int frame = 0; frame <= 60; frame++)
    {
        double time = frame / 60d; UnityEngine.Time.unscaledTimeAsDouble = time;
        var pose = capture.Sample(character, default, time);
        Check(!ReferenceEquals(previous, pose) && pose.All(node => node.SampleTime == time));
        previous = pose;
    }
});
Test("actual remote capture holds the same immutable pose without bone reads between network ticks", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 20; UnityEngine.Time.unscaledTimeAsDouble = 0;
    var character = new Character(local: false); var capture = new ActorJointCapture();
    var first = capture.Sample(character, default, 0);
    int poseReads = UnityEngine.Transform.PoseReads, catalogueReads = character.CatalogueReads;
    character.refs.hip.transform.localPosition = new UnityEngine.Vector3(1, 0, 0);
    var held = capture.Sample(character, default, 1 / 60d);
    Check(ReferenceEquals(first, held) && held.All(node => node.SampleTime == 0));
    Check(UnityEngine.Transform.PoseReads == poseReads && character.CatalogueReads == catalogueReads);
    var next = capture.Sample(character, default, .05);
    Check(!ReferenceEquals(first, next) && next.All(node => node.SampleTime == .05));
    Check(first.Single(node => node.Path.Contains("Hip")).Position[0] == 0);
    Check(next.Single(node => node.Path.Contains("Hip")).Position[0] == 1);
});
Test("actual remote capture records a full second at native cadence and no packet notification", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 20; UnityEngine.Time.unscaledTimeAsDouble = 0;
    var character = new Character(local: false); var capture = new ActorJointCapture();
    NodePose[]? previous = null; int count = 0;
    for (int frame = 0; frame <= 60; frame++)
    {
        double time = frame / 60d; UnityEngine.Time.unscaledTimeAsDouble = time;
        var pose = capture.Sample(character, default, time);
        if (!ReferenceEquals(previous, pose)) count++;
        previous = pose;
    }
    Check(count == 21);
});
Test("actual state and warp boundaries refresh once then resume native cadence", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 20; UnityEngine.Time.unscaledTimeAsDouble = 0;
    var character = new Character(local: false); var capture = new ActorJointCapture();
    var first = capture.Sample(character, default, 0);
    character.data.dead = true;
    var changed = capture.Sample(character, default, 1 / 60d);
    Check(!ReferenceEquals(first, changed));
    Check(ReferenceEquals(changed, capture.Sample(character, default, 2 / 60d)));
    character.warping = true;
    var warping = capture.Sample(character, default, .04);
    Check(!ReferenceEquals(changed, warping));
    Check(ReferenceEquals(warping, capture.Sample(character, default, .045)));
    character.warping = false;
    Check(!ReferenceEquals(warping, capture.Sample(character, default, .046)));
});
Test("ownership changes restore local full-rate capture and remote cadence", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 20; UnityEngine.Time.unscaledTimeAsDouble = 0;
    var character = new Character(local: false); var capture = new ActorJointCapture();
    var first = capture.Sample(character, default, 0);
    Check(ReferenceEquals(first, capture.Sample(character, default, .01)));
    character.IsLocal = true;
    var local = capture.Sample(character, default, .02);
    Check(!ReferenceEquals(first, local));
    var localNext = capture.Sample(character, default, .03);
    Check(!ReferenceEquals(local, localNext));
    character.IsLocal = false;
    var remote = capture.Sample(character, default, .04);
    Check(!ReferenceEquals(localNext, remote));
    Check(ReferenceEquals(remote, capture.Sample(character, default, .05)));
    Check(!ReferenceEquals(remote, capture.Sample(character, default, .09)));
});
Test("normal displacement cannot renew low-rate capture and true teleport wakes immediately", () =>
{
    Photon.Pun.PhotonNetwork.SerializationRate = 1; UnityEngine.Time.unscaledTimeAsDouble = 0;
    var character = new Character(local: false); var capture = new ActorJointCapture();
    var first = capture.Sample(character, default, 0);
    Check(ReferenceEquals(first, capture.Sample(character, new UnityEngine.Vector3(3, 0, 0), .3)));
    var teleported = capture.Sample(character, new UnityEngine.Vector3(30, 0, 0), .4);
    Check(!ReferenceEquals(first, teleported));
    Check(ReferenceEquals(teleported, capture.Sample(character, new UnityEngine.Vector3(33, 0, 0), .7)));
    Check(!ReferenceEquals(teleported, capture.Sample(character, new UnityEngine.Vector3(34, 0, 0), 1)));
});
Console.WriteLine($"TOTAL: {passed} remote model cadence checks passed. Unity animation is not simulated.");
