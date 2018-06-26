---
uid: des.readme
title: DES Readme
---

DEServer
========

# Einleitung
DES ist ein Dienst der die Kommunikation in einer Firma verwaltet.
Er ist nicht als Server für millionen von Anfragen ausgelegt, sondern seine Stärke ist die Verwaltung von definierten Nutzer- und Gerätegruppen in einen definierten Umfeld. Aber im bescheidenen Umfeld ist auch möglich überbetriebliche Aufgaben zu übernehmen.

## Was es ist
* Innerbetrieblicher Kommunikations Hub
* Hierarchisch Konfigurierbar
* Flexibel erweiterbar
* Externe Datenschnittstellen

## Was es nicht ist
* Ein WebServer für den Internetauftritt

# Erste Schritte
1. Um den Dienst starten zu können, benötigt man eine Konfigurationsdatei (Minimalbeispiel):
'''xml
<?xml version="1.0" encoding="utf-8" ?>
<des xmlns="http://tecware-gmbh.de/dev/des/2014" version="330">
	<server logpath="Log" />
	<http />
	<luaengine />
</des>
'''
2. Nun kann der Dienst über die Kommandozeile gestartet werden:
'''PS
DEServer.exe run -v -c C:\Config.xml
'''
3. Weiterführende Hilfe zu den Parametern des Dienstes bekommt man mit folgendem Befehl:
'''PS
DEServer.exe help
'''
Bei erfolgreicher Konfiguration des Dienstes kann der Status über http://localhost:8080/des.html abgerufen werden.

# Technologie
Grundlegend werden folgende Technologien vorausgesetzt, es kann ja nach Konfiguration zusätzliches hinzukommen
* Es handelt sich um einen Windows Service
* .net Framework 4.6 (C#)
* Lua für Scripting [NeoLua](http://https://github.com/neolithos/neolua)
* http WebServer [HttpSys](https://msdn.microsoft.com/en-us/library/windows/desktop/aa364510%28v=vs.85%29.aspx)

# Mitarbeit
(ToDo)

# Lizenz

Licensed under the [EUPL, Version 1.1] or - as soon they will be approved by the
European Commission - subsequent versions of the EUPL(the "Licence"); You may
not use this work except in compliance with the Licence.

[EUPL, Version 1.1]: https://joinup.ec.europa.eu/community/eupl/og_page/european-union-public-licence-eupl-v11

# How-to
(ToDo)
