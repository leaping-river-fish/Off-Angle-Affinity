# Player Scale Reference

This document defines the project's baseline world scale and player metrics.
These values are the foundation for all future movement, weapons, level
design, and gameplay systems — treat them as the source of truth, and update
this file if any of them are intentionally changed.

## World Scale

- **1 Unity unit = 1 meter.**

## Player Metrics

| Metric | Value |
|---|---|
| Player height | 1.8 m |
| Camera eye height | 1.68 m |
| Walk speed | 4.5 m/s |
| Sprint speed | 7.0 m/s |
| Standing jump height | 0.9 m |

## Player Prefab Conventions

`Assets/_Project/Prefabs/Player/Player.prefab`

- The **root transform is the player's feet / ground contact point** (`y = 0`
  on the root means standing on the ground). This is why the
  `CharacterController` center is offset upward by half its height rather than
  left at `(0, 0, 0)`.
- `CharacterController` (on the `Player` root):
  - Height: `1.8`
  - Radius: `0.5`
  - Center: `(0, 0.9, 0)`
- Visual/debug `Capsule` child: scaled to `(1, 0.9, 1)` and repositioned to
  `(0, 0.9, 0)` so its mesh and `CapsuleCollider` visually match the
  `CharacterController` above. This is a placeholder mesh — a real character
  model can replace it without any of the above conventions changing.
- `Camera` child local position: `(0, 1.68, 0)` — the eye height.
- Spawn points (e.g. `Spawnpoint_01` in `Test Scene.unity`) should be placed
  with their `y` at the ground surface height, since the player root now
  represents the feet position.

## Movement Settings

Configured on `PlayerController._movementSettings`
(`Assets/_Project/Scripts/Movement/MovementStateContext.cs`), exposed in the
Inspector — no code changes are needed to retune these:

- `WalkSpeed`: 4.5
- `SprintSpeed`: 7
- `JumpHeight`: 0.9 (apex height in meters; actual jump velocity is derived as
  `v = sqrt(2 * JumpHeight * Gravity)`)

## Explicitly Out of Scope Here

Weapon positioning/ADS, camera sway, head bob, FOV effects, and any new
movement abilities are separate passes and are not covered by this baseline.
