# Real-recording trajectory export comparison

Runs the supplied built plugin's public exporter on a sealed local `.peakrun` in a fresh .NET 8 process. It does not launch Unity, upload a route, modify the recording or call game APIs. Each run requires a new output directory; private route packages and metrics stay there.

```powershell
dotnet build tools/TrajectoryBenchmark/TrajectoryBenchmark.csproj -c Release
dotnet artifacts/bin/TrajectoryBenchmark/release/TrajectoryBenchmark.dll PLUGIN_DLL MANAGED_DIRECTORY RECORDING NEW_OUTPUT LABEL
```

Compare at least both versions on identical source files and alternate their order for repeated runs. `Packages[].JsonSHA256` hashes uncompressed output, covering every coordinate and event, including package metadata. The timer covers the public Export call, including its own index validation, compression and writes, but excludes subsequent output hashing. The harness reads metadata before timing; this is not a cold-start measurement. Report timing, cumulative allocations and GC separately from output equality. Working set is a process peak and includes startup; file caches are uncontrolled. These measurements execute the actual DLL but use .NET 8, not the game's Mono runtime, so they are not a game FPS or main-thread measurement. A canceled/failed export has no successful metrics report. Never commit real recordings or exported player data.
