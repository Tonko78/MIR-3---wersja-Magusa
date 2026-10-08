# Legend of Mir 3 — Wersja Magusa

Źródła serwera, klienta i narzędzi oparte na **Suprcode/Zircon**, ze zmianami Magusa i bazą definicji gry. To repozytorium programistyczne, **nie gotowa paczka klienta z grafiką, dźwiękami i mapami**.

## Pochodzenie

- Upstream: `https://github.com/Suprcode/Zircon`, punkt porównania `a051c34b74aee3e76f2ffa7185a68f5cbef018db` (`master`). Zachowano historię upstream i pierwotny commit repozytorium przez merge, bez force-push.
- Import: `C:\BBB\MIR3_TEST\Source`. Względem `C:\BBB\mir3-swf-zircon-main` stwierdzono 33 zmienione pliki i 2 nowe; jeden plik projektu różnił się tylko formatowaniem.
- Paczka `Gildia-fragmenty-komplet/Paczka/Sources` była już zawarta w źródłach. Ponowne nadpisanie cofnęłoby późniejsze poprawki.
- Dane: wyłącznie `MIR3_GOTOWY/SERWER/Database/System.db`, opublikowane jako `data/System.db`.
- Wykluczono bazy użytkowników, konta, hasła, prywatne FTP, kopie bezpieczeństwa, logi, lokalne INI i wyniki kompilacji. Oryginały `C:\BBB` pozostawiono bez zmian.

## Wersja Magusa / Zmiany

Opis dotyczy różnic snapshotu względem wskazanego upstream, nie przypisania autorstwa każdego fragmentu.

| Obszar | Zmiany i pliki |
|---|---|
| Plecak | 169 slotów, siatka 13×13, okno `DXWindow`, usuwanie DEL i zabezpieczenia UI. `LibraryCore/Globals.cs`, `Client/Scenes/Views/InventoryDialog.cs`. |
| Kosz przedmiotów | Przywracanie przez 45 sekund, kontrola miejsca i ciężaru, zachowanie instancji. `ServerLibrary/Models/PlayerObject.Recycle.cs`, pola `DBModels/UserItem.cs`, relacja `CharacterInfo.cs`, nowe pakiety klient/serwer. |
| Pety bojowe | Trwały zapis/odtworzenie, EXP, poziomy, statystyki, domyślny limit 4, nazwy/kolory z poziomem i EXP, oswajanie i przywołania Taoisty. `UserCombatPet.cs`, `CombatPetSettings.cs`, `PlayerObject.CombatPets.cs`, `MonsterObject.CombatPets.cs`. |
| Fragmenty gildii | Oddzielny magazyn, wpłaty/wypłaty, składanie przedmiotów, rozszerzanie pojemności, uprawnienia i kontrola rewizji. `GuildFragmentStorage.cs`, `PlayerObject.GuildFragments.cs`, `GuildDialog.Fragments.cs`, modele i pakiety. |
| Logowanie | Zapamiętane hasło chronione Windows DPAPI CurrentUser, migracja wcześniejszej jawnej wartości. `Client/Envir/LoginDetailsStore.cs`, `LoginScene.cs`, `CConnection.cs`. Poświadczenia są związane z kontem Windows. |
| Interfejs | Zapis preferencji, poprawki rozmiaru i położenia okien, ustawień grafiki oraz panelu gildii. |
| Administracja | Obsługa stałych administratorów; blokada uprzywilejowanego logowania z pustym/placeholderowym hasłem nadrzędnym. |
| ServerCore | Kolejka kont, inicjalizacja walut, zatrzymanie `stop`/`STOP.SERVER`, dostosowania ścieżek MirDB do pracy między systemami. |
| Portal | `Mir3.Web`: rejestracja i weryfikacja e-mail, panel administratora, aktywacja/dezaktywacja, reset hasła, status rejestracji i PL/EN. Osobna baza SQLite z migracjami EF Core. |
| Kolejka | `AccountPortal.Contracts`, `ServerCore/AccountQueue`: HMAC, ograniczenie czasu ważności, obsługa duplikatów i wyników. Portal nie zapisuje bezpośrednio do MirDB gry. |
| Dystrybucja | `Launcher.Core`, przebudowany launcher/patcher, walidacja HTTPS, manifestów i ścieżek, narzędzia w `ops/mir3-web`. |

