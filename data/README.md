# Definicje gry — MirDB

`System.db` pochodzi z `MIR3_GOTOWY/SERWER/Database/System.db`. Jest nieszyfrowanym binarnym plikiem MirDB; nie otwieraj go narzędziami SQLite i nie stosuj skryptów SQL.

- Rozmiar: 5 752 543 bajty.
- Wersja: `2026.10.06.1`.
- SHA-256: `0bbab12e09b38ba3714b03d9bf4f483e6b92e071f82ac84d0311d62d32e7f2fe`.
- 80 kolekcji `Library.SystemModels.*`, 26 348 rekordów. Przykładowo: 1078 przedmiotów, 174 umiejętności, 309 potworów, 244 mapy, 125 NPC, 1471 respawnów i 10 382 definicje dropu.
- Nie zawiera kolekcji kont/postaci z `Users.db`. Audyt pól tekstowych nie wykazał adresów infrastruktury ani typowych poświadczeń. Kontrola techniczna nie stanowi gwarancji bezpieczeństwa każdego przyszłego pliku o tej nazwie.

## Potwierdzone zmiany lokalne

Punktem odniesienia jest lokalny `MIR3_TEST/Server/Database/System.db`, SHA-256 `08aba27a1faadfa523a37eaff2e10b5aff35aae85328399d10bf6ff3aace42ab`, wersja `2026.10.01.1`. Taką samą sumę ma lokalna kopia bazy sprzed startu.

| MagicInfo.Index | Nazwa | Stara ikona | Nowa ikona |
|---:|---|---:|---:|
| 20 | Advanced Destructive Surge | 526 | 204 |
| 57 | Shock | 532 | 38 |
| 143 | Demonic Recovery | 536 | 256 |
| 156 | Elemental Swords | 502 | 176 |
| 158 | Tornado | 508 | 146 |
| 161 | Summon Dead | 514 | 208 |
| 171 | Dragon Wave | 542 | 320 |
| 174 | Chain Of Fire | 520 | 476 |

Poza wersją bazy i powyższymi ikonami nie wykryto zmian pól ani liczby rekordów między tymi dwoma snapshotami. Pozostałe 78 bloków kolekcji jest identycznych. Upstream nie dostarczył porównywalnego `System.db`; **nie jest to pełne porównanie z oryginalnymi danymi Zircon**. Obrazy ikon wymagają zgodnej biblioteki MIcon — odczyt bazy nie potwierdza wyglądu assetów.

## Instalacja jako seed w NOWYM środowisku

Z katalogu repozytorium, po `dotnet publish ServerCore/ServerCore.csproj -c Release -o artifacts/server`:

```powershell
$expected = '0bbab12e09b38ba3714b03d9bf4f483e6b92e071f82ac84d0311d62d32e7f2fe'
if ((Get-FileHash data/System.db -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Nieprawidlowa suma System.db'
}
$target = 'artifacts/server/Database'
if (Test-Path $target) { throw 'Katalog bazy juz istnieje; nie nadpisuj istniejacego wdrozenia' }
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item data/System.db "$target/System.db"
```

Są to instrukcje instalacji; publikacja repozytorium nie uruchamia serwera. Dostarcz mapy osobno. Ścieżki runtime wynikają z konfiguracji i katalogu binarium.

## Istniejący serwer

Nie nadpisuj jego bazy w ciemno: zatrzymaj serwer, zachowaj prywatną kopię **System.db i Users.db** oraz zgodnych binariów, wykonaj test na odizolowanej kopii. Zachowaj indeksy i relacje własnych definicji. Ten snapshot nie jest uniwersalną migracją każdej istniejącej instalacji.

Po zatwierdzeniu zmian zapewnij zgodne definicje serwerowi i klientowi. Nie kopiuj `Users.db` na klientów ani do Git. `Session.Initialize()` w trybie zapisu może automatycznie utrwalić migracje; do audytu używaj izolowanych kopii i `SessionMode.None`.

Sprawdzono odczyt dostarczonego pliku przez rzeczywistą bibliotekę MirDB w `SessionMode.None`, wartości ikon i niezmienność SHA-256. Nie wykonano produkcyjnej migracji ani testu graficznego klienta.
