# Native route audit — 0.8.27, 2026-10-13

This is a read-only native API audit for a test-only automated recorder route. No production source, installed plugin, game process, or save was changed by this audit.

Native source matches installed `Assembly-CSharp.dll` SHA-256 `97141B7FE9CDA6AC1DB3093D3579908DBFF78DB0B4343CB3E19429B486A46919`.

## Route design

Use a fresh protected offline **full** run. Keep the existing opt-in label/output isolation, save hash precondition, `Quicksave.SaveNow` / `DestroySaveData` blockers, blocked achievement writes, and restoration checks. `FinalizeRunSetup` may remain blocked for a new synthetic run as in the existing transition probe; native Continue requires that setup, so do not share that restriction blindly with resume tests.

For each selected native biome:

1. Wait for `GameHandler.IsOnIslandAndInitialized`, `MapHandler.ExistsAndInitialized`, local hip/refs, recorder readiness, and accepted frames. Record resolved native biome/root names and the active capture scope.
2. Observe a baseline at a safe native spawn for 30–60 seconds. Separate the first few bootstrap seconds from the steady interval. Record track counts, admissions/retirements, writer bytes, retained memory, capture method timings, and update counts by source.
3. Warp to a discovered active point using `Character.localCharacter.photonView.RPC("WarpPlayerRPC", RpcTarget.All, position, false)` or the local `WarpPlayerRPC(position, false)` in a guaranteed single-player offline test. Wait for `!character.warping` before interacting. Native warp resets ragdoll/velocity; assigning `transform.position` does not test the same route.
4. Stay near selected static items, crates, traps, or idle creatures for 20–30 seconds. Perform one normal native interaction, then wait 30–60 seconds without another interaction. Compare active sampling/update deltas with the last steady baseline. Omitted states alone are not proof of successful dormancy: also inspect track work diagnostics and final readback.
5. Warp to `MapHandler.CurrentCampfire` while it is still the current biome's ending campfire. Require `campfire.state == FireState.Off`, `campfire.IsInteractible(local)`, and `campfire.EveryoneInRange(campfire.moraleBoostRadius)`. Call `campfire.Interact(local)`, wait its native `GetInteractTime(local)` (currently `cookTime`), then `campfire.Interact_CastFinished(local)` once. The finish path sends `Light_Rpc` and triggers the normal map transition/effects/audio.
6. Confirm the old resolved biome root becomes inactive and the next resolved root active. `GoToSegment` increments its index before fog/map activation finishes, so index alone is insufficient. Allow another 6–10 seconds for fog reveal and enabling substeps before starting a steady interval; `WaitForReveal()` itself lasts 5 seconds and each `EnablingSubstep` adds 0.15 seconds. Do not repeatedly light or force another transition while it is in progress.
7. Confirm old biome capture sources retire and future biomes stay unadmitted. The previous campfire remains a valid active basecamp until the following transition, and carried items/global sources may be preserved. Do not assert that every prior campfire or preserved player-owned item must disappear.
8. Seal and read every archive page. Seek backward/forward across transitions and around action/effect timestamps, then allow playback to advance. Report actual observed coverage; teleporting through biomes does not exercise every creature state or every item action.

## Exact native calls and traps

`Campfire.Interact_CastFinished(Character)` checks `EveryoneInRange` and sends `view.RPC("Light_Rpc", RpcTarget.All, true, 0f)` when unlit. `Light_Rpc` updates lit state, shader/particles, calls `MapHandler.GoToSegment(advanceToSegment)`, and calls `Quicksave.SaveNow()`. Existing `DebugLight()` sends the same lighting RPC but bypasses range/hold, so the normal interaction is a stronger acceptance test. `LightWithoutReveal()` passes `updateSegment=false` and does not test biome progression.

`MapHandler.GoToSegment(Segment s)` only advances `currentSegment` by **one**. Passing `Caldera` while on Beach is not a far skip; it also changes special-case behavior. Sequence the current campfire's exact `advanceToSegment` instead.

`MapHandler.JumpToSegment(Segment)` is the native debug warp and is usable for a separately labeled spot-check. It activates roots, updates fog, spawns native items, and warps players. It omits parts of the campfire progression/old-item retirement coroutine; therefore do not use it as the only test of progression or compare repeated debug jumps to ordinary steady-state capture growth.

For a normal box use `Luggage.Interact(local)`, wait `GetInteractTime(local)`, then `Interact_CastFinished(local)`. The finish path sends `OpenLuggageRPC` and invokes `GlobalEvents.TriggerLuggageOpened`. `OpenImmediatelyNoNotify` omits the global open event and is not a full interaction test. Exclude `RespawnChest`, `MirageLuggage`, trap subclasses, and already open boxes from a generic harmless-crate scenario unless specifically testing those variants.

For a static ground pickup use `Item.IsInteractible(local)` and `Item.Interact(local)` after native proximity/slot readiness. The native pickup first hides the old object and then destroys/replaces it with inventory state, so expect identity transition rather than a permanently moving old object. Inventory needs a free slot and a 0.25-second equip cooldown. Keep the drop/use scenario separate and route it through native item/character methods.

## Biome selection and limits

`Segment` values are Beach 0, Tropics 1, Alpine 2, Caldera 3, TheKiln 4, Peak 5, Void 6. Native `Biome.BiomeType` includes Roots 7 (森蕈) and Swamp 8 (雾沼). Segment name is not an exact biome label.

`MapHandler.MapSegment.biome`, `.segmentParent`, `.segmentCampfire`, and `.dayNightProfile` resolve `hasVariant` against `MapHandler.biomes`. Use these resolved properties; do not hardcode hierarchy names or infer the variant from the segment enum. Log selected `map.biomes` and each resolved segment.

Choose an appropriate baked scene **before loading** when testing Roots/Swamp variants. `SingletonAsset<MapBaker>.Instance.selectedBiomes[i].biomeTypes` exposes each baked selection; `MapBaker.GetLevel(i)` supplies the corresponding `Level_n` scene. Bound the index against both collections. `Level_0` exercises only its baked biome selection. Avoid mutating `BiomeSelector`, `map.biomes`, or native roots at runtime because that changes map generation/ownership outside the ordinary flow.

TheKiln, Peak, and Void require dedicated final/special scenarios. `Campfire.Light_Rpc` can end a mini run, and terminal segments need not expose another valid current campfire. Stop the ordinary full-run campfire loop at a missing campfire/invalid next segment and report it explicitly. Do not call `CurrentCampfire` without checking the relevant `segmentCampfire` root, because that property dereferences the root.

## Measurement interpretation

Native active sources and capture sources must both be reported. Idle native creatures can still animate without recorder high-frequency sampling. Report serialized growth by bounded phase, ideally from finalized page bytes allocated to their timestamps rather than only `FileInfo.Length`: the writer batches and may keep unflushed pages. Capture durations measured with a Harmony prefix/finalizer include native calls and the diagnostic itself. One-second reflection/JSON snapshots add test overhead; do not label whole-process FPS or working-set growth as recorder-only CPU/memory.

Report event inventories as covered, absent in this generated map, attempted-but-not-triggered, or failed. A full all-page decode plus healthy per-track counts does not prove every gameplay action was captured. Cross-region replay should additionally verify visible actions/effects and actor advancement, not only no exceptions.
