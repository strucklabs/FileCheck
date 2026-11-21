# Filesize-Checker

Der Filesize-Checker ist ein lokales Windows-Werkzeug der CheckTools-Reihe. Er erfasst Dateibestände, zeigt Speicherverbrauch und Änderungen, findet Kopien und erstellt nachvollziehbare Sortierpläne. Die Anwendung ist in Entwicklung. Der [Projektplan](PROJEKTPLAN.md) beschreibt Ausbau und Abnahmekriterien. Die dort genannten künftigen Funktionen gehören erst nach ihrer Umsetzung zum Programm.

## Funktionen

- **Inventar und Speicheranalyse** scannen ausgewählte Ordner und zeigen Dateitypen, Größen und Speicherverbrauch. Folgescans vergleichen die erfassten Bereiche mit dem letzten erfolgreichen Stand. Die kombinierbaren Filter **Neu**, **Fehlend** und **Verändert** zeigen Trefferzahlen. Die Inventarliste ist als CSV exportierbar.
- **Duplikate** prüft Kandidaten nach Dateigröße, Stichproben bei großen Dateien, SHA-256 und Bytevergleich. Bestätigte Kopien haben denselben Inhalt. Bei der Schätzung des freigebbaren Speichers berücksichtigt die Anwendung Hardlinks. Die Prüfung hat Fortschrittsanzeige, Pause, Abbruch und eine Option zur Neuberechnung.
- **Namen und Versionen** findet Namensfamilien anhand erkennbarer Kopier- und Versionszusätze und prüft interne Versionsangaben bei ausgewählten Formaten. Für unterstützte Text-, Office-, OpenDocument- und PDF-Dateien kann die Ansicht auslesbare Inhalte vergleichen.
- **Bildvergleich** zeigt pixelgleiche und visuell ähnliche JPEG-, PNG- und WebP-Einzelbilder. Möglich sind Direktvergleich, Referenzsuche und Gruppensuche. Fehlzuordnungen lassen sich für unveränderte Bilder ausblenden.
- **Sammlung und Sortierplan** übernehmen ausgewählte Dateien per Kontextmenü oder Drag-and-drop. Die Vorschau zeigt das endgültige Ziel und die geplante Aktion je Datei. Namenskonflikte lassen sich überspringen, ersetzen, nummerieren oder mit Präfix und Suffix lösen. Nach Vorabprüfung und Freigabe kann der Plan kopieren oder verschieben.
- **Sortierläufe** zeigen den Zustand ausgeführter Aktionen und unterstützen Fortsetzen, Rückgängig und die Freigabe von Sicherungen. Rückgängig setzt prüfbare Pfade und die benötigten Sicherungen voraus.

## Voraussetzungen und Start

Erforderlich sind Windows, das **.NET-10-SDK** für den Build und die **.NET Desktop Runtime 10** zum Start eines frameworkabhängigen Builds ohne SDK. Die NuGet-Pakete werden beim Wiederherstellen bezogen.

```powershell
dotnet restore .\FilesizeChecker.csproj
dotnet build .\FilesizeChecker.csproj -c Release --no-restore
dotnet run --project .\FilesizeChecker.csproj -c Release --no-build
```

Wähle im Tab **Inventar** mindestens einen Ordner und klicke auf **Scan starten**. Lege zum Ausprobieren einen separaten Testordner mit einigen synthetischen Dateien an. Für Schreibaktionen prüfst du anschließend unter **Sortierplan** Quelle, Ziel und Konfliktentscheidungen und bestätigst unter **Prüfen und ausführen …** den geprüften Plan.

## Prüfung

Der Quellbaum enthält kein versioniertes automatisches Testprojekt. Ein reproduzierbarer manueller Kurztest ist ein erster Scan eines Testordners, gefolgt von je einer hinzugefügten, veränderten und entfernten Beispieldatei und einem zweiten Scan. Ergänze zwei bytegleiche Dateien und eine gleich große Datei mit anderem Inhalt, um die Duplikatsuche zu prüfen. Verwende für Vorabprüfung, Kopieren, Verschieben, Ersetzen und Rückgängig einen entbehrlichen Testbestand. Weitere Abnahmeszenarien stehen im [Projektplan](PROJEKTPLAN.md).

## Struktur

