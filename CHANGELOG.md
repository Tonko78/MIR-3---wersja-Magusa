# Changelog

## Integracja Magusa — 2026-10-08

### Import
- Zachowanie początkowej historii repozytorium oraz historii Zircon przez merge upstream `a051c34b74aee3e76f2ffa7185a68f5cbef018db`.
- Wybranie pełniejszego snapshotu `MIR3_TEST/Source`; paczka gildii była już włączona.
- Plecak 169 miejsc, kosz przedmiotów z czasem odzyskiwania 45 sekund.
- Trwałe pety bojowe z EXP, poziomami i konfigurowalnymi limitami; domyślny limit 4.
- Magazyn fragmentów gildii: uprawnienia, rewizje, składanie i rozszerzanie.
- DPAPI dla zapamiętanego hasła; poprawki preferencji i interfejsu.
- Portal rejestracji PL/EN, kolejka kont HMAC, launcher i narzędzia dystrybucji.
- Audytowany seed MirDB `System.db` z ośmioma korektami ikon i wersją `2026.10.06.1`.

### Porządkowanie integracji
- Wykluczenie kont, `Users.db`, poświadczeń, prywatnych wdrożeń, logów, buildów i duplikatów plików strony głównej.
- Przywrócenie publicznej strony głównej z zachowanej kopii; panel administratora pozostawiony osobno.
- Neutralne połączenie gry `127.0.0.1:7000`; usunięcie prywatnego profilu adresów testowych.
- Brak domyślnego serwera aktualizacji launchera: operator musi wskazać własne HTTPS.
- Dokumentacja źródeł, danych, instrukcji uruchamiania i faktycznych ograniczeń weryfikacji.

### Różnice kompatybilności
- Snapshot Magusa nie zawiera funkcji grupowego łupu z bieżącego upstream. Usunięto jej pozostałe pliki zamiast łączyć niezgodne protokoły.
- Nowe modele MirDB i pakiety wymagają zgodnego klienta/serwera oraz testu migracji prywatnych danych na kopii.
- Repozytorium nie obejmuje pełnych assetów klienta ani map runtime.

Wyniki kompilacji i testów: `docs/MAGUSA_VERIFICATION.md`. Historia dat plików lokalnych nie jest traktowana jako historia wydań ani dowód autorstwa.
