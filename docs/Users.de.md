# Nutzer

## Arten

Nutzerwaltung innerhalb des DEServers.

Es können verschieden Nutzer existieren, ein Nutzer zeichnet sich durch die
Implementierung von `IDEUser` und einer Registrierung mittels `IDEServer.RegisterUser`.

Es gibt zwei Standardnutzer.

basic
:   Nutzername und Passwort werden definiert und von außen kann er
    durch Basic Authentification angesprochen werden. In der Konfiguration
    kann er mittels `basicuser` definiert werden.

ntml
:   Dieser Nutzer kann innerhalb des AD definiert werden, und wird von
    außen mittels NTLM authenitifizert. In der Konfiguration kann er mittels
    `ntlmuser`definiert werden.

Die registrierten Nutzer und ihre Rechte werden mit der Liste `tw_users`an der
Wurzel abefragt.

## Eigenschaften

Name - `Identity`
:   Alle Nutzer benötigen einen Namen. Diese beschreibt zustzlich die Art der Authentifizierung

Rechte - `SecurityTokens` 
:   Ein Nutzer hat immer das Recht `desUser`. Es können noch mehr Tokens existieren. Diese
werden immer sortiert in den SecurityTokens abgelegt.

`DisplayName`
:   Optionaler Anzeigename.

Ein Nutzer kann beliebig viele weiter Eigenschaften besitzen.

## Provider

Provider können neue Nutzer zur Verfügung stellen.

`IDEServer.RegisterUser` macht den neuen Nutzer bekannt.

Erweiterung der Tabelle `tw_users` kann mit dem Attribute `DEUserProperty` vorgenommen werden.

## Authentifizierte Nutzer

Nutzer müssen sich gegenüber dem System authentifizieren. Danach kann der Nutzer innerhalb des Contextes mittels z.B. `GetCurrentUser`abgerufen werden.

Da Web-Browser nur einen Authentifizierungsmodus zulassen, müssen mehr als einer mittels des Headers `des-multiple-authentifications`: `true` freigeschaltet werden

## API - Lua

- `GetUserByIdentity(IIdentity identity, bool exact = false)`
- `GetUserByName(string name)`

Für das Debugen der Anmeldung kann `LogNextIdentity(string name, int count)` verwendet werden.

## API - Http

Aktion:
- `logNextIdentity`

List
- `tw_users`