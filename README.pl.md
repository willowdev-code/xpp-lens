# xpp-lens — indeks kodu X++ (D365 F&O) jako serwer MCP

[![CI](https://github.com/willowdev-code/xpp-lens/actions/workflows/ci.yml/badge.svg)](https://github.com/willowdev-code/xpp-lens/actions/workflows/ci.yml)

[English](README.md) | **Polski**

Czyta `PackagesLocalDirectory` **tylko do odczytu** i buduje własny indeks (SQLite) poza repozytorium AOS.
Claude korzysta z niego przez MCP zamiast czytać wielkie pliki XML z AOT. Zmiany między wersjami: [CHANGELOG.md](CHANGELOG.md).

© 2026 WillowDev. Udostępnione na [licencji MIT](LICENSE).

Niezależny projekt inspirowany [Graftem](https://github.com/trailhq/Graft) (graf kodu dla agentów programistycznych w wielu językach); xpp-lens nie dzieli z nim kodu i jest zbudowany pod X++ i AOT. Do wersji 1.2.0 nazywał się xpp-graft — `install.ps1` przejmuje istniejącą instalację xpp-graft (ustawienia, indeks, rejestrację w Claude).

## Instalacja na nowej maszynie

Paczki instalacyjnej nie ma w repozytorium (`dist\` jest pomijany przez `.gitignore`). Są dwie drogi:

**A. Z gotowej paczki** — na maszynie docelowej nie trzeba niczego instalować, nawet .NET.

1. Pobierz `xpp-lens-X.Y.Z.zip` z zakładki **Releases** tego repozytorium.
2. Rozpakuj i w zwykłym PowerShellu (nie jako administrator) uruchom:

```powershell
powershell -ExecutionPolicy Bypass -File .\xpp-lens\install.ps1 -Languages en-US,pl
```

**B. Z kodu źródłowego** — wymaga .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/xpp-lens.git C:\Dev\xpp-lens
cd C:\Dev\xpp-lens
.\pack.ps1                     # tworzy dist\xpp-lens\ i dist\xpp-lens-X.Y.Z.zip
powershell -ExecutionPolicy Bypass -File .\dist\xpp-lens\install.ps1 -Languages en-US,pl
```

Instalator kopiuje pliki do `C:\Tools\xpp-lens`, wykrywa `PackagesLocalDirectory` (z `web.config` AOS-a albo
układu katalogów), zapisuje konfigurację, rejestruje serwer w Claude Desktop i Claude Code, a na końcu buduje indeks
(modele własne ok. minuty, standard Microsoftu jednorazowo 20–90 minut, zależnie od dysku). Przed instalacją zamknij
Claude'a. Po instalacji uruchom go ponownie.

Aktualizacja instalacji: `xpplens update` pokazuje, czy jest nowsze wydanie; zamknij Claude'a i uruchom
`xpplens update --install`, żeby je pobrać (z kontrolą SHA-256) i uruchomić jego instalator — ustawienia i indeks zostają.
To samo daje uruchomienie `install.ps1` z nowszej paczki.

Przydatne parametry:

```powershell
.\install.ps1 -PackagesDir K:\AosService\PackagesLocalDirectory   # zamiast wykrywania
.\install.ps1 -Languages en-US,de -DisplayLanguage de             # języki etykiet
.\install.ps1 -FullModels XPL,XPLRetail                           # wymuszenie pełnego indeksu
.\install.ps1 -StandardModels HugeIsvModel                        # zepchnięcie modelu do poziomu standardowego
.\install.ps1 -NoStandard                                         # tylko modele własne (szybko, mały indeks)
.\install.ps1 -First -NoBuild -NoRegister                         # tryb nieinteraktywny / CI
```

Deinstalacja: `uninstall.ps1` (usuwa wpisy MCP wskazujące na tę instalację, pyta o indeks i pliki).
Nowy pakiet dystrybucyjny: `pack.ps1` (dodaj `-FrameworkDependent`, jeśli wolisz 5 MB i wymóg .NET 9).

## Konfiguracja po instalacji

```powershell
xpplens config                                   # pokaż ustawienia
xpplens config --add-language de                 # dołóż język etykiet
xpplens config --add-full-model XPL              # model/pakiet do pełnego indeksu
xpplens config --add-standard-model ContosoIsv   # model do poziomu standardowego
xpplens config --add-standard-publisher "Contoso"
xpplens config --packages-dir K:\AosService\PackagesLocalDirectory
xpplens config --standard-code false             # bez wywołań z kodu Microsoftu (mniejszy indeks)
xpplens config --usage-log false                 # nie zapisuj wywołań MCP dla 'xpplens stats'
xpplens detect [--set]                           # wykryj PackagesLocalDirectory
xpplens build                                    # zastosuj zmiany
xpplens build --std-only                         # dołóż standard Microsoftu do instalacji z -NoStandard
```

Ustawienia siedzą w `xpplens.json` obok katalogu `bin` — można je też edytować ręcznie.

## Trzy poziomy indeksu

| Poziom | Modele | Co zawiera |
|---|---|---|
| pełny | wszystkie niemicrosoftowe (wykrywane po `Publisher` w deskryptorze) + te z `extraFullModels` | obiekty, składowe, sygnatury i kod metod, referencje w kodzie (wywołania, także łańcuchowe, typy, pola, intrinsics, etykiety), referencje z metadanych |
| standardowy | modele Microsoftu | obiekty, pola/indeksy/relacje, sygnatury metod z zakresami linii, `extends`, CoC i event handlery oraz **wywołania** z ciał metod (wywołania, `new`, intrinsics — bez odczytów pól, typów i etykiet; wyłączasz przez `--standard-code false`) |
| skompilowany | pakiety wdrożone bez XML (np. lokalizacje krajowe albo moduły ISV dostarczone tylko w postaci skompilowanej) | nazwy obiektów z `bin\*.md`, metody, pola, grupy pól, relacje i **odwołania z kompilatora** z `.xref`, CoC z `ChainOfCommand.xml`, dziedziczenie z `ClassExtends.runtime`, etykiety z `Resources\<język>\*.resources.dll` — bez kodu źródłowego |

Pakiet standardowy można awansować do pełnego (`--add-full-model ApplicationSuite`), jeśli potrzebne są tam wszystkie
referencje (pola, typy, etykiety) i natychmiastowy `xpp_grep`. Kosztem jest dłuższa budowa i większa baza.

Rozmiar indeksu: ok. 0,6 GB bez wywołań standardu, ok. 0,9–1 GB z nimi (typowa maszyna deweloperska, ok. 190 tys. plików standardu).

Pakiety skompilowane przebudowują się same, gdy zmieni się ich `.xref`, `.md` albo zasoby. Ręcznie:
`xpplens build --compiled-only --force`. Katalogi, których nie da się zaindeksować wcale, wypisuje `xpplens status`
w linii „on disk but NOT indexed”.

## Uprawnienia do indeksu

Domyślnie indeks trafia do `%LOCALAPPDATA%\xpp-lens\index\xpp.db`, czyli tam, gdzie użytkownik uruchamiający
Claude'a ma prawo zapisu. Jeśli wskażesz indeks w katalogu instalacji, a instalację przeprowadzisz jako administrator,
Claude (działający bez podniesionych uprawnień) nie będzie mógł go aktualizować — zobaczysz wtedy w odpowiedziach
komunikat `index is read-only`, a wyniki zamarzną na stanie z chwili budowy. Naprawa:

```powershell
xpplens config --index-path "$env:LOCALAPPDATA\xpp-lens\index\xpp.db"
xpplens build
```

W trybie tylko do odczytu `xpp_method` i `xpp_object` i tak zwracają aktualny kod (parsują plik na żywo),
ale `xpp_find`, `xpp_callers` i `xpp_refs` korzystają z zamrożonego indeksu.

## Aktualność indeksu

- `FileSystemWatcher` na katalogach modeli pełnego poziomu — zapis z Visual Studio widoczny przy następnym zapytaniu.
- Skan dat plików przy starcie i co `rescanIntervalSeconds` (domyślnie 5 min) — wyłapuje Get Latest z Team Explorera.
- Standard: odcisk pakietu (deskryptory + `bin\*.dll`) — przebudowa tylko po aktualizacji platformy.
- Nowa wersja xpp-lens ze zmienionym analizatorem kodu: modele własne są analizowane od nowa raz, przy następnym
  starcie (ok. minuty); poziom standardowy przebudowuje się raz w tle, pakiet po pakiecie — przerwana przebudowa
  wznawia się od miejsca przerwania. Postęp pokazuje `xpp_status`.
- Zapisy serializowane nazwanym muteksem, więc Claude Desktop i Claude Code mogą działać równolegle.

## Mapa narzędzi

Na co odpowiada każde narzędzie MCP, kiedy Claude powinien po nie sięgnąć i jaki jest odpowiednik w CLI do samodzielnego uruchomienia.

| Narzędzie | Odpowiada na | Kiedy używać | Przykład w CLI |
|---|---|---|---|
| `xpp_find` | gdzie są obiekty, metody, pola (`*`, `?`, `Obiekt.składowa`, nazwy rozszerzeń z kropką) | znasz nazwę albo jej fragment | `xpplens find "Cust*Invoice*; SalesLine.createLine"` |
| `xpp_object` | szkielet obiektu: właściwości z etykietami, pola, indeksy, relacje, źródła danych, drzewo kontrolek/menu, metody z zakresami linii, rozszerzenia | potrzebna struktura, nie kod | `xpplens object CustTable --type table` |
| `xpp_method` | kod metody ze ścieżką pliku i zakresem linii, jej wrappery CoC i handlery | potrzebny kod; przy długich metodach z `match`/`lines` | `xpplens method SalesTable validateWrite --match "checkFailed" --context 2` |
| `xpp_callers` | kto wywołuje metodę — najpierw kod własny, potem pakiety skompilowane i kod Microsoftu; z wywołaniami łańcuchowymi (`Table::find().m()`) | wpływ zmiany, „gdzie to jest używane” | `xpplens callers CustTable creditMax` |
| `xpp_callees` | czego używa metoda: wywołania (z typem odbiorcy w łańcuchach), new, pola, enumy, intrinsics, etykiety | zrozumienie metody bez czytania jej | `xpplens callees SalesFormLetter run` |
| `xpp_refs` | każde użycie klasy, tabeli, pola, EDT, enuma, pozycji menu, etykiety | zmiana nazwy, usuwanie, użycia pola | `xpplens refs CustTable --member CreditMax` |
| `xpp_extensions` | klasy CoC (z owiniętymi metodami), rozszerzenia tabel/formularzy, event handlery, klasy pochodne | „co już zmienia ten obiekt” | `xpplens ext SalesTable` |
| `xpp_scaffold` | gotowy X++: wrapper CoC, handler zdarzeń tabeli/formularza/źródła danych/kontrolki, subskrybent delegata, handler pre/post — dokładna sygnatura, nazewnictwo z twoich modeli | przed pisaniem rozszerzenia | `xpplens scaffold coc SalesTable validateWrite --type table` |
| `xpp_build_errors` | błędy/ostrzeżenia ostatniego builda w Visual Studio, przypięte do linii w pliku XML | po buildzie — poprawki bez wklejania logów | `xpplens build-errors --severity warning` |
| `xpp_security` | pozycja menu / formularz → privileges (nadany dostęp) → duties → role, oraz w drugą stronę dla privilege, duty, roli | pytania o dostęp, nowe pozycje menu | `xpplens security CustTable --type display` |
| `xpp_join` | najkrótsza ścieżka relacji między dwiema tabelami jako gotowy `select … join … where` | pisanie zapytania przez kilka tabel | `xpplens join CustInvoiceTrans CustTable` |
| `xpp_entity` | encja danych: nazwy publiczne, tabela staging, drzewo źródeł danych ze złączeniami, mapowanie pól, klucze; albo encje używające danej tabeli | praca z DMF / OData | `xpplens entity CustCustomerV3Entity` |
| `xpp_changed` | obiekty zmienione na dysku od podanego czasu, w podziale na modele | po Get Latest, przegląd własnej pracy | `xpplens changed --since 3d` |
| `xpp_grep` | regex po ciałach metod (modele własne; standard z filtrem modelu) | wzorce tekstu, których inne narzędzia nie wyrażą | `xpplens grep "ttsbegin" --model Contoso*` |
| `xpp_label` | id etykiety → teksty we wszystkich językach, albo tekst → istniejące id etykiet | ponowne użycie etykiet | `xpplens label "Credit limit"` |
| `xpp_status` | stan indeksu, poziomy, praca w tle | sprawdzenie aktualności | `xpplens status` |

### Kilka zapytań w jednym wywołaniu i fragmenty metod

- `xpp_find` i `xpp_object` przyjmują kilka nazw rozdzielonych `;` — jedno wywołanie, osobna sekcja dla każdej nazwy.
- `xpp_method` przyjmuje kilka metod: `method="insert;update"` dla jednego obiektu albo `objectName="SalesTable.insert;CustTable::find"`.
- `xpp_method` z `match` (regex) i/lub `lines` (`120-180`) zwraca tylko te linie (± `context`), ponumerowane numerami
  linii z pliku; sygnatura i deklaracje zmiennych są zawsze dołączane, a pominięte fragmenty oznaczone
  `… N line(s)`. Bez tych parametrów zwraca całą metodę, jak dotąd.

### Drzewo kontrolek i menu

Duże drzewa (ponad 60 elementów) są zwijane do dwóch poziomów z licznikiem dzieci `[+N]`. Rozwijanie:

| Parametr | Działanie | Przykład |
|---|---|---|
| `filter` | wildcard po nazwie lub ścieżce; wypisuje **pełne ścieżki** | `xpplens object CustTable --type form --filter *PersonalTitle*` |
| `parent` | tylko poddrzewo danego elementu (nazwa albo ścieżka) | `--parent TabGeneral` |
| `depth` | liczba poziomów (pod `parent`, jeśli podany) | `--parent UpperGroup --depth 1` |

Kontrolki ReferenceGroup pokazują `ref=<datasource>.<ReferenceField>`, `replGroup=<ReplacementFieldGroup>`
i `relPath=<DataRelationPath>`. Elementy rozszerzeń menu pokazują `(under <Parent>)`, `position=<PositionType>`
i `menuitem=<MenuItemName>`.

## Statystyki użycia

Każde wywołanie MCP jest dopisywane do `%LOCALAPPDATA%\xpp-lens\usage\usage-RRRRMM.jsonl` (narzędzie, argumenty,
rozmiar odpowiedzi, czas, czy wynik był pusty). Dziennik nigdy nie opuszcza maszyny. Podsumowuje go `xpplens stats`:

```powershell
xpplens stats --days 7 --top 10
```

Pokazuje dla każdego narzędzia liczbę wywołań, średni / p95 / maksymalny rozmiar odpowiedzi w tokenach (znaki / 4),
czas i odsetek pustych odpowiedzi, a do tego największe i najwolniejsze wywołania oraz ostatnie puste odpowiedzi —
miejsca, gdzie narzędzie nie pomogło i Claude zapewne wrócił do czytania plików. Wyłączenie: `xpplens config --usage-log false`.

## CLI

```
xpplens find|object|method|callers|callees|refs|ext|scaffold|build-errors|security|join|entity|changed|grep|label …
xpplens build [--full-only] [--std-only] [--compiled-only] [--force]
xpplens status [--counts] | stats | update [--install] | detect | config | register | unregister | mcp | version
```

`xpplens help` wypisuje wszystkie opcje. Zmienne: `XPPLENS_CONFIG` (inna konfiguracja), `XPPLENS_VERBOSE=1`
(czasy zapytań SQL na stderr), `XPPLENS_TIMING=1` (łączny czas zapytania w CLI).

## Rozwój narzędzia

Kod źródłowy mieszka **osobno od instalacji**, domyślnie w `C:\Dev\xpp-lens`:

```
C:\Dev\xpp-lens\
  xpp-lens.sln             solucja dla Visual Studio 2022
  src\XppLens\*.cs         kod
  src\XppLens\Properties\launchSettings.json   profile uruchomieniowe (F5)
  tests\XppLens.Tests\     testy xUnit + przykładowe XML-e z AOT (Fixtures)
  build.ps1                 kompilacja; -Deploy podmienia binaria w instalacji
  pack.ps1                  pakiet ZIP do instalacji gdzie indziej
  install.ps1 uninstall.ps1 README.md README.pl.md CHANGELOG.md LICENSE
  release-notes.ps1         opis wydania jednej wersji z CHANGELOG.md (używa go workflow wydań)
  .github\                  workflowy CI i wydań, formularze zgłoszeń
  build\  dist\             wyniki (nie trzymaj tu niczego własnego)
```

### Visual Studio

Otwórz `C:\Dev\xpp-lens\xpp-lens.sln` w **Visual Studio 2022** (17.12 lub nowszym — VS 2019 nie obsługuje .NET 9).
Budowanie: Ctrl+Shift+B. Na pasku narzędzi obok zielonej strzałki wybierz profil z `launchSettings.json`
(`status`, `stats (MCP usage)`, `find (batch)`, `object (form controls)`, `method (fragment)`, `callers (incl. standard)`,
`scaffold coc`, `security`, `join`, `entity`, `changed (3 days)`, `build-errors`, `build compiled packages`,
`verbose SQL (status)`) i naciśnij F5 — program uruchomi się z debuggerem na prawdziwej konfiguracji i indeksie
(`XPPLENS_CONFIG` jest ustawione w profilu). Własny profil: Debug → *XppLens Debug Properties* → nowy profil,
w „Command line arguments” wpisz polecenie CLI.

Serwera MCP nie debuguje się przez F5 (rozmawia przez stdin/stdout z Claude'em). Żeby podejrzeć go w działaniu,
wdroż wersję Debug (`.\build.ps1 -Deploy -Configuration Debug`), zrestartuj Claude'a i w VS użyj
Debug → *Attach to Process* → `xpplens.exe`.

### Testy

Testy nigdy nie dotykają twojego `PackagesLocalDirectory` ani twojego indeksu. Kopiują mały zestaw przykładowych
pakietów z `tests\XppLens.Tests\Fixtures\PackagesLocalDirectory` („microsoftowy” pakiet `StdBase` i własny pakiet
`ContosoCore`, wyłącznie neutralne nazwy) do katalogu tymczasowego, budują tam indeks i sprawdzają odpowiedzi narzędzi.

**W Visual Studio:** Test → *Test Explorer* (Ctrl+E, T) → *Run All Tests* (Ctrl+R, A). Pierwsze uruchomienie
zbuduje solucję; pojedynczy test można debugować: prawy przycisk → *Debug*.

**Z linii poleceń:**

```powershell
cd C:\Dev\xpp-lens
dotnet test                                              # wszystkie testy (ok. 10 s)
dotnet test --filter "FullyQualifiedName~QueryTests"     # tylko testy narzędzi od początku do końca
dotnet test --filter "Name~Scaffold"                     # testy, których nazwa zawiera "Scaffold"
dotnet test --logger "console;verbosity=detailed"        # każdy test i szczegóły porażek
```

| Plik | Co sprawdza |
|---|---|
| `AnalyzerTests.cs` | lekser, nagłówki metod, rozwiązane wywołania, wywołania łańcuchowe (łańcuchy `ret:`), nierozwiązani odbiorcy, sygnatury do szkieletów |
| `AnalyzerTests.cs` → `HelperTests` | fragmenty metod, informacje o relacjach, parsowanie `since`, ścieżki z wyników builda, listy zbiorcze, dziennik użycia i raport |
| `QueryTests.cs` | każde narzędzie od początku do końca na indeksie z przykładów: find (podkreślnik, nazwy z kropką, wiele zapytań), object, method (fragment, wiele metod), callers (własne, łańcuchowe, standard), refs, callees, extensions, scaffold, security, join, entity, changed, build errors, etykiety |
| `IndexFixture.cs` | buduje tymczasowy indeks raz dla wszystkich `QueryTests` |
| `MigrationTests.cs` | przejęcie instalacji xpp-graft (przeniesienie, inna lokalizacja, plik zablokowany, cel zajęty, drugie uruchomienie) i `xpplens update` (wersje, JSON wydania, SHA-256, komunikat o aktualizacji) |

Dodanie testu: włóż potrzebny XML do `Fixtures` (nazwy neutralne — `Demo*`, `Contoso*`), potem dopisz `[Fact]`
w `QueryTests.cs`, który woła zapytanie i sprawdza tekst odpowiedzi. Uruchamiaj testy przed każdym commitem.

### Wydanie nowej wersji

1. Ustaw `<Version>` w `src\XppLens\XppLens.csproj` i dopisz jej sekcję `## X.Y.Z — data` w `CHANGELOG.md`.
2. Commit, push na `main` i poczekaj na zielone CI.
3. Utwórz i wypchnij tag:

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

Workflow Release uruchamia wtedy testy, sprawdza zgodność tagu z `<Version>`, buduje `xpp-lens-X.Y.Z.zip`
przez `pack.ps1` i publikuje wydanie z opisem z `CHANGELOG.md` (podgląd: `.\release-notes.ps1 -Version X.Y.Z`).
Tagi wydań są chronione: opublikowanej wersji nie da się zmienić — poprawki idą do nowej wersji.

### Gdzie co dopisać

| Zmiana | Plik |
|---|---|
| nowe narzędzie MCP | `McpTools.cs` (deklaracja) + `Queries*.cs` (zapytanie) + polecenie CLI w `Program.cs` + test |
| inne dane z XML-a (nowy typ obiektu, właściwość, składowa) | `XmlObjectParser.cs` |
| rozpoznawanie konstrukcji X++ (wywołania, łańcuchy, atrybuty, intrinsics) | `CodeAnalyzer.cs`, `XppLexer.cs` — podnieś `Indexer.AnalyzerVersion` |
| fragmenty metod | `Fragments.cs` |
| nowa tabela lub indeks w bazie | `Store.cs` — `EnsureExtras` dla zmian w miejscu, `SchemaVersion` tylko gdy przebudowa jest nieunikniona |
| odświeżanie, obserwator, poziomy modeli | `IndexService.cs`, `Indexer.cs`, `Catalog.cs` |
| pakiety bez XML (`.xref`, `bin\*.md`, zasoby z etykietami) | `BinaryPackage.cs` |
| dziennik użycia i `stats` | `Usage.cs` |
| `xpplens update`, przejęcie xpp-graft | `Updater.cs`, `Migration.cs` |
| automatyczne wydania | `.github\workflows\release.yml`, `release-notes.ps1`, `pack.ps1` |
| polecenia CLI, konfiguracja, rejestracja w Claude | `Program.cs`, `Config.cs`, `Detect.cs` |

Pętla pracy:

```powershell
.\build.ps1                 # kompilacja do .\build
.\build\xpplens.exe find CustTable   # test z linii poleceń, bez restartu Claude'a
dotnet test                 # testy
.\build.ps1 -Deploy         # podmiana w instalacji (zatrzymuje działające procesy)
.\build.ps1 -Test -Deploy   # to samo, ale tylko gdy wszystkie testy przejdą
```

Po `-Deploy` zrestartuj Claude Desktop i sesje Claude Code — MCP ładuje binarium przy starcie.

Zmiana `Store.SchemaVersion` kasuje indeks i wymusza `xpplens build`.

Dokumentacja jest w dwóch językach: każdą zmianę w `README.pl.md` trzeba odzwierciedlić w `README.md` w tym samym commicie.

## Zgłaszanie problemów

Błędy i pomysły: załóż issue (formularz pyta o wersję i wykonane polecenie). Problemy z bezpieczeństwem: **Security → Report a vulnerability**, zob. [SECURITY.md](SECURITY.md). Nigdy nie wklejaj kodu, nazw obiektów ani danych z własnych projektów ani projektów klientów — odtwórz problem na obiektach standardowych albo neutralnych nazwach.

## Ograniczenia

- Typy odbiorców wywołań pochodzą z deklaracji zmiennych i typów zwracanych przez metody (łańcuchy); nie ma pełnego
  wnioskowania typów — np. elementy kontenerów, `this.pole.metoda()` przez pole innej klasy czy wyniki `as`/rzutowań
  w wyrażeniach zostają jako „receiver type unknown”.
- Kod Microsoftu ma tylko wywołania (bez odczytów pól, typów, etykiet); do nich awansuj pakiet do pełnego poziomu albo
  użyj `xpp_grep --standard` z filtrem modelu (czyta XML z dysku: sekundy dla małego pakietu, minuty dla
  `ApplicationSuite`, odmowa powyżej 60 tys. plików).
- Makra (`#nazwa`) nie są rozwijane. Etykieta jest rozpoznawana tylko wtedy, gdy ciąg znaków zawiera samo id etykiety.
- Pakiety skompilowane: brak kodu źródłowego; skład grup pól i właściwości obiektów nie są odtwarzane
  (format `bin\*.md` jest czytany tylko w nagłówku), a odwołania obejmują tylko to, co zapisał kompilator.
- `xpp_join` idzie tylko po relacjach tabel (nie po relacjach EDT); `xpp_changed` nie pokazuje usuniętych obiektów.
- `xpp_build_errors` przelicza linie kompilatora na linie pliku XML dla metod klas, tabel i formularzy; dla metod źródeł
  danych i kontrolek formularza pokazuje, gdzie metoda się zaczyna.
- Windows i x64 (pakiet samodzielny); indeks nie jest przenośny między maszynami — buduje się go lokalnie.
