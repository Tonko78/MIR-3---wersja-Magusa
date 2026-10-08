# Weryfikacja integracji Magusa

Stan częściowy: 2026-10-08. Środowisko Windows, lokalny .NET SDK 10.0.401. Ten raport nie potwierdza gotowości produkcyjnej ani zakończenia publikacji.

## Kompilacja

Ponownie wykonano `dotnet build "Zircon Server.sln" -c Release --nologo -v quiet`; polecenie zakończyło się kodem 0. Wcześniej osobno budowano Client i ServerCore. Kompilacja nie zastępuje uruchomienia gry z kompletnymi zasobami ani testów migracji.

## Portal — poprawki konfiguracji

Odczytano zmienione źródła i pliki wynikowe TRX:

| Zestaw | Przeszło | Nie przeszło | Razem |
|---|---:|---:|---:|
| Regresja konfiguracji przed poprawką (`config-red`) | 6 | 9 | 15 |
| Regresja proxy przed poprawką (`proxy-red`) | 0 | 1 | 1 |
| Konfiguracja/proxy/SMTP po poprawce (`config-green`) | 47 | 0 | 47 |
| Pełny zestaw portalu (`web-full`) | 133 | 234 | 367 |

Linki weryfikacji e-mail i statusu korzystają z walidowanego `MIR3_PUBLIC_BASE_URL`, nie z odziedziczonej domeny. Wymagany jest origin HTTPS bez poświadczeń, ścieżki, query i fragmentu. SMTP waliduje host, port i timeout; STARTTLS pozostaje wymagane. `MIR3_TRUSTED_PROXIES` określa listę konkretnych adresów IP; brak ustawienia oznacza loopback, nie zaufanie wszystkim proxy.

**Pełny zestaw nie przeszedł.** W odczytanym TRX występują wyjątki funkcji dostępnych wyłącznie na Linuksie, blokady usuwania plików SQLite na Windows oraz niezgodności asercji tekstowych/lokalizacji i typu wyjątku. Nie należy klasyfikować wszystkich niepowodzeń jako nieszkodliwych problemów środowiska bez dalszej diagnozy. Wymagane są testy na Linuksie i analiza pozostałych asercji. Nie wysyłano prawdziwych wiadomości ani nie wdrażano portalu.

Lokalne dowody (nie dodawane do repozytorium): `C:/Users/Aramis/AppData/Local/hermes/cache/scratch/mir3-publication/`, pliki `.trx` oraz `.log` wymienionych zestawów.

## Ponowne testy rdzenia gry i publikowanych danych

Uruchomiono po poprawkach konfiguracji, z logami i TRX w `C:/Users/Aramis/AppData/Local/hermes/cache/scratch/magusa-final/`:

| Zestaw | Wynik |
|---|---|
| AccountPortal.Contracts.Tests | 18 przeszło, 0 niepowodzeń |
| ServerCore.Tests | 55 przeszło, 0 niepowodzeń |
| Launcher.Core.Tests | 60 przeszło, 1 niepowodzenie |
| CombatPetChecks | 79 sprawdzeń, kod zakończenia 0 |
| GuildFragmentChecks | 41 sprawdzeń, kod zakończenia 0 |
| GroundLootChecks | Wszystkie raportowane scenariusze przeszły, kod zakończenia 0 |
| Tests/publication/test_publication.py | 3 testy przeszły |
| Tests/ops/test-public-defaults.py | 3 testy przeszły |

Jedyny nieudany test launchera (`Resolve_client_path_rejects_symlinked_directory_inside_root`) zatrzymał się przy tworzeniu dowiązania: konto Windows nie ma wymaganych uprawnień. Nie dotarł do sprawdzenia ochrony ścieżki; nie oznaczamy tej ochrony jako przetestowanej. Nie zmieniano uprawnień systemowych.

Testy petów i gildii uruchomiono na nowych, oddzielnych katalogach fixture poza repozytorium, bez danych produkcyjnych. Raport testów obejmuje m.in. serializację pakietów, restart MirDB, scenariusze starego schematu, prawa dostępu i limity.

## Skrypty pakowania i audyt publikacji

Potwierdzono w logu `magusa-ops-final.log`: 49 testów Python, 40 przeszło, 9 pominięto, bez niepowodzeń i błędów. Pominięcia obejmują 5 przypadków dowiązań bez uprawnień Windows, 3 przypadki nazw plików niedostępnych na Windows i 1 kolizję wymagającą systemu plików rozróżniającego wielkość liter. Nie wykonywano prawdziwego SSH, publikacji, pobierania ani uruchamiania klienta. Testy skryptów powłoki wdrożeniowej nie zostały uruchomione.

Niezależny audyt nadal **blokuje publikację** z dwóch powodów:

1. Samodzielne generatory ZIP/patch wymagają wspólnej kontroli wejścia, aby nie opublikować przypadkowo prywatnych baz, konfiguracji z zapamiętanymi poświadczeniami i kopii zapasowych.
2. Instalator musi przekazywać jawnie skonfigurowany adres HTTPS aktualizacji do launchera, którego domyślny host jest obecnie pusty.

Obie poprawki zostały zaimplementowane i oczekują niezależnego ponownego przeglądu. Wspólny moduł `publication_input.py` jest używany przez generatory ZIP i patch, a instalator wymaga jawnego `PatchOrigin`. Potwierdzono w `magusa-blockers-final.log`: **53 testy, 44 przeszły, 9 pominięto, 0 niepowodzeń i błędów**. Te wyniki zastępują wcześniejszy wynik 49 testów.

**Ograniczenie instalatora:** ISCC nie jest zainstalowany, więc nie wykonano kompilacji ani uruchomienia instalatora. Sprawdzono walidator adresu i testy kontraktu pliku instalatora; zachowanie istniejącego `Launcher.ini` nie zostało sprawdzone w rzeczywistej instalacji.

Audyt nie znalazł oczywistych aktywnych poświadczeń w przeglądanych plikach tekstowych, ale nie certyfikuje zawartości wszystkich binariów ani historii Git.

## Pozostałe ograniczenia

Końcowa weryfikacja skryptów ops, kompletny audyt publikowanych plików i potwierdzenie push nie są jeszcze zamknięte. Wyniki innych zestawów testów wymagają oddzielnego zestawienia. Nie wykonano migracji produkcyjnej bazy użytkowników ani graficznego testu klienta. Instrukcje danych znajdują się w `data/README.md`.
