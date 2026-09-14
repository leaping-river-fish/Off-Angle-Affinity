# Off-Angle

**Choose your affinity. Take any angle.**

A networked first-person arena shooter featuring momentum-based movement, charge-based ultimates, and a flexible Affinity loadout system.

---

## Overview

Off-Angle is a competitive FPS built in Unity with FishNet networking, emphasizing fluid movement mechanics and strategic loadout customization. Players engage in free-for-all matches where mastering movement chains (slide, wall-run, grapple) is as important as gunplay.

**Key Features:**
- **Momentum Movement** — Chain slides, wall-runs, and grapple slingshots to maintain and build velocity
- **Affinity System** — Choose from 6 elemental paths with unique passives, perks, and ultimates
- **Charge-Based Ultimates** — Build charge through combat and unleash powerful abilities
- **LAN Multiplayer** — Host/join sessions with short join codes (FishNet + Tugboat transport)

---

## Gameplay

### Combat
- **Health System**: Regenerating shields absorb damage first, backed by a health pool
- **Weapons**: Gun-based loadouts with primary and sidearm categories (melee to be added), featuring fire modes (semi/auto/burst) and shot types (hitscan, projectile, spread)
- **Match Type**: Free-for-all elimination, first to 30 kills wins
- **Respawn**: Timed respawn after death with ragdoll and death camera transition

### Movement
Players have access to a momentum-preserving state machine that rewards skillful movement:
- **Grounded** → Sprint → **Slide** → Jump (velocity preservation)
- **Airborne** → Contact wall → **Wall-run** → Wall-kick (free jump)
- **Grapple** → Pull toward anchor → Launch/slingshot or wall hold
- Movement states dynamically transition based on velocity, input, and environment

Key implementations:
- [`MovementStateMachine.cs`](Assets/_Project/Scripts/Movement/MovementStateMachine.cs) — State orchestration
- [`PlayerGrapple.cs`](Assets/_Project/Scripts/Networking/PlayerGrapple.cs) — Grapple hook physics
- Movement states in [`Assets/_Project/Scripts/Movement/States/`](Assets/_Project/Scripts/Movement/States/)

### Affinity System
Affinities define your playstyle through a two-path loadout system:

**Six Paths:**
- **Cinder** — Affinity for aggressive players who want to apply as much pressure onto their enemies at the cost of themselves
- **Tide** — Affinity for commanding players who want to control the field of battle, altering player states and positions
- **Thorn** — Affinity for tenacious players who prefer close quarters combat, burst, and survivability
- **Tempest** — Affinity for elusive players who never want to be caught in the same position with enhanced movement capabilities
- **Frost** — Affinity for precise players who want to choose or create favourable engagements to win gun fights
- **Void** — Affinity for bold players who want to bend the rules for underhanded advantages in fights

**Loadout Structure:**
- **Primary Affinity**: Grants passive effect, access to 3 ultimates (pick 1), and perks from all three rows
- **Secondary Affinity**: Grants perks from two rows only (no passive, no ultimate)
- **Perks**: 3×3 grid per affinity, choose one per row with restrictions on middle row

**Ultimates:**
- Charge meter builds from damage dealt/taken plus passive trickle
- No cooldown — activate when fully charged, spending the meter
- Examples: Solar Ascension (Cinder), Hadal Zone (Tide)

Key implementations:
- [`AffinityDefinition.cs`](Assets/_Project/Scripts/Affinities/AffinityDefinition.cs) — Data structure for each path
- [`PlayerAffinity.cs`](Assets/_Project/Scripts/Networking/PlayerAffinity.cs) — Runtime effect application
- [`PlayerUltimate.cs`](Assets/_Project/Scripts/Networking/PlayerUltimate.cs) — Charge and activation

---

## Architecture

### Engine & Networking
- **Unity** game engine
- **FishNet** networking library with **Tugboat** LAN transport
- **Host/Client** model — Host acts as both server and local client
- **Join Codes** — Short codes encode IPv4:port for easy LAN session joining
- **No relay** — Direct LAN connections only, no matchmaking infrastructure

