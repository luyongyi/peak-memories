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
  "RequireCacheSkip": true,
  "RunLargeDuplicate": true,
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

The rounds are recent export/upload, the same recent recording selected again, optional large export canceled while its actual worker is still running, and large export/upload restarted after that worker has stopped. `RequireCacheSkip` requires the repeated selection to finish with an authenticated local receipt, zero calls to the real exporter and zero calls to the uploader. `RunLargeDuplicate` repeats the large recording with the same requirement. Leave these two flags false when measuring an old DLL. Initial uploads may legitimately return duplicates when the local receiver already contains the same immutable package. The actual route UI is shown and remains visible for `ResultHoldSeconds` after each result, allowing independent screenshot inspection.

A scoped Harmony prefix changes the production export/cache directory to this fresh session and measures actual exporter invocations with `Stopwatch`; its finalizer records completion or cancellation. All decoding, serialization, compression, HTTP validation, cache decisions and receipt writing stay in the installed production code. Uploads are restricted to the one explicitly supplied numeric loopback URL and packages under this session. The normal `TrajectoryExports` and its cache are not used. Package inspection runs in a separate background task after the measured export and computes decompressed wire JSON SHA-256, expanded point/event counts and part sizes without writing player identities into metrics. Comparing v1 and v2 requires canonical `validateTrajectory(...).trajectory` hashes, since their wire JSON intentionally differs.

`test-server.mjs ABSOLUTE_WEB_REPO NEW_PRIVATE_DIRECTORY PORT [ABSOLUTE_CATALOG]` imports the real Web receiver, binds only `127.0.0.1`, and records upload request counts/statuses without names or coordinates. Use its `http-summary.json` to independently verify that cached rounds did not issue a POST. The real input recordings and receiver store remain in ignored private directories; they must not be uploaded to production for acceptance testing.

The metrics distinguish background export time, request-to-production-menu-ready time and upload acknowledgement time. Per-round main-thread `LateUpdate` wall intervals include presentation, scheduling and companion overhead; they are **not exclusive main-thread CPU durations**. p95/max and counts over 50/100 ms are reported, with a 10-second native library baseline by default. Each frame meter retains at most 180,000 intervals; the report explicitly states retained/total sample counts.

Before launch, the launcher backs up the actual native quicksave and every `.cfg` recursively, preserving absence as a valid original save state. The companion blocks native save/destruction/finalization, achievement writes and all `ConfigFile.Save` calls **before** changing the endpoint in memory. It restores the original endpoint on completion and leaves write protections active throughout application shutdown. The hidden watchdog verifies save/config hashes after process exit, permits exact restoration only of a release config's comment-only version header, copies the BepInEx log, and removes only the exact companion DLL with its expected hash. Unexpected configuration changes or a forced exit are reported as failures in private evidence; no blanket restore hides changed settings.

Results appear as `summary.json`, one JSON report per round, `phase.json`, a heartbeat updated every five seconds, and a post-exit `disk-protection-proof.json`. A success summary alone is insufficient without the disk protection proof and independent package/hash comparison. No packages are sent to production by this tool.
