# Jolt Physics smoke example

Runs native Jolt without a window or GPU and checks an impulse against `J/m`,
free fall against semi-implicit Euler integration, and sphere/floor contact.
Prints `JOLT_SMOKE_OK` and `真` on success; failed checks return exit code 1.

```powershell
dotnet run --project AquariusDesktop -- AquariusDesktop/examples/jolt_physics/main.aqua
dotnet test AquariusTests -c Release --filter FullyQualifiedName~Jolt
```

NuGet restores the native engine. See the [Jolt API guide](../../physics/Jolt.md).
