# Filesize-Checker Funktionsplan

Der Filesize-Checker ist ein lokales Windows-Tool der CheckTools-Reihe. Er erfasst Dateibestände, zeigt Speicherverbrauch und Änderungen, findet exakte Kopien und hilft, Dateien nach nachvollziehbaren Regeln auf der Festplatte zu organisieren. Als technischer Rahmen dient C# mit .NET 10 und Windows Forms wie bei WinCheck.

## Version 1

### Inventar und Speicheranalyse

- Ein lokal gespeichertes Inventar umfasst einen oder mehrere Scan-Ordner auf einem oder mehreren Laufwerken. Der Scan erfasst Pfad, Dateityp, Größe, Zeitstempel und verfügbare Metadaten. Größen und Dateianzahlen lassen sich pro Ordner, Dateityp und Größenklasse auswerten. Die größten Dateien und Ordner sind direkt auffindbar. Das Inventar ist als CSV exportierbar.
- Ein Folgescan vergleicht jeden Bereich mit seinem letzten erfolgreich erfassten Stand. Die Inventaransicht bietet direkt anklickbare Filter **Neu**, **Fehlend** und **Verändert** mit Trefferzahlen. Mehrere Status sind gemeinsam auswählbar und mit Ordner-, Namens-, Typ-, Größen- und Datumsfiltern kombinierbar. Die Dateidetails zeigen die erkannten Änderungen.
- **Fehlend** setzt voraus, dass der betreffende Bereich erfolgreich erneut erfasst wurde. Ein ausgefallenes Laufwerk oder ein unlesbarer Bereich wird als **Nicht erreichbar** beziehungsweise **Scan unvollständig** angezeigt. Für betroffene Bereiche bleibt der bisherige Stand erhalten. Eine beim Inventarscan beobachtete Metadatenänderung und eine bestätigte Inhaltsänderung sind als solche erkennbar.

### Exakte Kopien

- Die Duplikatsuche startet separat, zeigt Fortschritt und lässt sich pausieren und fortsetzen. Sie gruppiert Dateien zunächst nach der exakten Bytezahl. Nur Gruppen mit mehreren Dateien kommen für Inhaltsprüfungen infrage. Die Suche berechnet SHA-256-Prüfwerte und bestätigt gemeldete Kopien byteweise.
- Die Suche speichert abgeschlossene Prüfwerte lokal. Sie verwendet sie wieder, wenn Dateikennung, Größe und verfügbare Änderungsmerkmale dies zulassen. Mit **Alle Prüfwerte neu berechnen** lässt sich die Wiederverwendung für einen Lauf abschalten. Ändert sich eine Datei während der Prüfung, wird ihr Ergebnis verworfen und die Datei kann erneut geprüft werden.
- Die Trefferliste zeigt alle Pfade und den potenziell freigebbaren Speicher einer Gruppe. Verweisen mehrere Hardlinks auf dieselbe Datei, zählt deren Speicherbelegung einmal. Lesefehler werden am betroffenen Eintrag ausgewiesen.

### Sortierregeln und einfache Namensähnlichkeit

- Speichern und kombinieren lassen sich Regeln nach Dateityp, Datum, Größe, Namensmuster und verfügbaren Metadaten. Bei Datumsregeln sind Datumsquelle und Ausweichregel für fehlende Angaben erkennbar. Regeln lassen sich auf die aktuelle Auswahl oder den gesamten Bestand anwenden.
- Einfache Namensähnlichkeit ist bereits in Version 1 ein Sortierkriterium. Das Tool erkennt übliche Trennzeichen und eindeutige Kopier- oder Versionszusätze. Es schlägt beispielsweise für `Modell.safetensors`, `Modell (1).safetensors` und `Modell_v2.safetensors` einen gemeinsamen Zielordner vor. Der Sortierplan zeigt den erkannten Bezug. Dateien lassen sich einzeln aus der Gruppe nehmen und Zielordner ändern.
- Standardansichten sind **Inventar**, **Duplikate** und **Sortierplan**. Der Sortierplan zeigt für jede Datei Quelle, endgültiges Ziel und die geplante Aktion. Die Ausführung beginnt nach Freigabe des Plans.

