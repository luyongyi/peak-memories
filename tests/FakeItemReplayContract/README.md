# Fake item replay contract

Run `dotnet run --project tests/FakeItemReplayContract/FakeItemReplayContract.csproj -c Release` from the repository root.

This executable links the production fake item capture/playback, interaction DTO rules, static capture directory, root lifecycle observer, bootstrap budget, and presentation budget. It checks static snapshot reuse, native visibility events, admission/retry bounds, independent backward presentation, scene-path lease aliases, inactive-source fallback, and cleanup.

Unity objects and `VisualReplica` construction use controlled adapters. Counters check whether production code queries or activates native sources; the replica adapter creates inert objects and consumes the real presentation budget. These checks do not run Unity, Harmony, networking, or the production mesh-copy implementation and do not replace native-game visual verification.
