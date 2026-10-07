# ReplayView ownership and native UI copy contracts

Run without Unity or game binaries:

```powershell
dotnet run --project PeakReplayLab/tests/ReplayViewContract/ReplayViewContract.csproj -c Release
```

This executable links the production `ReplayView.cs` and `NativeUiComponentCopy.cs`
directly. `UnityDoubles.cs`
contains deliberately limited in-memory Unity/game API substitutes; no game assets,
Unity runtime assemblies, rendering pipeline, or copied game implementation are used.

Covered contracts:

- Borrow the existing native Camera, its main-camera references, URP component,
  camera stack, and render target. Leave other cameras and environment volumes alone.
- Suspend local camera movement and Eye Blink controllers, open `_EyeOpen`, and
  disable only the named global Eye Blink renderer feature. Reapply those invariants
  when `Enforce()` runs; include inactive local children and deduplicate shared resources.
- Suspend PassOutPost/FallPost controllers and their own volumes, not all volumes.
- Restore transform, culling mask, near clip, field of view, enabled states, material
  values, and renderer-feature state, including values originally disabled.
- Roll back a constructor failure after movement suspension; tolerate repeated
  Dispose, absent optional resources, destroyed objects, and independent restore errors.
- Copy native CanvasGroup fields directly, including transparent/disabled state,
  without entering JsonUtility. The serializer double reproduces the installed
  Unity player's managed rejection of native engine components; allowed script
  components still serialize, and mismatched/destroyed/unsupported components fail.

These tests **do not validate actual Unity/URP pixels**, game-specific lifecycle
ordering, actual EyeBlinkController behavior, shader compatibility, scene-loading
completion, camera occlusion, avatar rendering, or the in-game notification layout.
Those require a real PEAK runtime test. The simulated Eye Blink disable side effects
exist solely to check that ReplayView snapshots original state before suspension.