| Pfad | Inhalt |
| --- | --- |
| `Program.cs` | Start der Anwendung und lokaler Datenordner |
| `InventoryForm*.cs`, `FileDragGrid.cs`, `ImagePreviewStrip.cs` | Windows-Forms-Oberfläche und Ansichten |
| `Inventory/` | Scan, Inventar, Vergleiche, Sortierplanung und Dateivorgänge |
| `PROJEKTPLAN.md`, `AGENTS.md` | Funktionsplan und Projektanweisungen |
| `bin/`, `obj/` | Ignorierte Build-Ausgaben und lokale Prüfarbeitsstände. Ältere Varianten in diesen Ordnern sind lokale Zwischenstände. |

Der Ordnername des Arbeitsverzeichnisses kann vom Projektnamen abweichen. Maßgeblich für den Build ist `FilesizeChecker.csproj`.

## Datenhaltung

Die Anwendung speichert ihre Daten in `%LOCALAPPDATA%\CheckTools\FilesizeChecker\`. Dazu gehören `inventory.json` mit Dateipfaden und Metadaten, `duplicate-hashes.json` mit Inhaltsprüfdaten, `image-features-v1.json` und `image-exclusions-v1.json` für Bildbefunde, `sort-rules.json` für Regeln und `sort-runs\` mit Quell- und Zielpfaden sowie Wiederherstellungszuständen.

CSV-Exporte liegen am beim Export gewählten Ort. Dateivorgänge können neben dem Ziel temporäre Dateien und Sicherungen mit dem Präfix `.filesize-checker-` ablegen. Verwende **Sortierläufe → Sicherungen freigeben …**, um nicht mehr benötigte Sicherungen nach Prüfung der Folgen zu entfernen. Schließe die Anwendung vor dem Löschen lokaler Inventar- oder Cache-Dateien. Entferne Laufprotokolle und Sicherungen erst, wenn offene Vorgänge geklärt sind und du die betreffenden Läufe nicht mehr rückgängig machen willst. CSV-Exporte löschst du am gewählten Speicherort.

Scans, Dateiinhalte, Vergleiche und Vorschauen verarbeitet die Anwendung lokal. Dateien können durch freigegebene Sortieraktionen geändert werden. Vor einer Weitergabe von Inventaren, Exporten, Laufprotokollen oder Screenshots sollte ihr Inhalt geprüft werden.

## Einschränkungen

- Unvollständige oder unerreichbare Scanbereiche behalten ihren letzten erfolgreich erfassten Stand. Der Scan meldet solche Bereiche in den Hinweisen.
- Der Bildvergleich unterstützt JPEG, PNG und WebP bis 48 Megapixel. Animierte, defekte und zu große Dateien werden gesondert gemeldet. Visuelle Ähnlichkeit ist ein Hinweis mit möglichen Fehlzuordnungen.
- Dokumentvergleiche werten je nach Format bestimmte Inhalte aus. Bildbasierte PDFs benötigen OCR. Der eingebundene PDF-Vergleich liest vorhandenen Seitentext. Für Textdateien und extrahierten PDF-Text gilt eine Grenze von 12 MiB, für PDFs zusätzlich eine Grenze von 1.000 Seiten.
- Vorabprüfung und Rückgängig hängen vom aktuellen Dateizustand, den Zugriffsrechten, dem Dateisystem und dem verfügbaren Speicher ab.

## Lizenz und Drittanbieter

Für den Filesize-Checker selbst ist im Repository bisher keine Projektlizenz festgelegt. Eine Lizenzfreigabe zur Weiterverwendung des Projektcodes ist damit nicht dokumentiert.

- **PdfPig 0.1.16** für PDF-Metadaten und PDF-Text, Apache-2.0. [NuGet-Paket](https://www.nuget.org/packages/PdfPig/0.1.16)
- **SkiaSharp 3.119.4** für Bildverarbeitung, MIT. [NuGet-Paket](https://www.nuget.org/packages/SkiaSharp/3.119.4)
- Das native Windows-Paket von SkiaSharp liefert außerdem separate Drittanbieterhinweise mit. Lizenztexte und Hinweise der tatsächlich enthaltenen Paketbestandteile sind bei einem eigenständigen Binärpaket mitzuführen. [Natives Windows-Paket](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Win32/3.119.4)