### Namenskollisionen

Die Vorschau erkennt vorhandene Zieldateien und Kollisionen zwischen Einträgen desselben Plans. Sie zeigt für Quelle und Ziel Pfad, Größe und Änderungsdatum. Die Aktionen gelten je Konflikt oder für alle ausgewählten Konflikte.

| Aktion | Verhalten |
| --- | --- |
| Überspringen | Der geplante Vorgang entfällt. |
| Ersetzen | Die Zieldatei wird nach ausdrücklicher Freigabe ersetzt. |
| Beide behalten | Die Quelldatei erhält einen freien, nummerierten Namen wie `Modell (2).safetensors`. |
| Präfix oder Suffix | Ergänzt den Namen vor der Dateiendung, etwa `Archiv_Modell.safetensors` oder `Modell_alt.safetensors`. Beide Zusätze lassen sich kombinieren. Bei erneutem Konflikt ist eine zusätzliche Nummerierung möglich. |

Die Vorschau zeigt jeden endgültigen Namen. Ein bei der Ausführung neu auftretender Konflikt braucht vor einem Ersetzen eine neue Entscheidung. Die Auswahl orientiert sich am Windows-Dateidialog. [Microsoft-Leitfaden zum Dateikonfliktdialog](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)

### Verschieben mit Vorabprüfung und Rückgängig

Version 1 unterstützt Kopieren und **Verschieben mit Rückgängig**. Jeder Verschiebeplan durchläuft vor der Freigabe eine Vorabprüfung. Sie prüft Erreichbarkeit, erforderliche Zugriffsrechte, verfügbare Quellen, gültige Zielpfade, Konflikte und den erforderlichen freien Speicher je Ziellaufwerk. Zum Platzbedarf zählen auch temporäre Kopien und Sicherungen für Rückgängig. Blockierte Einträge müssen vor dem Start korrigiert oder ausdrücklich aus dem Plan genommen werden.

Unmittelbar vor jeder Aktion prüft das Tool erneut, ob Quelle und Ziel dem freigegebenen Stand entsprechen. Eine Vorabprüfung gilt für den geprüften Zustand. Ändert sich dieser bis zur Ausführung, stoppt das Tool den betroffenen Vorgang und zeigt den Grund an.

- **Auf demselben Laufwerk** nutzt das Tool, wo das Dateisystem es unterstützt, eine atomare Umbenennung an den Zielpfad.
- **Über Laufwerksgrenzen** erstellt das Tool zuerst eine temporäre Kopie am Ziel und prüft deren Inhalt gegen die Quelle. Es entfernt die Quelle erst, nachdem das endgültige Ziel erfolgreich erstellt, überprüft und im Wiederherstellungsprotokoll erfasst wurde und die Quelle weiterhin dem geprüften Stand entspricht. Scheitert die Quellenentfernung, weist das Tool den Zwischenstand mit beiden Pfaden aus.
- **Beim Ersetzen** sichert das Tool den vorhandenen Zielinhalt vor der Änderung, damit Rückgängig auch die ursprüngliche Zieldatei wiederherstellen kann.

Ein dauerhaftes Protokoll hält vor jedem Eingriff den Ausgangszustand und anschließend das Ergebnis fest. Nach einem Abbruch oder Neustart gleicht das Tool offene Vorgänge mit dem tatsächlichen Dateibestand ab. Rückgängig prüft den aktuellen Zielzustand, die Verfügbarkeit benötigter Sicherungen und den ursprünglichen Quellpfad. Ist der Quellpfad inzwischen belegt oder wurde die Zieldatei verändert, stoppt die Rücknahme für diesen Eintrag und zeigt den Konflikt an. Bei laufwerksübergreifender Rücknahme wird die wiederhergestellte Quelle geprüft, bevor die Datei am Ziel entfernt wird.

Abgeschlossene Verschiebeläufe bleiben auch nach einem Neustart rückgängig machbar, solange ihre Voraussetzungen erfüllt sind. Das Tool zeigt den Speicherverbrauch der Sicherungen an. Vor dem Freigeben von Speicher zeigt es, welche Läufe dadurch ganz oder teilweise ihre Rückgängig-Funktion verlieren, und wartet auf Bestätigung.

