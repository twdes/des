# Html Ausgabe

Html kann mittels Lua-Code der auf dem Server ausgeführt wird angereichert werden.

Einbetten:
```
<% print("Hallo") %>
```

Die Skripte werden in einen speziellen Html-Context ausgeführt. Der vom eigentlichen Knoten erbt.

:::info
Im Html kann also alles verwendet werden, was auch der Knoten kann.
:::

Globale Variablen die durch den Html-Scope hinzugefügt werden:

## Variablen

`out`
:   Zugriff auf Ausgabe Datenstrom. Je nach Type ein `TextWriter` oder `Stream`.

`Context`: `IDEWebRequestScope`
:   Zugriff auf den Request-Context.

`ContentType`: `string`
:   Aktueller Media-Type unter dem die Ausgabe erfolgt.

`ScriptBase`: `string`
:   Dateiname des Scripts.

## Methoden

`obinary(contentType : string)`
:   Öffnet den Ausgabestrom als Binärstrom. `out` ist ein `Stream`.

`otext(contentType : string, encoding : Encoding)`
:   Öffnet den Ausgabestrom als Text. `out` ist ein `TextWriter`.

`print(values)`
:   Gibt die Werte als Text aus.

`printValue(value, fmt)`
:   Gibt einen Wert als Text aus. Es wird dabei die Locale des Clients beachtet.

`indent(spaces)`
:   Erzeugt Leerzeichen am Zeilenanfang.

`printTemplate(source, args : table)`
:   Schreibt das Template in die Ausgabe.
