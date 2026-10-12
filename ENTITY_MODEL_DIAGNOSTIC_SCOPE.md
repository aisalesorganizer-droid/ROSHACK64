# ROS64Hack — Entity → ModelSkeletal → SpaceNode Evidence Diagnostic

This diagnostic is deliberately narrower than the previous BGA `+0xB8` deep scanners.

## The exact proof target

```text
LIVE gameplay/native Entity
    +0x18  identity / entity ID
    +0x1C  X
    +0x20  Y
    +0x24  Z
    +0x5E  selfControlled
    +0xD8  Entity.model backing reference
    +0xE0  native model-array begin
    +0xE8  native model-array end
        |
        +---- exact ModelSkeletal pointer (vtable 0x141C1ACE0)
                   |
                   +0x38 -> SpaceNode (vtable 0x141C137C8)
                                   |
                                   +0x40 == ModelSkeletal
                                   +0x48..+0x87 = 64-byte transform
```

## What makes this diagnostic different

It does not interpret arbitrary pointer runs as bones and does not walk `BGA.+0xB8` as a guessed skeleton container.

It starts from exact class anchors already proven in the executable:

- `ModelSkeletal` vtable `0x141C1ACE0`
- `SpaceNode` vtable `0x141C137C8`
- native `Entity` field layout `+0x18/+0x1C/+0x20/+0x24/+0x5E/+0xD8/+0xE0/+0xE8`

It finds current `ModelSkeletal` instances by the exact vtable, validates their `+0x38 → SpaceNode` and reciprocal `SpaceNode+0x40 → ModelSkeletal` relationship, then reverse-scans readable private memory for references to those exact model addresses.

Every reference is tested against the native Entity layout in two ways:

1. `reference - 0xD8` — direct `Entity.model` field candidate.
2. `reference - 0xE0 - index*8` — native `models[]` entry candidate.

A candidate is accepted only when its ID/XYZ are structurally valid and its `+0xD8` or `+0xE0/+0xE8` model storage is coherent.

The diagnostic then correlates the resulting native Entity ID/XYZ against the already-working `avatarInAoI` `BattleGroundAvatar` snapshot. This explicitly avoids treating the Python BGA address as the native Entity address.

## Evidence captured

The diagnostic records:

- current EM pointer and both relevant EM roots (`+0x10F8`, `+0x10E0`) without assuming their semantics beyond the evidence;
- the current local `PlayerBattleGroundAvatar` and several AOI `BattleGroundAvatar` IDs/XYZ values;
- selected Python instance-dictionary semantic hits (`entity`, `model`, `_model`, `gameObject`, `transform`, `spaceID`);
- all exact `ModelSkeletal` objects found within the private readable scan scope, after validating the `+0x38 ↔ +0x40` relationship;
- each validated model's `SpaceNode` transform block;
- exact reverse references from model pointers to candidate native Entity objects;
- native Entity raw qwords around the accepted candidate;
- Entity→Model→SpaceNode XYZ distances;
- Entity-ID / BGA-ID correlation;
- `selfControlled` state;
- 12 continuity samples so pointer/ID/model/node/position relationships are shown across time.

The expensive heap work runs on a separate diagnostic timer and uses quiet `ReadProcessMemory` calls, so the existing `RuntimeDiagnostics` counters do not include the evidence scan.

## Result interpretation

The strongest successful result is:

```text
native Entity found
+ native Entity ID matches an AOI BGA ID
+ native Entity XYZ matches that BGA XYZ
+ native Entity models[] contains exact ModelSkeletal
+ ModelSkeletal +0x38 == SpaceNode
+ SpaceNode +0x40 == same ModelSkeletal
+ SpaceNode transform XYZ matches native Entity XYZ
```

That closes the exact bridge required for the codebase fix.

A negative result is reported with the exact scan bytes, region count, and truncation state. It must not be interpreted as proof that the relationship does not exist outside the captured scan scope.
