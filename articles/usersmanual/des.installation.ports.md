---
uid: des.configuration.ports
title: DE-Server Netzwerkkonfiguration
---

#### Entsprechend ob der DE-Server verschlüsselt oder offen betrieben werden soll, die Anweisungen <i>HTTPS</i> oder <i>HTTP</i> befolgen

##### <i>HTTPS</i> Verschlüsselter Zugriff

[!include[DES secured](~/des/articles/usersmanual/des.installation.ports.ssl.md)] 

##### <i>HTTP</i> Offener Zugriff

[!include[DES plain](~/des/articles/usersmanual/des.installation.ports.plain.md)]

> [!NOTE]
> | Variable | Beispiel |
> | --- | --- |
> | [DES-Benutzername] | `ppsn\PPSnServiceUser$` |

> [!IMPORTANT]
> Die hier gewählte URL inklusive Port muss im weiteren Verlauf in die [Konfigurationsdatei](<xref:ppsn.mod.installation#iv-ppsn-server-konfigurieren>) eingefügt werden.