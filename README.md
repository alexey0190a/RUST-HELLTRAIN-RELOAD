## What is this
Helltrain — Rust plugin (uMod/Oxide).
Train-based PvE/PvP event.
This repository contains production code.



## Development rules (IMPORTANT)

- This is a live production plugin.
- 1 task = 1 diff.
- No refactoring unless explicitly requested.
- No renaming, no reformatting, no architecture changes without approval.
- Minimal changes only.
- If a task is ambiguous — STOP and ask.



- ## Architecture (high-level)

- Helltrain.cs — core plugin (lifecycle, spawn, cleanup)
- HelltrainGenerator.cs — planning/generation logic (no spawn, no lifecycle)
- Layout JSONs — data only, no logic

- ## Definition of Done

- Plugin compiles
- Plugin loads without errors
- No new warnings in server log


## What is considered broken

- Compilation errors
- Runtime exceptions (NRE)
- Duplicate methods / ambiguous calls



## Testing

- Deploy to server
- Run: oxide.reload Helltrain
- Check server console log