Bei einem unerwarteten Fehler pausiert das Tool den restlichen Sortierlauf. Es zeigt abgeschlossene, ausstehende und teilweise abgeschlossene Aktionen. Sobald der tatsächliche Zustand eines teilweise abgeschlossenen Vorgangs geklärt ist, kann der Nutzer den Lauf fortsetzen oder abgeschlossene Verschiebungen zurücknehmen. Beim Fortsetzen prüft das Tool die ausstehenden Einträge erneut.

## Weitere Ausbaustufen

1. **Namens- und Versionsvergleich.** Unscharfe Namensähnlichkeit für Tippfehler und abweichende Wortreihenfolgen ergänzen. Dateifamilien mit nachvollziehbaren Hinweisen bilden, interne Versionsangaben etwa bei EXE, DLL und Installationspaketen auslesen und Inhaltsunterschiede für ausgewählte Office-, PDF- und Textformate sichtbar machen. Widersprüchliche Hinweise anzeigen und uneindeutige Versionsreihenfolgen offenlassen. Zwei beliebige Inventarstände oder Ordnerbestände gegenüberstellen und mögliche Verschiebungen oder Umbenennungen anhand übereinstimmender Inhalte kennzeichnen.
2. **Bild- und Videovergleich.** Bei Bildern zunächst Größen-, Format- und Kompressionsvarianten erkennen, danach Drehungen, Zuschnitte und ähnliche Serienbilder. Bei Videos umkodierte Vollkopien, gekürzte Fassungen und überlappende Ausschnitte erkennen. Für Ausschnitte die passenden Zeitbereiche anzeigen. Den Vergleich später mit einem separaten synthetischen Medienbestand prüfen.

Die Oberfläche zeigt den Grund für jeden Treffer. Namensähnlichkeit, Hinweise auf eine Versionsbeziehung, visuelle Ähnlichkeit und bestätigte Bytegleichheit haben jeweils eigene Befunde.

## Abnahme von Version 1

- Einen kontrollierten Testbestand zweimal scannen. Die Filter finden neu angelegte, entfernte und geänderte Dateien mit korrekten Trefferzahlen. Ein unterbrochener oder nicht erreichbarer Scan-Bereich erzeugt keine fälschlichen Fehlend-Treffer.
- Gleich große unterschiedliche Dateien, bestätigte Kopien mit abweichenden Namen oder Endungen und Hardlinks prüfen. Wiederverwendung, Neuberechnung und Fortsetzen der Duplikatsuche nachvollziehen.
- Eine einfache Namensfamilie mit einzeln abwählbaren Dateien und änderbarem Zielordner im Sortierplan anzeigen. Alle Kollisionsaktionen einschließlich Präfix und Suffix, Mehrfachauswahl, Kollisionen innerhalb desselben Plans und später belegte Ziele mit Testdateien prüfen.
- Kopieren sowie Verschieben und Rückgängig auf demselben und über zwei Laufwerke prüfen. Als Fehlerfälle gesperrte Dateien, fehlende Rechte, zu wenig Speicher, Änderungen nach der Vorschau, fehlgeschlagene Kopierprüfungen und einen unterbrochenen Vorgang mit Neustart nachstellen. Beim Ersetzen muss Rückgängig sowohl die ursprüngliche Quelle als auch den vorherigen Zielinhalt wiederherstellen. Ein inzwischen belegter Quellpfad oder ein verändertes Ziel muss als Konflikt sichtbar sein.
- Mit einem Fehler in der Mitte eines Sortierlaufs prüfen, dass die restlichen Aktionen pausieren und ihre Zustände erkennbar bleiben. Fortsetzen mit erneuter Prüfung sowie die Rücknahme abgeschlossener Verschiebungen nach einem Neustart prüfen. Die Vorschau zum Freigeben von Sicherungsspeicher muss die betroffenen Läufe und den Verlust der Rückgängig-Funktion sichtbar machen.
- `<Testordner>` als lesenden Praxistest für Inventar, Speicheranalyse, Duplikatsuche und Sortiervorschau verwenden. Für ausgeführte Dateiaktionen einen separaten Testbestand verwenden.