### Session Flow

```
Bootstrap → MainMenu → [Host/Join] → Lobby → AffinitySelect → Game → MatchEnd
    ↑                                   ↑                              ↓
    └───────────────────────────────────┴──────────────────────────────┘
                            (host return to lobby or leave to menu)
```

**Flow Details:**
1. **Bootstrap**: Loads persistent `NetworkManager` root with core services
2. **MainMenu**: Host or join selection
3. **Lobby**: Display connected players via [`LobbyPlayerList`](Assets/_Project/Scripts/Networking/LobbyPlayerList.cs), host initiates match start
4. **AffinitySelect**: All players configure loadouts (affinities, perks, ultimate, weapons)
5. **Game**: FFA combat until first player reaches 30 kills
6. **MatchEnd**: Display winner, host can return everyone to lobby or players can leave to main menu

**Late Join**: Players joining after match start are kicked with a notification

### Core Systems

**Persistent Root** (DontDestroyOnLoad):
- [`NetworkMenuController.cs`](Assets/_Project/Scripts/Networking/NetworkMenuController.cs) — Host/join/leave session management
- [`GameFlowController.cs`](Assets/_Project/Scripts/Networking/GameFlowController.cs) — Scene sequence and rematch reset
- [`PlayerSpawner.cs`](Assets/_Project/Scripts/Networking/PlayerSpawner.cs) — Queue connections, spawn after loadout submission
- [`AffinitySelectionService.cs`](Assets/_Project/Scripts/Networking/AffinitySelectionService.cs) — Server-authoritative loadout storage

**Player** (Networked prefab):
- [`PlayerController.cs`](Assets/_Project/Scripts/Player/PlayerController.cs) — Composition root for player systems
- [`NetworkPlayerController.cs`](Assets/_Project/Scripts/Networking/NetworkPlayerController.cs) — Ownership gating
- [`Health.cs`](Assets/_Project/Scripts/Combat/Health.cs) — HP and shield management
- [`Gun.cs`](Assets/_Project/Scripts/Weapons/Gun.cs) — Weapon fire pipeline

**Match** (Scene objects):
- [`MatchManager.cs`](Assets/_Project/Scripts/Networking/MatchManager.cs) — Win condition tracking (first to N kills)
- [`KillCount.cs`](Assets/_Project/Scripts/Combat/KillCount.cs) — Per-player kill scoring

---

## Project Structure

The codebase is organized by domain under `Assets/_Project/Scripts/`:

```
Assets/_Project/Scripts/
├── Networking/          # FishNet integration, session management, lobby, match flow
├── Player/              # PlayerController, camera, input, stats
├── Movement/            # State machine, grapple system, wall detection, abilities
├── Combat/              # Health, damage pipeline, respawn, hitboxes
├── Weapons/             # Gun system, loadouts, shot behaviors, ADS
├── Affinities/          # Affinity definitions, effects, ultimates, registry
├── UI/                  # Menus, HUD elements, lobby/match UI
├── Core/                # Input state, cross-cutting utilities
└── Editor/              # Custom inspectors and tools
```

**Key Registries** (ScriptableObjects):
- [`AffinityRegistry.asset`](Assets/_Project/Data/AffinityRegistry.asset) — All affinity definitions
- [`WeaponRegistry.asset`](Assets/_Project/Data/WeaponRegistry.asset) — All weapon definitions

---

## Development Status

This project is in active development. Core systems (movement, combat, networking, affinities) are functional. Affinity effects and ultimate behaviors are partially implemented.

---

## Getting Started

1. Open the project in Unity
2. Load the **Bootstrap** scene
3. Enter Play mode
4. Host a session or join via code/IP
5. Configure your Affinity loadout and weapons
6. Fight to 30 kills

---

*For questions or contributions, please contact Me.*
