# Guild fragment checks

From the repository root with .NET 10:

    dotnet run --project Tests/GuildFragmentChecks/GuildFragmentChecks.csproj -- ./work/guild-fragment-tests/

Use a new empty test directory on each run. The checks create their own System.db and Users.db
and never use the game's database. Failures print to stderr and return exit code 1.

41 assertions exercise real SConnection handlers and production storage planning:
permissions, safe zone/observer/trade restrictions, malformed quantities, stale/replayed
requests, two different members assembling from the same revision, partial deposits and
withdrawals, stack identity, capacity/funds, multi-stack recipe debits, factory failure,
packet serialization, MirDB restart and migration from a Users.db schema without the new
capacity column. Sockets, maps and rendering are replaced by disconnected fixtures.

Manual staging checks: new tab placement/scroll/selection, quantity buttons, permission
checkboxes, two connected clients receiving updated contents, reconnect/restart, ordinary
guild storage and private parts storage. Compilation and this fixture do not validate
rendering or any specific production database.