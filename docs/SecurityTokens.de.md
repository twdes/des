# SecurityTokens

Mit SecurityTokens wird ein System für die Rechtezuordnung bereitgestellt. An jeden 
Knoten können entsprechende Tokens hinterlegt werden, die ein Nutzer besitzen muss
damit er auf diese Resource zugreifen kann.

## Tokens/Gruppen

SecurityTokens sind einfache Zeichenfolgen die aus Buchstaben, Ziffern und Punkten 
bestehen können (z.B. `desSys`, `des.userlist`). Groß-/Kleinschreibung ist dabei
nicht relevant.

Ein SecurityToken kann auch eine Gruppe von Tokens darstellen.

Dies wird in der Konfiguration definitert:

```xml
<server>
	<securitygroup name="mde.full">mde.entnahme;mde.umlagern;mde.info</securitygroup>
</server>
```

Der neue Token `mde.full` wird in die drei Untertoken `mde.entnahme`, `mde.umlagern` 
und `mde.info`.

## Verwendung

### Konfiguration

In der Konfiguration können die Zugriff auf die einzelnen Knoten, Actions und Listen gesteuert.

### API

Innerhalb des Codes gibt es Zugriff über den aktuellen Kontext. Es gibt
zwei Methoden für die Abfrage eines Tokens (`TryDemandToken` bzw. `DemandToken`).

```Lua
return GetCurrentScope():TryDemandToken("desSys");
```
```C#
var scope = DEScope.GetScopeService<IDECommonScope>(true);
return scope.TryDemandToken("desSys");
```
Gibt true oder false zurück.

```Lua
GetCurrentScope():DemandToken("desSys");
```
```C#
var scope = DEScope.GetScopeService<IDECommonScope>(true);
return scope.DemandToken("desSys");
```
Wirft eine Zugriffsexception, wenn der Token nicht vorhanden ist.

In .net gibt zusätzlich noch `IDEAuthentificatedUser` mit `IsInRole`.
