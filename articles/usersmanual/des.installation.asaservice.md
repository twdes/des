1. Eingabeaufforderung mit erhöhten Rechten starten:
   ```bash
   #Service registrieren
   [Arbeitsverzeichnis]\Bin\DEServer.exe register --config [Konfigurationsdatei] --name [Unternehmensname]
   #Benutzer für den Service festlegen
   sc.exe config "Tw_DES_[Unternehmensname]" obj= [DES-Benutzername] password=[DES-Passwort]
   # DEServer für PPSn benötigt die Datenbank zum Start
   sc.exe config "Tw_DES_[Unternehmensname]" depend= HTTP/[Datenbankservicename]
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [Arbeitsverzeichnis] | C:\DEServer |
   > | [Config] | C:\DEServer\Cfg\PPSn.xml |
   > | [Datenbankservicename] | MSSQL$PPSNDATABASE oder $((Get-Service *mssql*).Name) |
   > | [DES-Benutzername] | ppsn\PPSnServiceUser$ |
   > | [DES-Passwort] | ... |
   > | [Unternehmensname] | TecWare |

   > [!WARNING]
   > Bei Verwendung eines [Managed Service Accounts](https://blogs.technet.microsoft.com/askds/2009/09/10/managed-service-accounts-understanding-implementing-best-practices-and-troubleshooting/) ist der Parameter wegzulassen.
1. Gegebenenfalls müssen in übergeordneten Routern Exposed Hosts/Portweiterleitungen eingerichtet werden, um den DE-Server aus fremden Netzsegmenten erreichen zu können.