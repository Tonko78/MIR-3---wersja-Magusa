# MERGE GUIDE — integracja modyfikacji Magusa z serwerem docelowym (swoojeff.online)

Dla: agent Hermes pracujący nad wersją serwera na Linuxie (baza: Suprcode/Zircon).
Od: repo `Tonko78/MIR-3---wersja-Magusa` (Windows, katalog dla agentów AI).
Data: 2026-10-08, tip: `51ba9a5`.

## 1. Relacja baz — ważna

Obie gałęzie rosną z tego samego Zircona:

```
4cc883d "Fixes for render swapping" (2026-09-21)  ← HEAD serwera docelowego
   │
   ├─ 5 commitów Zircona (m.in. a051c34 "guild label fixes")
   │        │
   │        └─ tip tego repo: 51ba9a5  ← NIESZCZEGÓLNY: zawiera WSZYSTKO poniżej
   │
   └─ 10 niezcommitowanych zmian serwera docelowego (patrz §3)
```

- Tip tego repo (`51ba9a5`) jest **późniejszy** niż baza serwera docelowego.
- Wersja docelowa NIE zawiera commitów `4cc883d..a051c34` (5 commitów Zircona) — po merge zostaną dociągnięte automatycznie.
- `ServerCore/ServerDataInitializer.cs` — **identyczny bajt w bajt** w obu wersjach (obie strony dodały to samo). Nie wymaga decyzji.

## 2. Co leci do serwera docelowego (mody Magusa, tip 51ba9a5)

| Funkcja | Główna lokalizacja |
|---|---|
| Pety bojowe (EXP, poziomy, limity; default 4) | `ServerLibrary/Models/PlayerObject.CombatPets.cs`, `MonsterObject.CombatPets.cs`, `CombatPetSettings.cs`, `DBModels/UserCombatPet.cs`, `Tests/CombatPetChecks/` |
| Plecak 169 miejsc, kosz (odzysk 45 s) | `PlayerObject.Recycle.cs`, `ServerLibrary/Envir/Commands/...` |
| Magazyn fragmentów gildii | `PlayerObject.GuildFragments.cs`, `GuildFragmentStorage.cs`, `Client/Scenes/Views/GuildDialog.Fragments.cs`, `Tests/GuildFragmentChecks/` |
| DPAPI zapamiętane hasło (Windows) | `Client/Envir/LoginDetailsStore.cs`, `Client/Scenes/LoginScene.cs` |
| Portal PL/EN + kolejka kont HMAC | `Mir3.Web/`, `AccountPortal.Contracts/`, `ServerCore/AccountQueue/` |
| Launcher + pipeline publikacji | `Launcher.Core/`, `Patcher/`, `PatchManager/`, `ops/mir3-web/` |
| Seed bazy | `data/System.db` (weryfikowany, wersja 2026.10.06.1) |
| Testy i dokumentacja | `Tests/`, `ServerCore.Tests/`, `Mir3.Web.Tests/`, `docs/` |

Usunięte względem starego kodu (celowo): `GroupLootDialogs.cs`, `PlayerObject.GroupLoot.cs` (zastąpione przez fragmenty gildii), `Command/Admin/ClearBag.cs`, `DXCheckedComboBox.cs`, `Launcher/Patcher.exe`.

## 3. 10 plików zmienionych w serwerze docelowym — status na tipie 51ba9a5

Porównanie: wersja docelowa (4cc883d + lokalne zmiany) vs tip tego repo.

**IDENTYCZNE — nie wymagają żadnej decyzji (6):**
- `Client/Envir/CEnvir.cs`
- `LibraryCore/MirDB/Session.cs`
- `ServerLibrary/Envir/Translations/ChineseMessages.cs`
- `ServerLibrary/Envir/Translations/EnglishMessages.cs`
- `Client/Properties/Resources.resx`
- `ServerCore/ServerDataInitializer.cs`

**RÓŻNIĄ SIĘ — wymaga połączenia (4):**
| Plik | Zmiana docelowa | Zmiana Magusa | Różnica |
|---|---|---|---|
| `ServerCore/Program.cs` | +38/−2 | +34/−2 | ~33 linie — obie strony inicjalizują własne moduły (AccountQueue/pety); połączyć oba bloki |
| `ServerLibrary/Envir/Config.cs` | +14/−4 | +13/−4 | ~12 linii — oba strony dodają pola `Config`; połączyć listę pól |
| `ServerLibrary/Envir/SConnection.cs` | +12/−1 | +10/−1 | ~11 linii — dispatch pakietów; połączyć case'y |
| `ServerLibrary/Envir/SEnvir.cs` | +23/−8 | +22/−8 | ~17 linii — init/stop; połączyć oba ciągi wywołań |

Uwaga: wstępne diffy tych plików wyglądają na tysiące linii różnicy — **to artefakt CRLF/LF** (wersja docelowa: Linux/LF, repo Magusa: Windows/CRLF). Po `diff --strip-trailing-cr` pozostaje tylko ~10–35 realnych linii na plik.

## 4. Rekomendowany proces merge (dla agenta)

1. W gałęzi serwera docelowego: `git add -A && git commit -m "wip: lokalne zmiany przed merge Magusa"` (zabezpiecz 10 plików).
2. `git remote add magusa https://github.com/Tonko78/MIR-3---wersja-Magusa.git && git fetch magusa`.
3. `git merge magusa/main -X theirs` **NIE** — za agresywne przy 4 plikach z §3. Zamiast:
   `git merge magusa/main` → rozwiązać konflikty w 4 plikach z §3 (połączyć oba bloki zmian, zachowując semantykę każdej strony), reszta ma się scalić automatycznie (baza wspólna, 6 plików identycznych).
4. Alternatywnie (czystsza): `git diff 4cc883d -- <10 plików docelowych>` → patch; `git checkout magusa/main -b merge-magus`; `git am -3` / ręczne nałożenie patcha; commit.
5. Po merge: `dotnet build "Zircon Server.sln"` + `dotnet test` (suite w repo: `Tests/`, `ServerCore.Tests/`, `Mir3.Web.Tests/`).

## 5. Ograniczenia i otwarte sprawy

- **Windows-first**: DPAPI (`LoginDetailsStore.cs`), instalator ISCC (`ops/mir3-web/Mir3-Launcher.iss`) — na Linuxie portal działa, ale launcher/DPAPI nie; wersja docelowa musi dostarczyć odpowiednik (np. libsecret/keyring) albo wyłączyć te funkcje.
- **Suite `Mir3.Web`**: 234/367 testów padało w pełnym przebiegu (rejestracja/SQLite/tokeny) — stan na 2026-10-08; wąskie testy config/SMTP: 47/47 ✅. `MAGUSA_VERIFICATION.md` w `docs/` ma pełny wykaz.
- **`Mir3.Web.FdZeroProbe`**: dev-tool diagnostyczny (bin/obj w .gitignore), nie jest częścią pipeline produkcyjnego.
- **Konwencja katalogu**: repo to "katalog dla agentów AI" — zawiera tylko to, co ma polecieć do Gity (kod, testy, docs, ops, seed). Bez bin/obj, bez logów, bez poświadczeń, bez baz produkcyjnych.

## 6. Kontakt / zweryfikowanie

- Repo: https://github.com/Tonko78/MIR-3---wersja-Magusa
- Tip: `51ba9a5` (2026-10-08)
- CHANGELOG z pełną historią integracji: `CHANGELOG.md`
- Weryfikacja: `docs/MAGUSA_VERIFICATION.md`
