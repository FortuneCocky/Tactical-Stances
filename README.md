# TacticalStances-SPT

A BepInEx plugin for **SPT 4.1.x** that adds ready stances, canted aim, and shoulder swapping — so your weapon isn't always glued to your shoulder. Built to layer on top of [TrueView](https://github.com/FortuneCocky/TrueView).

## The stances

| Stance | What it feels like |
|--------|-------------------|
| **Low Ready** | Weapon lowered, muzzle down — relaxed carry. Faster sprint spool-up. |
| **High Ready** | Muzzle up, alert posture — tactical sprint animation. Moves faster. |
| **Active Aim** | Gun raised and shouldered — faster move while aiming. |
| **Canted Aim** | Weapon rolled to the side — auto-switches to a side-mounted sight if your gun has one. |

## Features

- **Hold Alt + scroll** to cycle stances (or bind a key per stance in config)
- **Smooth spring transitions** — the weapon glides between stances, no snapping — including swapping stances mid-sprint
- **Fire return** — shooting kicks you into a firing pose, then settles back to your stance after a delay
- **Trigger discipline option** — can block firing while Low/High Ready so stances actually matter
- **Canted aim** — hold or toggle a key (default mouse button) to cant; auto-detects side sights and restores your main sight after
- **Lean shoulder swap** — weapon moves to your left shoulder while left-leaning
- **Pistol-specific stances** — separate poses for sidearms
- **Arm stamina model** — stances drain/regen stamina scaled by weapon weight (LMGs are heavy, pistols are cheap)
- **Stance bonuses** — each stance has configurable perks: sprint accel, move speed, ADS move speed, recoil
- **Persistent stance** — your choice saves across raids
- **Every value configurable** — pose offsets, rotations, speeds, stamina — `BepInEx/config/com.devin.tacticalstances.cfg`

## Install

1. Download `TacticalStances-x.x.x.zip` from [Releases](https://github.com/FortuneCocky/Tactical-stances/releases)
2. Extract into your SPT folder (merge `BepInEx`)
3. Launch — config generates at `BepInEx/config/com.devin.tacticalstances.cfg`

## Compatibility

- SPT 4.1.x — client only, no server mod
- Pairs with **TrueView** — stances layer over its procedural weapon motion

## License

Custom license — free for personal use; **modification and redistribution require written permission**. See [LICENSE](LICENSE).

**Contact for permission:** GitHub issues on this repo, or Discord `Isaiahnohart` (ID `138781947550892032`).
