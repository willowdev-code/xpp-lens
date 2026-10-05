# xpp-graft — indeks kodu X++ (D365 F&O) jako serwer MCP

Czyta `PackagesLocalDirectory` **tylko do odczytu** i buduje własny indeks (SQLite) poza repozytorium AOS.
Claude korzysta z niego przez MCP zamiast czytać wielkie pliki XML z AOT.

© 2026 WillowDev. Udostępnione na [licencji MIT](LICENSE).

## Instalacja na nowej maszynie

Paczki instalacyjnej nie ma w repozytorium (`dist\` jest pomijany przez `.gitignore`). Są dwie drogi:

**A. Z gotowej paczki** — na maszynie docelowej nie trzeba niczego instalować, nawet .NET.

1. Pobierz `xpp-graft-RRRRMMDD.zip` z zakładki **Releases** tego repozytorium.
2. Rozpakuj i w zwykłym PowerShellu (nie jako administrator) uruchom:

```powershell
powershell -ExecutionPolicy Bypass -File .\xpp-graft\install.ps1 -Languages en-US,pl
```

**B. Z kodu źródłowego** — wymaga .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/xpp-graft.git C:\Dev\xpp-graft
cd C:\Dev\xpp-graft
.\pack.ps1                     # tworzy dist\xpp-graft\ i dist\xpp-graft-RRRRMMDD.zip
powershell -ExecutionPolicy Bypass -File .\dist\xpp-graft\install.ps1 -Languages en-US,pl
```

Instalator kopiuje pliki do `C:\Tools\xpp-graft`, wykrywa `PackagesLocalDirectory` (z `web.config` AOS-a albo
układu katalogów), zapisuje konfigurację, rejestruje serwer w Claude Desktop i Claude Code, a na końcu buduje indeks
(modele własne ok. minuty, standard Microsoftu jednorazowo 20–40 minut). Przed instalacją zamknij Claude'a.
Po instalacji uruchom go ponownie.

Nowe wydanie: `.\pack.ps1`, a powstały ZIP dołącz do nowego wydania w zakładce Releases (tag `vX.Y.Z` zgodny
z `<Version>` w `src\XppGraft\XppGraft.csproj`).

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
xppgraft config                                   # pokaż ustawienia
xppgraft config --add-language de                 # dołóż język etykiet
xppgraft config --add-full-model XPL              # model/pakiet do pełnego indeksu
xppgraft config --add-standard-model ContosoIsv   # model do poziomu standardowego
xppgraft config --add-standard-publisher "Contoso"
xppgraft config --packages-dir K:\AosService\PackagesLocalDirectory
xppgraft detect [--set]                           # wykryj PackagesLocalDirectory
xppgraft build                                    # zastosuj zmiany
```

Ustawienia siedzą w `xppgraft.json` obok katalogu `bin` — można je też edytować ręcznie.

## Trzy poziomy indeksu

| Poziom | Modele | Co zawiera |
|---|---|---|
| pełny | wszystkie niemicrosoftowe (wykrywane po `Publisher` w deskryptorze) + te z `extraFullModels` | obiekty, składowe, sygnatury i kod metod, referencje w kodzie (wywołania, typy, pola, intrinsics, etykiety), referencje z metadanych |
| standardowy | modele Microsoftu | obiekty, pola/indeksy/relacje, sygnatury metod z zakresami linii, `extends`, CoC i event handlery — bez referencji z ciał metod |
| skompilowany | pakiety wdrożone bez XML (np. lokalizacje krajowe albo moduły ISV dostarczone tylko w postaci skompilowanej) | nazwy obiektów z `bin\*.md`, metody, pola, grupy pól, relacje i **odwołania z kompilatora** z `.xref`, CoC z `ChainOfCommand.xml`, dziedziczenie z `ClassExtends.runtime`, etykiety z `Resources\<język>\*.resources.dll` — bez kodu źródłowego |

Pakiet standardowy można awansować do pełnego (`--add-full-model ApplicationSuite`), jeśli potrzebny jest tam
graf wywołań i natychmiastowy `xpp_grep`. Kosztem jest dłuższa budowa i większa baza.

Pakiety skompilowane przebudowują się same, gdy zmieni się ich `.xref`, `.md` albo zasoby. Ręcznie:
`xppgraft build --compiled-only --force`. Katalogi, których nie da się zaindeksować wcale, wypisuje `xppgraft status`
w linii „on disk but NOT indexed”.

## Uprawnienia do indeksu

Domyślnie indeks trafia do `%LOCALAPPDATA%\xpp-graft\index\xpp.db`, czyli tam, gdzie użytkownik uruchamiający
Claude'a ma prawo zapisu. Jeśli wskażesz indeks w katalogu instalacji, a instalację przeprowadzisz jako administrator,
Claude (działający bez podniesionych uprawnień) nie będzie mógł go aktualizować — zobaczysz wtedy w odpowiedziach
komunikat `index is read-only`, a wyniki zamarzną na stanie z chwili budowy. Naprawa:

```powershell
xppgraft config --index-path "$env:LOCALAPPDATA\xpp-graft\index\xpp.db"
xppgraft build
```

W trybie tylko do odczytu `xpp_method` i `xpp_object` i tak zwracają aktualny kod (parsują plik na żywo),
ale `xpp_find`, `xpp_callers` i `xpp_refs` korzystają z zamrożonego indeksu.

## Aktualność indeksu

- `FileSystemWatcher` na katalogach modeli pełnego poziomu — zapis z Visual Studio widoczny przy następnym zapytaniu.
- Skan dat plików przy starcie i co `rescanIntervalSeconds` (domyślnie 5 min) — wyłapuje Get Latest z Team Explorera.
- Standard: odcisk pakietu (deskryptory + `bin\*.dll`) — przebudowa tylko po aktualizacji platformy.
- Zapisy serializowane nazwanym muteksem, więc Claude Desktop i Claude Code mogą działać równolegle.

## Narzędzia MCP

| Narzędzie | Zastosowanie | Poziom standardowy |
|---|---|---|
| `xpp_find` | obiekty, metody, pola po nazwie (`*`, `?`, `Obiekt.składowa`; nazwa z kropką sprawdzana też jako pełna nazwa obiektu, np. `*Staging.Contoso` z `type=tableext`) | tak |
| `xpp_object` | szkielet obiektu: właściwości z etykietami, pola, indeksy, relacje, datasource'y, drzewo kontrolek i menu, metody z zakresami linii, rozszerzenia | tak |
| `xpp_method` | kod metody + ścieżka i zakres linii + wrappery CoC i handlery | tak |
| `xpp_extensions` | klasy CoC, rozszerzenia tabel/formularzy, event handlery, klasy pochodne | tak |
| `xpp_callers` / `xpp_callees` | kto wywołuje metodę / czego używa metoda | nie (tylko CoC i handlery) |
| `xpp_refs` | wszystkie użycia klasy, tabeli, pola, EDT, enuma, pozycji menu, etykiety | nie |
| `xpp_grep` | regex po ciałach metod | `standard=true` + filtr modelu (czyta pliki z dysku) |
| `xpp_label` | rozwiązanie `@SYS…`/`@Model:Klucz` albo szukanie po tekście | tak |
| `xpp_status` | stan indeksu | — |

### Drzewo kontrolek i menu

Duże drzewa (ponad 60 elementów) są zwijane do dwóch poziomów z licznikiem dzieci `[+N]`. Rozwijanie:

| Parametr | Działanie | Przykład |
|---|---|---|
| `filter` | wildcard po nazwie lub ścieżce; wypisuje **pełne ścieżki** | `xppgraft object CustTable --type form --filter *PersonalTitle*` |
| `parent` | tylko poddrzewo danego elementu (nazwa albo ścieżka) | `--parent TabGeneral` |
| `depth` | liczba poziomów (pod `parent`, jeśli podany) | `--parent UpperGroup --depth 1` |

Kontrolki ReferenceGroup pokazują `ref=<datasource>.<ReferenceField>`, `replGroup=<ReplacementFieldGroup>`
i `relPath=<DataRelationPath>`. Elementy rozszerzeń menu pokazują `(under <Parent>)`, `position=<PositionType>`
i `menuitem=<MenuItemName>`.

## CLI

```
xppgraft find|object|method|callers|callees|refs|ext|grep|label …
xppgraft build [--full-only] [--std-only] [--compiled-only] [--force]
xppgraft status | detect | config | register | unregister | mcp
```

Zmienne: `XPPGRAFT_CONFIG` (inna konfiguracja), `XPPGRAFT_VERBOSE=1` (czasy zapytań SQL na stderr).

## Rozwój narzędzia

Kod źródłowy mieszka **osobno od instalacji**, domyślnie w `C:\Dev\xpp-graft`:

```
C:\Dev\xpp-graft\
  xpp-graft.sln             solucja dla Visual Studio 2022
  src\XppGraft\*.cs         kod (14 plików)
  src\XppGraft\Properties\launchSettings.json   profile uruchomieniowe (F5)
  build.ps1                 kompilacja; -Deploy podmienia binaria w instalacji
  pack.ps1                  pakiet ZIP do instalacji gdzie indziej
  install.ps1 uninstall.ps1 README.md
  build\  dist\             wyniki (nie trzymaj tu niczego własnego)
```

### Visual Studio

Otwórz `C:\Dev\xpp-graft\xpp-graft.sln` w **Visual Studio 2022** (17.12 lub nowszym — VS 2019 nie obsługuje .NET 9).
Na pasku narzędzi obok zielonej strzałki wybierz profil z `launchSettings.json` (`status`, `find`,
`object (form controls)`, `method`, `build compiled packages`, `verbose SQL (status)`) i naciśnij F5 —
program uruchomi się z debuggerem na prawdziwej konfiguracji i indeksie (`XPPGRAFT_CONFIG` jest ustawione w profilu).
Własny profil: Debug → *XppGraft Debug Properties* → nowy profil, w „Command line arguments” wpisz polecenie CLI.

Serwera MCP nie debuguje się przez F5 (rozmawia przez stdin/stdout z Claude'em). Żeby podejrzeć go w działaniu,
wdroż wersję Debug (`.\build.ps1 -Deploy -Configuration Debug`), zrestartuj Claude'a i w VS użyj
Debug → *Attach to Process* → `xppgraft.exe`.

Gdzie co dopisać:

| Zmiana | Plik |
|---|---|
| nowe narzędzie MCP | `McpTools.cs` (deklaracja) + `Queries.cs` (zapytanie) |
| inne dane z XML-a (nowy typ obiektu, właściwość, składowa) | `XmlObjectParser.cs` |
| rozpoznawanie konstrukcji X++ (wywołania, atrybuty, intrinsics) | `CodeAnalyzer.cs`, `XppLexer.cs` |
| nowa tabela lub indeks w bazie | `Store.cs` — podnieś `SchemaVersion`, co wymusi przebudowę |
| odświeżanie, obserwator, poziomy modeli | `IndexService.cs`, `Indexer.cs`, `Catalog.cs` |
| pakiety bez XML (`.xref`, `bin\*.md`, zasoby z etykietami) | `BinaryPackage.cs` |
| polecenia CLI, konfiguracja, rejestracja w Claude | `Program.cs`, `Config.cs`, `Detect.cs` |

Pętla pracy:

```powershell
.\build.ps1                 # kompilacja do .\build
.\build\xppgraft.exe find CustTable   # test z linii poleceń, bez restartu Claude'a
.\build.ps1 -Deploy         # podmiana w instalacji (zatrzymuje działające procesy)
```

Po `-Deploy` zrestartuj Claude Desktop i sesje Claude Code — MCP ładuje binarium przy starcie.

Zmiana `Store.SchemaVersion` kasuje indeks i wymusza `xppgraft build`.

## Ograniczenia

- Typy odbiorników wywołań rozwiązywane są z deklaracji zmiennych, bez pełnej analizy typów; łańcuchy `a.b().c()`
  trafiają do sekcji „receiver type unknown” w `xpp_callers`.
- `xpp_grep --standard` czyta XML-e z dysku: mały pakiet to sekundy, `ApplicationSuite` to minuty (albo odmowa
  przy ponad 60 tys. plików). Rozwiązanie: filtr `type`/`object` albo awans pakietu do pełnego poziomu.
- Makra (`#nazwa`) nie są rozwijane.
- Pakiety skompilowane: brak kodu źródłowego; skład grup pól i właściwości obiektów nie są odtwarzane
  (format `bin\*.md` jest czytany tylko w nagłówku), a odwołania obejmują tylko to, co zapisał kompilator.
- Windows i x64 (pakiet samodzielny); indeks nie jest przenośny między maszynami — buduje się go lokalnie.
