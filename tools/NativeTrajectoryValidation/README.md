# Protected native trajectory acceptance

This companion tests the installed release DLL inside graphical PEAK / Unity Mono. It does not replace or modify the release plugin. It is disabled unless launched with the complete explicit audit arguments. Private launch plans, logs, packages and backups belong under the repository's ignored `local/` tree.

Build with the local game references:

```powershell
dotnet build tools/NativeTrajectoryValidation/driver/TrajectoryAuditProbe.csproj -c Release
```

Create a private JSON plan with two **existing, sealed, playable full recordings from the game's actual recording library**:

```json
{
  "RecentPath": "C:/absolute/game/BepInEx/PeakReplayLab/Recordings/recent.peakrun",
  "LargePath": "C:/absolute/game/BepInEx/PeakReplayLab/Recordings/large.peakrun",
  "RunCancellation": true,
  "CancelAfterSeconds": 2,
  "BaselineSeconds": 10,
  "ResultHoldSeconds": 15,
  "MaximumSeconds": 1500
}
```

Start the real Web receiver separately on an explicit `127.0.0.1` port with private temporary storage. Close PEAK, then launch:

```powershell
pwsh -NoProfile -File tools/NativeTrajectoryValidation/launch-trajectory.ps1 `
  -Label native-v35-a -Plan C:/absolute/private/plan.json `
  -Endpoint http://127.0.0.1:53378/api/route-uploads `
  -OutputRoot C:/absolute/private/native-trajectory/sessions
```

Use a new label for every run. `MaximumSeconds` must be 1200–2400, cancellation delay 0.5–30 seconds, baseline 5–30 seconds and result hold 0–30 seconds. For an older DLL timing comparison, set `RunCancellation` to false and use a new plan/label. The launcher records the actual installed release DLL hash; installing a baseline and restoring the release DLL remain the caller's responsibility.

The companion waits for the native `Title` and `MainMenuMainPage`, invokes the real library opener and collection chooser, waits for actual catalog tasks, and requires the requested recordings' original playable cards. It calls production `RequestRouteExport`, lets production `UpdateRouteUploads` consume its task, calls guarded `ConfirmRouteUpload`, and requires the production acknowledgement. It does not manufacture cards, relax guards, mutate recording metadata, enter a gameplay scene or start capture.

The rounds are recent export/upload, the same recent recording exported/uploaded again (duplicate receipt required), optional large export canceled while its actual worker is still running, and large export/upload restarted after that worker has stopped. Initial uploads may legitimately return duplicates when the local receiver already contains the same immutable package. The actual route UI is shown and remains visible for `ResultHoldSeconds` after each result, allowing independent screenshot inspection.

A scoped Harmony prefix changes only the production export's output directory to this fresh session and measures its background invocation with `Stopwatch`; its finalizer records completion or cancellation. All decoding, serialization, compression, HTTP validation and receipt writing stay in the installed production code. Uploads are restricted to the one explicitly supplied numeric loopback URL and packages under this session. The normal `TrajectoryExports` directory is not used. Package inspection runs in a separate background task after the measured export and computes complete decompressed JSON SHA-256, point/event counts and part sizes without writing player identities into metrics.

The metrics distinguish background export time, request-to-production-menu-ready time and upload acknowledgement time. Per-round main-thread `LateUpdate` wall intervals include presentation, scheduling and companion overhead; they are **not exclusive main-thread CPU durations**. p95/max and counts over 50/100 ms are reported, with a 10-second native library baseline by default. Each frame meter retains at most 180,000 intervals; the report explicitly states retained/total sample counts.

Before launch, the launcher backs up the actual native quicksave and every `.cfg` recursively, preserving absence as a valid original save state. The companion blocks native save/destruction/finalization, achievement writes and all `ConfigFile.Save` calls **before** changing the endpoint in memory. It restores the original endpoint on completion and leaves write protections active throughout application shutdown. The hidden watchdog verifies save/config hashes after process exit, permits exact restoration only of a release config's comment-only version header, copies the BepInEx log, and removes only the exact companion DLL with its expected hash. Unexpected configuration changes or a forced exit are reported as failures in private evidence; no blanket restore hides changed settings.

Results appear as `summary.json`, one JSON report per round, `phase.json`, a heartbeat updated every five seconds, and a post-exit `disk-protection-proof.json`. A success summary alone is insufficient without the disk protection proof and independent package/hash comparison. No packages are sent to production by this tool.
