# TacticalStances-SPT

A BepInEx plugin for **SPT 4.1.x** (Single Player Tarkov) that adds three configurable tactical weapon stances with stamina modeling, shoulder swapping, and a tactical sprint animation. Designed to layer cleanly on top of [TarkovRL](https://github.com/Cortex/TarkovRealism) procedural weapon motion.

## What it does

TacticalStances lets you hold your weapon in three distinct ready positions while not aiming down sights. Each stance changes the weapon's position and rotation in your hands, affects arm stamina, and (in the case of High Ready) unlocks a tactical sprint animation.

### The three stances

| Stance | Description | Stamina effect |
|--------|-------------|----------------|
| **Low Ready** | Weapon lowered, muzzle pointed down. Relaxed carry. | Regenerates arm stamina 25% faster than vanilla. |
| **High Ready** | Weapon raised high, muzzle up. Alert carry. | Drains arm stamina while sprinting (10%/sec). Regenerates when not sprinting. |
| **Active Aim** | Weapon held close and angled inward. Aggressive ready. | Constantly drains arm stamina (5%/sec, 10% while sprinting). |

### Features

- **Spring-smoothed transitions** — the weapon smoothly moves between stances instead of snapping.
- **IK-aware offsets** — the stance offset is applied before the IK system reads the left-hand marker, so your support hand follows the weapon to the new position correctly.
- **Layers over TarkovRL** — offsets are applied to `WeaponRootAnim` (child of `WeaponRoot`), so TarkovRL's procedural sway/deadzone/tracking on the parent transform is preserved underneath.
- **Shoulder swap on lean** — leaning left (Q) swaps the weapon to your left shoulder; leaning right (E) swaps back. Stance offsets mirror accordingly. Can be toggled off to use vanilla Alt+left/right only.
- **Tactical sprint** — when in High Ready and sprinting, the weapon is held up while running (via the weapon size modifier parameter). Can be toggled off.
- **Stance persistence** — your selected stance is saved to the config and restored across raids.
- **Alt+Scroll cycling** — hold Left Alt and scroll the mouse wheel to cycle through the three stances.
- **Fully configurable offsets** — each stance has six configurable values (position X/Y/Z and rotation X/Y/Z) so you can tune the exact weapon pose.
- **Stamina drain multiplier** — a global config slider controls how much arm stamina the stances drain (0 = no drain, 1 = default, 2 = double).

### What it does NOT do

- Does not change weapon accuracy, recoil, or sway.
- Does not modify the ADS pose — stances are suppressed while aiming down sights.
- Does not affect sprint movement speed or stamina (only arm/hands stamina).
- Does not conflict with TarkovRL — it layers on top of TarkovRL's procedural motion.

## Controls (default)

| Action | Default key | Configurable |
|--------|-------------|-------------|
| Cycle stances | `Left Alt` + `Scroll` | Yes (toggle) |
| Shoulder swap | `Q` / `E` (lean) | Yes (toggle, off by default) |

Individual stance hotkeys are unbound by default — use `Left Alt` + `Scroll` to cycle through Low Ready, Active Aim, and High Ready. You can bind specific keys (e.g. F1/F2/F3) in the config if preferred.

The default stance on raid start is **Low Ready**.

Stances are suppressed while: aiming down sights, sprinting (except High Ready tactical sprint), inventory open, during interactions/reloads, grenade launcher mounted, or hands busy.

## Installation

1. Build the project (targets `netstandard2.1`) or grab a release DLL.
2. Copy `TacticalStances.dll` into your SPT `BepInEx/plugins` folder.
3. Launch the game — a config file will be generated at `BepInEx/config/com.devin.tacticalstances.cfg`.

## Build

```bash
# From the project folder
dotnet build -c Release
```

The `.csproj` expects an `SPT_INSTALL_DIR` environment variable (or MSBuild property) pointing to your SPT install. The default is `J:\Games\SPT 4.1.5` — override it on the command line if your SPT is elsewhere:

```bash
dotnet build -c Release -p:SPT_INSTALL_DIR="C:\SPT"
```

A post-build target copies the DLL to `BepInEx/plugins` automatically.

## Compatibility

- **SPT 4.1.x** (tested on 4.1.5)
- **BepInEx** with HarmonyX
- Compatible with **TarkovRL / Tarkov Realism** (layers on top of its procedural weapon motion)
- No server mod required — this is a client-only plugin

## Technical notes

The stance offset is applied directly to `WeaponRootAnim` (a child of `WeaponRoot`) in two places each frame:

1. **`Player.IkProcess` prefix** — captures the spring output as the base pose, then adds the stance offset on top. This runs before the IK solver reads the left-hand marker, so the support hand is placed at the stance-offset position.
2. **`Player.LateUpdate` postfix** — re-applies the offset from the captured base in case `LateTransformations` overwrote `WeaponRootAnim` during the visual pass.

Because `WeaponRootAnim` is a child of `WeaponRoot`, TarkovRL's procedural motion on the parent layers underneath cleanly. The game resets `WeaponRootAnim` via `SetPositionAndRotation` each frame in `ApplyComplexRotation`, so the offset is applied fresh every frame — no fighting with the animation system.

Transitions between stances use a critically-damped spring (frequency derived from the configurable `Transition Speed`) for natural weapon movement.

## Plugin metadata

```text
GUID:    com.devin.tacticalstances
Name:    Tactical Stances
Version: 1.0.0
```

## License

MIT — see LICENSE file if present. Otherwise, free to use and modify.
