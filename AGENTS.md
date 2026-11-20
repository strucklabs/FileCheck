# Projektanweisung für den Filesize-Checker

Entwickle den Filesize-Checker als lokales Windows-Tool der CheckTools-Reihe. Der [Funktionsplan](PROJEKTPLAN.md) legt Umfang, Ausbaustufen und Abnahmekriterien fest. Aktualisiere ihn, wenn sich eine Produktentscheidung ändert.

## Umsetzung

- Verwende C# mit .NET 10 und Windows Forms wie bei WinCheck. Halte Inventar, Dateivergleiche, Sortierregeln und Ausführung unabhängig von der Oberfläche. Scans und Inhaltsprüfungen laufen im Hintergrund und melden Fortschritt. Speichere Inventare, Prüfdaten und Regeln lokal.
- Implementiere zuerst Inventar, Speicheranalyse und den Vergleich mit dem letzten erfolgreichen Scan. Stelle **Neu**, **Fehlend** und **Verändert** als kombinierbare Filter mit Trefferzahlen bereit. Behalte bei einem unvollständigen Scan den letzten erfolgreichen Stand der betroffenen Bereiche bei. Weise aus, ob ein Befund auf Metadaten oder einer Inhaltsprüfung beruht.
- Ermittle exakte Kopien anhand von Dateigröße, SHA-256 und anschließendem Bytevergleich. Behandle gespeicherte Prüfwerte als wiederverwendbare Zwischenergebnisse. Ermögliche eine vollständige Neuberechnung und verwerfe Prüfergebnisse für Dateien, die sich während der Prüfung ändern. Berücksichtige Hardlinks bei der Berechnung des potenziell freigebbaren Speichers.
- Implementiere einfache Namensähnlichkeit bereits in Version 1. Bilde nachvollziehbare Gruppen anhand erkennbarer Kopier- und Versionszusätze. Schlage dafür Zielordner vor und lass Nutzer die Gruppenzugehörigkeit für den jeweiligen Plan bearbeiten. Die weiteren Vergleichsverfahren richten sich nach den Ausbaustufen im Funktionsplan.

## Dateivorgänge

- Führe Inventar, Vergleiche und Sortiervorschauen lesend aus. Zeige vor der Ausführung die endgültigen Ziele und Aktionen und warte auf die Freigabe des Sortierplans.
- Unterstütze im Konfliktdialog **Überspringen**, **Ersetzen**, **Beide behalten** und **Präfix oder Suffix** je Konflikt oder für die ausgewählten Konflikte. Erhalte die Dateiendung und prüfe erzeugte Namen nach den Windows-Dateinamensregeln. Prüfe sowohl den Zielbestand als auch geplante Ziele desselben Laufs. Erneut entstandene Konflikte erfordern vor einem Ersetzen eine neue Entscheidung.
- Unterstütze in Version 1 Kopieren und Verschieben mit Rückgängig. Prüfe den gesamten Verschiebeplan vor der Freigabe auf erreichbare Quellen und Ziele, verfügbare Zugriffsrechte, gültige Pfade, Konflikte und ausreichenden Speicher je Ziellaufwerk einschließlich temporärer Dateien und Sicherungen. Lass blockierte Einträge korrigieren oder ausdrücklich aus dem Plan nehmen. Prüfe unmittelbar vor jeder Einzelaktion erneut, ob Quelle und Ziel dem freigegebenen Plan entsprechen.
- Verwende auf demselben Laufwerk eine atomare Umbenennung, soweit das Dateisystem sie unterstützt. Erstelle bei laufwerksübergreifenden Verschiebungen eine temporäre Zielkopie und verifiziere den Inhalt. Entferne die Quelle erst, wenn das endgültige Ziel verifiziert ist, der Wiederherstellungsschritt dauerhaft protokolliert wurde und die Quelle dem geprüften Stand entspricht. Melde einen Teilabschluss mit den tatsächlichen Pfaden. Sichere beim Ersetzen die bisherige Zieldatei vor der Änderung.
- Speichere vor jedem Eingriff den Ausgangszustand in einem dauerhaften Protokoll und gleiche offene Vorgänge nach einem Neustart mit dem Dateisystem ab. Prüfe vor Rückgängig Zielzustand, Sicherungen und ursprünglichen Quellpfad. Halte bei inzwischen belegten oder veränderten Pfaden an und zeige den Konflikt. Bei laufwerksübergreifender Rücknahme gilt dieselbe Kopierprüfung vor dem Entfernen wie beim Verschieben.
- Erhalte Rückgängig für abgeschlossene Verschiebeläufe über Neustarts hinweg, solange die Voraussetzungen erfüllt sind. Zeige den Speicherverbrauch der Sicherungen und vor deren Freigabe die Läufe, für die Rückgängig damit ganz oder teilweise entfällt. Warte auf Bestätigung.
- Pausiere bei einem unerwarteten Fehler den restlichen Sortierlauf und zeige abgeschlossene, ausstehende und teilweise abgeschlossene Aktionen. Kläre den tatsächlichen Zustand teilweise abgeschlossener Aktionen, bevor der Nutzer fortsetzt oder abgeschlossene Verschiebungen zurücknimmt. Prüfe ausstehende Aktionen vor dem Fortsetzen erneut.

## Verifikation

- Verwende `<Testordner>` als lesenden Praxistest für Scan, Speicheranalyse, Duplikatsuche und Sortiervorschau. Führe Kopieren, Umbenennen, Ersetzen und Verschieben mit einem separaten kontrollierten Testbestand aus.
- Prüfe die Abnahmekriterien des Funktionsplans beim jeweiligen Entwicklungsschritt. Decke dabei Scan-Ausfälle, gleich große Dateien mit unterschiedlichem Inhalt, Hardlinks, Namensfamilien und Konflikte im selben Sortierplan ab. Gib bei der Übergabe an, welche Fälle tatsächlich geprüft wurden.
