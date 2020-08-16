# Actions

Aktionen sind definierte funktionen die von extern aus erreichbar sein sollen.

## Definition

todo...

### Automatische Parameter

Parameter mit folgenden Datentyp werden speziell behandelt.

`IDEWebRequestScope`
:   Übergibt alle Htp-Request informationen

`LogMessageScopeProxy`
:   Aktiviert automatisches Logging und gibt Zugriff auf den entsprechenden Kontext.

`DEConfigItem` oder Vererbungen
:   Gibt zugriff auf den aktuellen Knoten, an dem die Aktion ausgeführt wird.

`TextReader`
:   Der Inputstream wird als TextReader bereit gestellt.

`Stream`
:   Der Inputstream wird als Stream bereit gestellt.

`LuaTable`
:   Der Inputstream wird als LuaTable interpretiert.
