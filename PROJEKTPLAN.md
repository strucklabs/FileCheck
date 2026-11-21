# FileCheck Funktionsplan

FileCheck ist ein lokales Windows-Tool der CheckTools-Reihe. Es erfasst Dateibestände, zeigt Speicherverbrauch und Änderungen, findet exakte Kopien und hilft, Dateien nach nachvollziehbaren Regeln auf der Festplatte zu organisieren. Als technischer Rahmen dient C# mit .NET 10 und Windows Forms wie bei WinCheck.

## Version 1

### Inventar und Speicheranalyse

- Ein lokal gespeichertes Inventar umfasst einen oder mehrere Scan-Ordner auf einem oder mehreren Laufwerken. Beim Hinzufügen lassen sich im Windows-Dialog mehrere Ordner auf einmal auswählen. Bereits erfasste oder überlappende Ordner werden mit einem Hinweis übersprungen. Der Scan erfasst Pfad, Dateityp, Größe, Zeitstempel und verfügbare Metadaten. Größen und Dateianzahlen lassen sich pro Ordner, Dateityp und Größenklasse auswerten. Die größten Dateien und Ordner sind direkt auffindbar. Das Inventar ist als CSV exportierbar.
- Aus der Speicheranalyse öffnet **Dateien anzeigen** die passende Inventarliste zum Ziehen einzelner Dateien oder markierter Zeilen in die Sammlung. Ganze Gruppen lassen sich in der Speicheranalyse über das Kontextmenü sammeln. Es zeigt die Dateizahl und bei Ordnern den Hinweis **einschließlich Unterordnern**.
- Ein Folgescan vergleicht jeden Bereich mit seinem letzten erfolgreich erfassten Stand. Die Inventaransicht bietet direkt anklickbare Filter **Neu**, **Fehlend** und **Verändert** mit Trefferzahlen. Mehrere Status sind gemeinsam auswählbar und mit Ordner-, Namens-, Typ-, Größen- und Datumsfiltern kombinierbar. Die Dateidetails zeigen die erkannten Änderungen.
- Die Leiste über der Inventarliste zeigt alle aktiven Filter einschließlich der Auswahl aus der Speicheranalyse. Ohne Auswahl zeigt sie **Keine Filter aktiv**. Rechts in der Leiste setzt **Filter zurücksetzen** alle Filter zurück.
- **Fehlend** setzt voraus, dass der betreffende Bereich erfolgreich erneut erfasst wurde. Ein ausgefallenes Laufwerk oder ein unlesbarer Bereich wird als **Nicht erreichbar** beziehungsweise **Scan unvollständig** angezeigt. Für betroffene Bereiche bleibt der bisherige Stand erhalten. Eine beim Inventarscan beobachtete Metadatenänderung und eine bestätigte Inhaltsänderung sind als solche erkennbar.

### Exakte Kopien

- Die Duplikatsuche startet separat, lässt sich pausieren, fortsetzen oder abbrechen und zeigt die aktuelle Prüfphase, die gelesene Datenmenge und das durchschnittliche Lesetempo. Sie gruppiert Dateien zunächst nach der exakten Bytezahl. Bei großen Dateien nutzt sie Stichproben am Anfang, in der Mitte und am Ende, um unterschiedliche Inhalte vor der vollständigen Prüfung auszusortieren. Die Suche gruppiert verbleibende Kandidaten per SHA-256 und bestätigt Kopien byteweise. Stichproben, SHA-256-Prüfwerte und bestätigte Bytevergleiche lassen sich bei unveränderten Dateien wiederverwenden. Eine vollständige Neuberechnung ist möglich.
- Die Suche speichert abgeschlossene Prüfwerte lokal. Sie verwendet sie wieder, wenn Dateikennung, Größe und verfügbare Änderungsmerkmale dies zulassen. Mit **Alle Prüfwerte neu berechnen** lässt sich die Wiederverwendung für einen Lauf abschalten. Ändert sich eine Datei während der Prüfung, wird ihr Ergebnis verworfen und die Datei kann erneut geprüft werden.
- Die Trefferliste zeigt alle Pfade und den potenziell freigebbaren Speicher einer Gruppe. Verweisen mehrere Hardlinks auf dieselbe Datei, zählt deren Speicherbelegung einmal. Lesefehler werden am betroffenen Eintrag ausgewiesen.

### Sortierregeln und einfache Namensähnlichkeit

- Speichern und kombinieren lassen sich Regeln nach Dateityp, Datum, Größe, Namensmuster und verfügbaren Metadaten. Bei Datumsregeln sind Datumsquelle und Ausweichregel für fehlende Angaben erkennbar. Regeln lassen sich auf die aktuelle Auswahl oder den gesamten Bestand anwenden.
- **Direkt in den Zielordner** legt Dateien beim Kopieren oder Verschieben ohne automatisch erzeugte Unterordner ab. Vorschau, Namenskollisionen, Vorabprüfung und Freigabe gelten auch für die direkte Ablage.
- Einfache Namensähnlichkeit ist bereits in Version 1 ein Sortierkriterium. Das Tool erkennt übliche Trennzeichen und eindeutige Kopier- oder Versionszusätze. Es schlägt beispielsweise für `Modell.safetensors`, `Modell (1).safetensors` und `Modell_v2.safetensors` einen gemeinsamen Zielordner vor. Der Sortierplan zeigt den erkannten Bezug. Dateien lassen sich einzeln aus der Gruppe nehmen und Zielordner ändern.
- Standardansichten sind **Inventar**, **Duplikate** und **Sortierplan**. Der Sortierplan zeigt für jede Datei Quelle, endgültiges Ziel und die geplante Aktion. Die Ausführung beginnt nach Freigabe des Plans.

