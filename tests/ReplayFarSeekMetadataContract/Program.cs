using System.Security.Cryptography;
using Newtonsoft.Json;
using PeakReplayLab;

// Read-only native-test metadata utility; recordings remain machine-local.
if (args.Length != 1)
{
    Console.WriteLine("Usage: ReplayFarSeekMetadataContract <recording.peakrun>");
    return args.Length == 0 ? 0 : 2;
}
string path = Path.GetFullPath(args[0]);
var file = new FileInfo(path);
var info = FullReplayArchive.ReadInfo(path);
using var input = File.OpenRead(path);
string sha = Convert.ToHexString(SHA256.HashData(input));
Console.WriteLine(JsonConvert.SerializeObject(new
{
    Path = path, Length = file.Length, Sha256 = sha, info.Header.Schema, info.Header.Scene,
    info.Duration, Pages = info.Pages.Length, info.FrameCount,
    Targets = new[] { .9, .05, .85, .15, .95, .25 }
        .Select(fraction => Math.Clamp(info.Duration * fraction, 0, Math.Max(0, info.Duration - 2))).ToArray()
}, Formatting.Indented));
return 0;
