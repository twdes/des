---
uid: des.firststeps.configuration
title: DE-Server Beispielkonfiguration
---

## Konfiguration des DE-Servers

Zuvor sollte der DE-Server gem. @des.filestructure eingertichtet werden. Zusätzlich sollte das Verzeichnis `html` erstellt werden.

### Grundlegender Aufbau einer Konfigurationsdatei

```xml

<?xml version="1.0" encoding="utf-8" ?>
<des xmlns="http://tecware-gmbh.de/dev/des/2014"
  xmlns:ps="http://tecware-gmbh.de/dev/des/2015/powershell"
  version="330"
  displayname="DES Development Environment">

  <server logpath="..\Log">
  </server>

  <http>
    <prefix>
      http://localhost:8081/
    </prefix>

    <!-- Zugriffsrechte -->
    <access id="localhostRoot" scheme="none">
      http://localhost:8081/
    </access>
  </http>

  <cron/>

  <luaengine
    security="desSys"
    allowDebug="false" />

  <files
    name="html"
    displayname="HTML Base Dir"
    directory="..\html" />
</des>

```

Für die Serveradresse sei auf @des.configuration.ports verwiesen.  
Der DE-Server ist nun konfiguriert, Dateien aus dem Verzeichnis `html` anzuzeigen.

### Anhängen von Scripten(Funktionen) an einen Knoten

Um ein Script an einen Knoten anzuhängen, wird dieses zuerst an die LuaEngine gebunden:

```xml

<luaengine
  security="desSys"
  allowDebug="false" >
  <script
    id="ImportantFunctions"
    filename="..\html\imports.lua" />
</luaengine>

```

Dieses Script wird im Anschluss an einen Knoten gebunden:

```xml

<files
  name="html"
  displayname="HTML Base Dir"
  directory="..\html"
  script="ImportantFunctions" />

```