**Niezgodność z bieżącym upstream:** snapshot Magusa nie zawiera integracji wspólnego łupu grupowego. Usunięto pozostałości jej plików, aby nie tworzyć niespójnego zestawu. Nie deklarujemy zachowania wszystkich funkcji upstream. Klient, serwer i `LibraryCore` muszą pochodzić z jednej wersji.

### Bazy danych

- `data/System.db`: binarny **MirDB**, nie SQLite ani SQL. Wersja `2026.10.06.1`, 80 kolekcji systemowych i 26 348 rekordów. Osiem korekt ikon, suma SHA-256 i import: [data/README.md](data/README.md).
- W kodzie schematu użytkowników dodano modele/relacje petów, fragmentów gildii i kosza. **Nie opublikowano `Users.db`.** Przed migracją wykonaj backup i próbę na izolowanej kopii. Inicjalizacja MirDB w trybie zapisu może automatycznie zapisać migracje.
- Migracje SQLite portalu znajdują się w `Mir3.Web/Migrations`; nie stosuje się ich do MirDB gry.

## Budowanie

.NET SDK 10; klient i WinForms wymagają Windows. Część projektów GUI odwołuje się do DevExpress — zapewnij dostęp do zależności i nie publikuj kluczy NuGet/licencji.

```powershell
dotnet build ServerCore/ServerCore.csproj -c Release
dotnet build Client/Client.csproj -c Release
dotnet build "Zircon Server.sln" -c Release
dotnet publish ServerCore/ServerCore.csproj -c Release -o artifacts/server
```

Biblioteki `Components` pochodzą z upstream; nie są nowymi wynikami kompilacji. Mapy i biblioteki grafiki/dźwięku dostarcz osobno z zasobów, do których masz prawa.

## Uruchomienie i konfiguracja

1. Przygotuj nowy izolowany katalog wdrożenia. Nie uruchamiaj testów na produkcyjnym `Users.db`.
2. Zweryfikuj i skopiuj `data/System.db` do `Database/System.db` obok `ServerCore.dll`, według [instrukcji danych](data/README.md). Nie podmieniaj całych katalogów bazy.
3. Dostarcz zgodne mapy do `Map/` oraz zasoby klienta. Definicje map w bazie nie są plikami map.
4. Skonfiguruj `Server.ini` i `Zircon.ini`. Domyślne lokalne połączenie klienta/serwera: **127.0.0.1:7000**. Dla sieci ustaw własny adres, port i zaporę. Usunięto prywatne nadpisanie `TestServer.mode`.
5. Po przygotowaniu danych uruchom `dotnet artifacts/server/ServerCore.dll`; zatrzymuj komendą `stop` przed wymianą danych.
6. Launcher nie ma domyślnego serwera aktualizacji. W lokalnym `Launcher.ini`, sekcji `[Patcher]`, ustaw `Host` na własny zaufany adres HTTPS zakończony `/patch/`.

Nie ma gotowych kont administratora ani haseł. Cofnięcie do plecaka 48-slotowego wymaga migracji, inaczej przedmioty mogą pozostać poza zakresem starszego klienta.

Portal jest opcjonalny. Ustaw własny publiczny adres HTTPS, SMTP i sekrety kolejki/admina. `example.invalid` to celowo nieoperacyjne przykłady. Wdrożenie kolejki obejmuje mechanizmy plikowe specyficzne dla Linuksa; kompilacja Windows nie zastępuje testu wdrożenia Linux. Przejrzyj parametry skryptów `ops/mir3-web` przed ich uruchomieniem.

## Weryfikacja

Rzeczywiste wyniki i ograniczenia: [docs/MAGUSA_VERIFICATION.md](docs/MAGUSA_VERIFICATION.md).

```powershell
dotnet test AccountPortal.Contracts.Tests -c Release
dotnet test ServerCore.Tests -c Release
dotnet test Launcher.Core.Tests -c Release
dotnet test Mir3.Web.Tests -c Release
python Tests/publication/test_publication.py
```

`CombatPetChecks` i `GuildFragmentChecks` zapisują własne MirDB; jako argument podawaj **nowy pusty katalog**, nigdy katalog prawdziwej instalacji.

Zobacz [CHANGELOG.md](CHANGELOG.md), [mapę projektu](docs/PROJECT_MAP.md), [dokumentację Zircon](docs/README.md) i oryginalne forum [LOMCN](http://www.lomcn.org/forum/forumdisplay.php?735).
