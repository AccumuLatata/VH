# Loss Lock für Quantower

Sperrt ein Quantower-Konto, nachdem ein Trade im Verlust geschlossen wurde. Ein Verlust sperrt 5 Minuten. Drei Verluste in Folge sperren 30 Minuten. Beides ist einstellbar.

Die Sperre ist Quantowers eigene Kontosperre (`Core.LockAccount` / `Core.UnLockAccount`). Das ist dieselbe Sperre wie im Panel Account Info. Chart, DOM und Order Entry nehmen auf diesem Konto keine neuen Orders mehr an. Sieht der Abschluss nach einem Verlust aus, sperrt die Strategie sofort. Die Serie und die endgültige Frist werden nach etwa 0,6 Sekunden festgeschrieben, damit ein bloßes Positions-Update der Verbindung nicht als Verlust zählt. Kommt dieselbe Position in dieser Zeit zurück, wird die vorläufige Sperre wieder aufgehoben. Danach setzt die Strategie die Sperre jede Viertelsekunde neu, damit ein manuelles Entsperren während der Frist nicht hält.

## Was ein Verlust ist

Ein Abschluss zählt, wenn eine Position flach wird (`PositionRemoved`). Teilverkäufe bleiben ein Trade, bis die Position weg ist. Brutto-P&L unter null ist ein Verlust. Ein Ergebnis von genau null ändert die Serie nicht. Ein Gewinn setzt die Serie auf 0, hebt eine schon laufende Sperre aber nicht vorzeitig auf.

Gebühren zählen standardmäßig nicht mit, damit ein glatter Ausgang nicht wegen der Commission zur Sperre wird. Die Option dafür steht in den Strategie-Einstellungen.

Die Serie wird an einem neuen lokalen Kalendertag zurückgesetzt. Eine Sperre, die über Mitternacht läuft, läuft trotzdem bis zu ihrem Ende.

## Installation

Quantower schließen. In PowerShell, im Ordner dieses Projekts:

```powershell
.\build.ps1 -QuantowerRoot C:\Quantower
```

Das Skript sucht `TradingPlatform\v*\bin\TradingPlatform.BusinessLayer.dll`, baut `LossLock.dll` und kopiert sie nach `Settings\Scripts\Strategies\LossLock\`.

Quantower starten.

1. Strategies Manager öffnen (StM).
2. Plus, unter Custom **Loss Lock** wählen.
3. Einstellungen: das Handelskonto wählen. Die Minuten können so bleiben (5 und 30, drei Verluste).
4. Im Strategies Manager unter General **Restore strategies state after shutdown** aktivieren. Sonst läuft die Strategie nach einem Neustart nicht weiter und hebt die Sperre nicht von selbst auf.
5. Run.

Nur eine Instanz pro Konto. Zwei Instanzen zählen denselben Abschluss doppelt.

Der Zustand liegt unter `%LocalAppData%\LossLock\`. Dort stehen Serie und Sperr-Ende, damit ein Neustart von Quantower die laufende Frist fortsetzt.

## Vor dem Live-Konto prüfen

Zuerst auf einem Sim-Konto, mit kurzen Fristen. Die Minuten lassen sich nur ändern, bevor die Strategie läuft. Also stoppen, auf 1 und 2 Minuten stellen, starten, danach wieder auf 5 und 30 stellen.

1. Einen kleinen Verlust schließen. Das Konto muss gesperrt sein, eine neue Order darf nicht durchgehen. Im Log steht die Uhrzeit.
2. Eine offene Position muss sich während der Sperre noch schließen lassen, und ein schon liegender Stop muss liegen bleiben. Wenn das Schließen blockiert ist: Strategie stoppen, im Account-Info-Panel entsperren, die Option **Kontosperre von Quantower setzen** ausschalten. Dann bleibt nur das Abbrechen neuer Einstiege. Das ist schwächer, weil eine Market-Order zwischen Absenden und Abbruch noch gefüllt werden kann. Die Kontosperre ist der eigentliche Schutz. Vor dem Live-Handel muss das Schließen auf dem Sim funktionieren.
3. Während der Sperre im Account-Info-Panel entsperren. Innerhalb einer Sekunde muss die Sperre wieder da sein.
4. Drei Verluste hintereinander. Die dritte Sperre muss die lange Frist sein.
5. Strategie stoppen. Die Sperre bleibt. Neu starten: sie wird zur gespeicherten Uhrzeit aufgehoben.

Notfall, wenn die Strategie läuft und du trotzdem raus musst: Strategie stoppen, dann im Account-Info-Panel die Kontosperre lösen. Solange Loss Lock läuft, setzt es die Sperre sofort wieder.

## Was diese Sperre nicht ist

Sie gilt nur in dieser Quantower-Instanz, und nur solange die Strategie läuft. Orderfenster der Börse, das Handy und ein zweiter Rechner sind davon nicht betroffen. Es ist keine Limit des Brokers und kein Ersatz für die Regeln einer Prop-Firma.

Stoppt du die Strategie, bleibt die Kontosperre bis zum gespeicherten Ende stehen, wird aber nicht mehr erneuert. Ein manuelles Entsperren wirkt dann.

Das eingebaute Risk-Management-Panel kann das nicht. Es kennt Tagesverlust, Positionsgröße und Flatten. **Lock on Risk Event** friert dort die Vorlage ein, damit du das Limit nicht hochziehst. Es sperrt nicht für fünf Minuten nach einem einzelnen Verlust und nicht für dreißig Minuten nach drei Verlusten.

Der Button **Lock trading** in der Toolbar ist die falsche Sperre. Laut Quantower-Hilfe schaltet er auch das Schließen von Orders und Positionen ab. Deshalb nutzt Loss Lock die Kontosperre und nicht diesen Button.

Abschlüsse, die passieren während die Strategie nicht läuft, zählen nicht nachträglich mit. Ein Verbindungsabbruch zählt nicht als Trade: verschwindet die Position, weil die Verbindung weg ist, bleibt die Serie unverändert.

## Regeln im Kurztest

Die Zeitrechnung steht in `src/LossLockRules.cs` und hat keinen Quantower-Bezug. Lokal:

```powershell
dotnet run --project tests/LossLock.Tests.csproj
```

Erwartet wird `all passed`. Damit ist geprüft: ein Verlust fünf Minuten, der dritte dreißig Minuten, ein Gewinn löscht die Serie und nicht die laufende Sperre, ein kürzerer neuer Verlust verkürzt eine längere Sperre nicht, ein neuer Tag setzt nur die Serie zurück.
