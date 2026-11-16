# Titel mit Abstracts und Zitaten von Claude anreichern

Sie haben PDFs mit Ihren Titeln verknüpft und möchten, dass Citavi ein Abstract und die wichtigsten Zitate mit Seitenzahlen enthält, ohne diese von Hand zu übernehmen.

## Lösung
Das Makro sendet für jeden Titel in der aktuellen Auswahl die erste verknüpfte PDF-Datei an Claude, standardmäßig über einen [LiteLLM](https://docs.litellm.ai)-Proxy. Claude liest das Dokument (auch gescannte Seiten) und liefert:

- ein englisches **Abstract**, das nur eingetragen wird, wenn das Abstract-Feld des Titels leer ist
- bis zu 15 **wörtliche Zitate** mit der gedruckten Seitenzahl (bzw. der PDF-Seite, falls keine Seitenzahl gedruckt ist) und einer englischen Kernaussage
- bis zu 8 **Zusammenfassungen** der Hauptabschnitte mit Seitenbereich

Vorhandene Abstracts und Zitate werden nie verändert. Zitate und Zusammenfassungen werden nur hinzugefügt, wenn der Titel noch keine Zitate hat (Markierungen zählen nicht). Titel, die bereits ein Abstract und Zitate haben, werden gar nicht an Claude gesendet. Das Makro kann daher gefahrlos mehrfach ausgeführt werden. Alle neuen Zitate erhalten das Schlagwort `AI-generated`, damit Sie sie prüfen können.

Die Höchstzahlen, das Modell und das Schlagwort können im Abschnitt `EDIT HERE` des Makros angepasst werden.

## Voraussetzungen
- Ein LiteLLM-Proxy mit einem Claude-Modell, das PDFs verarbeiten kann. Passen Sie `model` (Modellname aus Ihrer LiteLLM-Konfiguration) im Abschnitt `EDIT HERE` an.
- Zwei Windows-Benutzerumgebungsvariablen (Windows-Einstellungen > "Umgebungsvariablen für dieses Konto bearbeiten"), danach Citavi neu starten:
  - `LITELLM_BASE_URL`: Basis-URL des Proxys, z. B. `https://litellm.example.com`
  - `LITELLM_API_KEY`: Ihr LiteLLM-Schlüssel
- Um Anthropic direkt anzusprechen, setzen Sie `LITELLM_BASE_URL` auf `https://api.anthropic.com` und `LITELLM_API_KEY` auf einen Anthropic-API-Schlüssel.

## Wichtig
- Die PDF-Dateien werden an den Proxy und den Modellanbieter übertragen. Verwenden Sie das Makro nur, wenn Sie die Dokumente weitergeben dürfen.
- Generierte Texte können Fehler enthalten. Prüfen Sie Zitate und Seitenzahlen anhand des PDFs, bevor Sie sie verwenden.
- Die Analyse kann pro PDF mehrere Minuten dauern. Citavi reagiert während der Ausführung nicht.

## Download
[für Citavi 6](C6_Enrich_References_With_Claude.cs)

## Anwendung
Folgen Sie der Anleitung in [diesem Artikel](/readme.de.md), um das Makro einzusetzen.
