# Peak and Nadir recording audit

Verified against the installed PEAK 2.4.c, Steam build 25306743. Evidence is the
local `Assembly-CSharp` decompilation under
`local/verification/runtime-wall-source`, plus a read-only Unity asset inspection
of `PEAK_Data/level5` (`Level_0`). These findings describe this build.

## Game identities and transitions

| Place | Biome enum | Segment enum | MapHandler array index |
| --- | --- | --- | --- |
| Final mountain stage: Kiln or Citadel | Volcano 3 or Swamp 8 | TheKiln 4 | 4 |
| Peak / 顶峰 | Peak 5 | Peak 5 for an explicit jump | 4, shared with the final stage |
| Nadir / 天底 | Void 17 | Void 6 | 5, appended from VoidBiome |

`Biome.cs:11,23` and `Segment.cs:8,9` define the enums.
`MapHandler.JumpToSegmentLogic` subtracts one from enum values 5 and above
(`MapHandler.cs:877-882`); Peak has its own teleport position at line 947.
Normal recording uses `GetCurrentSegment` (999-1005), so simply walking onto
Peak usually continues to record TheKiln 4. Peak is detected by the player's
Z position passing the final progress point, not by a new map layer
(`MountainProgressHandler.cs:198-204`).

The inspected scene has five serialized ordinary segments and a separate
VoidBiome component. Its final progress point is `PEAK`, biome 5. Nadir's
segment is biome 17. `InitializeMap` appends that segment whenever VoidBiome
exists, even before a player visits it (`MapHandler.cs:449-451,979-990`).
Available geometry therefore does not prove a visit.

Nadir entry activates VoidBiome, disables the ordinary mountain ancestor,
selects array index 5, changes the day/night profile, and warps players to its
spawn points in the **same scene** (`MapHandler.cs:870-882,937-960`). It does
not load another scene or end the run. Both places belong in the interface,
but they are not mutually exclusive daily biome alternatives.

The inspected `ScoutsHonor` item has `Action_WarpToShadowRealm` with
`segmentToWarpTo = 6`. Its base action calls `GoToVoidRoutine`
(`Action_WarpToBiome.cs:17-28`), which clears status and moves the party
(`MapHandler.cs:730-781`). A ScoutStatue's four filled amulet slots generate
ScoutsHonor (`Peak/ScoutStatue.cs:65-86,109-138`). Duplicate submitted amulet
types fill a missing type (`179-183`), so four different item types are not a
code requirement. Ascent 8 and above requires Nadir (`Ascents.cs:48`);
ordinary flare rescue checks that requirement (`Flare.cs:36`).

The Nadir portal ends the run (`Peak/PeakGatePortal.cs:76-82`). A surviving
character in Nadir qualifies for the win; the game selects the Nadir final
cutscene before `TriggerRunEnded` (`Character.cs:903-918,927-934`).
`wonViaNadir` distinguishes that ending (`CharacterStats.cs:349-352`). The
game then rewrites the last statistics point to biome Peak even for a Nadir
win (`337-343`), so that final point is not evidence of physically visiting
the summit.

## Replay correction

WorldFrame continues to store raw Segment enums, preserving existing schema
2-8 files and the current capture cost. Playback now resolves the array index
before setting `currentSegment` or choosing its day/night profile. Previously
Void 6 was rejected against the six-element array, and Peak 5 selected Nadir's
profile.

On entering Nadir, playback also sets the native `VoidBiome.isActive` flag and
hides the known mountain ancestor. It restores their initial values when
seeking back to an ordinary segment or Peak. The ancestor is resolved only
from the validated MapHandler, and both objects must belong to the replay
scene. This invokes no warp, item spawning, soul events or native transition
routine. The native spirit fog height updater needs the active flag
(`Peak/ForceFogShaderHeight.cs:18`). Static shader values and the day/night
profile use the corrected layer index.

ReplayTheatre owns an isolated scene and replaces it with Title on exit;
these scene-local switches cannot carry into the next live run. Backward
seeking restores the original ancestor and flag states without relying on
scene unloading. Header map paths and capture data are unchanged.

Validation: Release plugin build succeeds with zero warnings/errors. The
ReplayContract suite passes 396 checks, including enum mapping, absent Nadir,
schema 5-8 delta round trips, a clip starting in Nadir, repeated samples and
backward seeks. These are contract checks, not a live game visual test. No
plugin was installed during this audit.

## Remaining limits

- WorldFrame has no per-player summit-visit field. A precise visit indicator
  must use the native spatial progress check or a verified scene threshold;
  it cannot infer Peak from index 5 or from an available Nadir layer.
- Soul-release phase, rising hazards, moving fog and final cutscene state are
  not fully represented by these two presentation switches. Their exact
  timing and visual reproduction still require separate capture support and
  live verification. Selecting the right Nadir profile is not a claim that
  every Nadir environmental event is reproduced.
