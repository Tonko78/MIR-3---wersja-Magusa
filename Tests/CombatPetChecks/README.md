# Combat pet checks

Run from the repository root with .NET 10:

```text
dotnet run --project Tests/CombatPetChecks/CombatPetChecks.csproj -c Release -- <empty-data-directory> [old-schema-database-copy]
```

Create the data directory and its `backup` subdirectory first. Both input directories must be disposable: the executable saves databases. Reusing a populated test directory creates duplicate fixture definitions.

The checks invoke server combat, spawn/despawn, teleport, logout, taming and all five Taoist summon completion handlers. They verify EXP sharing, caps, HP preservation, relog/restart, frozen offline duration, pending-slot limits, death and aggregate deletion. The optional second path tests migration of an older database lacking combat-pet records while preserving its characters/items.

Maps are synthetic; the fixture bypasses the graphical/network portion of login. This does not replace a manual client session with real maps, movement portals and production content. Failures are caught and written to stderr with exit code 1, avoiding unhandled-exception dialogs.
