# Nutzer

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

## Rechte

Ein Nutzer hat immer das Recht `desUser`.