### Dateien für den Sortierplan sammeln

Eine feste Ablage unter den Ergebnislisten nimmt markierte Dateizeilen, vollständige Duplikat- oder Namensgruppen und Dateien aus dem Windows-Explorer an. Mehrfach hinzugefügte Pfade erscheinen einmal mit ihren jeweiligen Befunden. Beim Weitersammeln oder Entfernen von Einträgen aktualisiert sich die Vorschau der Sammlung. Ausschlüsse, eigene Ziele und weiterhin gültige Konfliktentscheidungen für bestehende Dateien bleiben erhalten. Das Ablegen und Übernehmen sammelt Verweise. Dateiaktionen erfolgen erst über die Vorschau, Vorabprüfung und Freigabe des Plans.

Ziehgriffe kennzeichnen Dateien und Gruppen in den Listen. Das Kontextmenü übernimmt markierte Dateien oder die vollständige Gruppe in die Sammlung.

### Bildvergleich

- Der Tab **Bildvergleich** bietet einen Direktvergleich mehrerer Dateien mit einer gemeinsamen Referenz, eine Suche mit Referenzbild und eine Gruppensuche in ausgewählten Inventarordnern einschließlich ihrer Unterordner. Er unterstützt JPEG, PNG und WebP als Einzelbilder bis 48 Megapixel.
- Die Suche berücksichtigt die gespeicherte Ausrichtung. Sie meldet gleiche normalisierte Pixel als **Pixelgleich** und mögliche Größen-, Format- oder Kompressionsvarianten als **Visuell ähnlich**. Die strengere Suche ist voreingestellt. **Weitere ähnliche Treffer** erweitert den Suchbereich.
- Für die automatische Referenzauswahl zählt zuerst die Pixelzahl. Bei identischen Bildmaßen haben verlustfrei codierte Dateien Vorrang. Danach entscheidet das ältere Änderungsdatum. Bei gleichem Datum und gleichem verlustbehaftetem Format dient die größere Datei als nachrangiges Auswahlkriterium. In jedem Suchmodus lässt sich das Referenzbild über die Trefferliste oder die Vorschauleiste ändern. Der Bildvergleich berechnet die Befunde der vorhandenen Gruppe für das neue Referenzbild neu.
- Die Vorschau zeigt die Bilder der gewählten Gruppe nebeneinander in einer horizontal scrollbaren Leiste. Die Auswahl in der Trefferliste hebt das Bild hervor und scrollt es ins Sichtfeld. Vom Befundfilter ausgeblendete Bilder bleiben ausgegraut sichtbar. Zu den Bildern stehen Bildmaße, Format, Dateigröße und Kompressionsart. Ein Alphakanal oder eine angewandte Ausrichtung wird bei Bedarf angezeigt. Der vollständige Pfad steht beim Überfahren des Dateinamens zur Verfügung. Dateien und Gruppen lassen sich in die Sammlung übernehmen.
- Die Bildtreffer lassen sich nach Befund und nach einer Mindestzahl visuell ähnlicher Treffer je Gruppe filtern. Mehrere Befunde sind kombinierbar. **Kein Bildtreffer** steht bei Ergebnissen des Direktvergleichs zur Verfügung. Die Sammelaktion übernimmt die gefilterten Bilder aus allen verbleibenden Gruppen. Beim Sammeln einer einzelnen Gruppe gilt dieselbe Befundauswahl.
- Markierte visuell ähnliche Treffer lassen sich als Fehlzuordnung ausschließen. Ausschlüsse bleiben für dasselbe Bildpaar mit unveränderten Pixeln gespeichert. Ausgeschlossene Treffer zählen nicht zur Mindestzahl und lassen sich in einer separaten Ansicht wieder einblenden.
- Der Vergleich läuft lokal im Hintergrund, zeigt den Fortschritt und lässt sich abbrechen. Geprüfte Bildmerkmale bleiben bei unveränderten Dateien lokal verfügbar. Liefert das Dateisystem keinen geeigneten Änderungsstempel, etwa bei exFAT, bestätigt ein SHA-256-Prüfwert der vollständigen Bilddatei die Wiederverwendung. Vorschauen werden bei Bedarf erzeugt und im Arbeitsspeicher gehalten. Defekte, animierte und zu große Dateien werden als Prüfhinweise aufgelistet.

### Namenskollisionen

