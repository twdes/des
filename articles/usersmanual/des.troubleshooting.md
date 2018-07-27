---
uid: des.troubleshooting
title: DE-Server Fehlerbehebung
---

#### Überprüfen, ob der DE-Server in der HTTPsys eingetragen ist:

```bash
netsh http show servicestate
```

#### Überprüfen, ob der DE-Server Verbindungen akzeptiert

```bash
([System.Net.WebRequest]::Create('http://[IP des DE-Servers]/des.html')).GetResponse().StatusCode
```

Die Antwort sollte <i>(401) Not authorized</i> lauten.