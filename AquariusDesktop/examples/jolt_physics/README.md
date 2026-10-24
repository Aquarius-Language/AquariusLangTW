# Jolt Physics Bottle example

Jolt is distributed as the independent `Aqua_JoltPhysics` TOML library.

```powershell
aqua bottle install ../Aqua_JoltPhysics --global
aqua build . -o main.wasm
aqua run main.wasm
aqua build . --target web -o web
```

The numerical smoke test checks impulse, gravity and settled contact with the actual Jolt Wasm engine and prints `JOLT_SMOKE_OK`. Import `/bottles/Aqua_JoltPhysics/main.aqua`; built-in `Jolt`/`JoltPhysics` imports are no longer registered. The former world methods and Traditional Chinese aliases remain in the package. Build through this manifest to include cached library assets.