Die Vorschau erkennt vorhandene Zieldateien und Kollisionen zwischen Einträgen desselben Plans. Bei einer markierten Konfliktzeile zeigt sie Quelle und Ziel nebeneinander mit vollständigen Pfaden, Größen und Änderungsdaten. Bei einem Konflikt im Plan ist auch die konkurrierende Quelldatei sichtbar. Der Filter **Offen oder blockiert** zeigt die noch zu klärenden Einträge. Konfliktaktionen gelten für die markierten Konflikte oder gesammelt für alle offenen Namenskonflikte. Offene oder blockierte Einträge müssen vor der Freigabe geklärt oder ausgeschlossen werden.

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

Bei einem unerwarteten Fehler pausiert das Tool den restlichen Sortierlauf. Es zeigt den Grund und den nächsten möglichen Schritt und bietet einen Sprung zum betroffenen Eintrag. Abgeschlossene, ausstehende und teilweise abgeschlossene Aktionen bleiben im Protokoll erkennbar. Sobald der tatsächliche Zustand eines teilweise abgeschlossenen Vorgangs geklärt ist, kann der Nutzer den Lauf fortsetzen oder abgeschlossene Verschiebungen zurücknehmen. Beim Fortsetzen prüft das Tool die ausstehenden Einträge erneut.

## Weitere Ausbaustufen

1. **Namens- und Versionsvergleich.** Unscharfe Namensähnlichkeit für Tippfehler und abweichende Wortreihenfolgen ergänzen. Dateifamilien mit nachvollziehbaren Hinweisen bilden, interne Versionsangaben etwa bei EXE, DLL und Installationspaketen auslesen und Inhaltsunterschiede für ausgewählte Office-, PDF- und Textformate sichtbar machen. Widersprüchliche Hinweise anzeigen und uneindeutige Versionsreihenfolgen offenlassen. Zwei beliebige Inventarstände oder Ordnerbestände gegenüberstellen und mögliche Verschiebungen oder Umbenennungen anhand übereinstimmender Inhalte kennzeichnen.
2. **Erweiterter Bild- und Videovergleich.** Gedrehte Bildfassungen, Zuschnitte und ähnliche Serienbilder erkennen. Bei Videos umkodierte Vollkopien, gekürzte Fassungen und überlappende Ausschnitte erkennen. Für Ausschnitte die passenden Zeitbereiche anzeigen. Den Vergleich später mit einem separaten Testbestand aus synthetischen Medien prüfen.

Die Oberfläche zeigt den Grund für jeden Treffer. Namensähnlichkeit, Hinweise auf eine Versionsbeziehung, visuelle Ähnlichkeit und bestätigte Bytegleichheit haben jeweils eigene Befunde.

Einzelne Dateien lassen sich über **Datei prüfen** untersuchen. **Zwei Dateien vergleichen** öffnet die Gegenüberstellung von Versionsangaben und verfügbaren Inhaltsunterschieden. Die Ergebnisliste bietet die entsprechenden Aktionen für eine oder zwei markierte Dateien.

## Abnahme von Version 1

- Einen kontrollierten Testbestand zweimal scannen. Die Filter finden neu angelegte, entfernte und geänderte Dateien mit korrekten Trefferzahlen. Ein unterbrochener oder nicht erreichbarer Scan-Bereich erzeugt keine fälschlichen Fehlend-Treffer.
- Gleich große unterschiedliche Dateien, bestätigte Kopien mit abweichenden Namen oder Endungen und Hardlinks prüfen. Wiederverwendung, Neuberechnung, Pausieren, Fortsetzen und Abbrechen der Duplikatsuche nachvollziehen.
- Eine einfache Namensfamilie mit einzeln abwählbaren Dateien und änderbarem Zielordner im Sortierplan anzeigen. Alle Kollisionsaktionen einschließlich Präfix und Suffix, Mehrfachauswahl, Kollisionen innerhalb desselben Plans und später belegte Ziele mit Testdateien prüfen.
- Kopieren sowie Verschieben und Rückgängig auf demselben und über zwei Laufwerke prüfen. Als Fehlerfälle gesperrte Dateien, fehlende Rechte, zu wenig Speicher, Änderungen nach der Vorschau, fehlgeschlagene Kopierprüfungen und einen unterbrochenen Vorgang mit Neustart nachstellen. Beim Ersetzen muss Rückgängig sowohl die ursprüngliche Quelle als auch den vorherigen Zielinhalt wiederherstellen. Ein inzwischen belegter Quellpfad oder ein verändertes Ziel muss als Konflikt sichtbar sein.
- Mit einem Fehler in der Mitte eines Sortierlaufs prüfen, dass die restlichen Aktionen pausieren und ihre Zustände erkennbar bleiben. Fortsetzen mit erneuter Prüfung sowie die Rücknahme abgeschlossener Verschiebungen nach einem Neustart prüfen. Die Vorschau zum Freigeben von Sicherungsspeicher muss die betroffenen Läufe und den Verlust der Rückgängig-Funktion sichtbar machen.
- Einen separat ausgewählten Testordner für den lesenden Praxistest zu Inventar, Speicheranalyse, Duplikatsuche und Sortiervorschau verwenden. Für ausgeführte Dateiaktionen einen kontrollierten Testbestand verwenden.
