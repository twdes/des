#### Für den Start des DE-Servers als Service wird ein Managed Service Account eingerichtet
1. Auf dem DC eine Powershell mit erhöhten Rechten starten.
   ```bash
   #PowerShell Modul zur Konfiguration der Domäne laden
   Import-Module ActiveDirectory
   # MSA erstellen
   New-ADServiceAccount -Name [DES-Benutzername] -Enabled $true -DNSHostName [DNS-Hostname]
   # Dem Computer [DES-Hostname] die Nutzung des MSA erlauben
   Add-ADComputerServiceAccount -Identity [DES-Hostname] -ServiceAccount [DES-Benutzername]
   # Dem Computer [DES-Hostname] die Berechtigung zuweisen, das Kennwort für den MSA abzurufen
   Set-ADServiceAccount -Identity [DES-Benutzername] -PrincipalsAllowedToRetrieveManagedPassword [DES-Hostname];
   ```
   
   > [!Warning]
   > In den meisten Befehlen wird für -Identity eine GUID/SAM/DistinghuishedName erwartet. Für Computer ist dies i.d.R. der Hostname mit einem angehängten ''$''Zeichen.

   > [!Warning]
   > Wichtig: Managed Service Accounts enden IMMER auf ein ''$''-Zeichen. Auch wenn bei der Einrichtung ein Name ohne angegeben wird, so muss dieser bei Referenzen immer mit dem Dollarzeichen angegeben werden!

   > [!Tip]
   > Sofern der DC innerhalb der letzten 10 Stunden eingerichtet wurde und eine Fehlermeldung ''Schlüssel nicht gefunden'' erscheint, behebt folgender Befehl das Problem (Replikationszeitraum auf 10h gesetzt):
   > ```bash
   > Add-KdsRootKey –EffectiveTime ((get-date).addhours(-10))
   > ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [DES-Benutzername] | "PPSnServiceUser$" |
   > | [DNS-Hostname] | "ppsnserver.ppsn.tecware-gmbh.de" |
   > | [DES-Hostname] | "PPSnServer$" |
1. Auf dem Host, auf dem der DE-Server laufen soll, eine Powershell mit erhöhten Rechten starten
   ```bash
   Install-ADServiceAccount -Identity [DES-Benutzername]
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [DES-Benutzername] | "PPSnServiceUser$" |