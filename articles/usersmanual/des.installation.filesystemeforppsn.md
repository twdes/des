1. Ein neues Verzeichnis anlegen
   ```bash
   New-item -ItemType Directory [Arbeitsverzeichnis]
   ```
   
   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [Arbeitsverzeichnis] | `C:\DE-Server` |
1. folgende Unterordner anlegen:
   ```bash
   cd [Arbeitsverzeichnis]
   New-item -ItemType Directory Backup,Bin,Cfg,Client,Data,Log,Speedata,Temp
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [Arbeitsverzeichnis] | `C:\DE-Server` |
   >
   > | Ordnername | Verwendung |
   > | --- | --- |
   > | Backup | für Backups des PPSn Systemes |
   > | Bin | Binärdateien des DE-Servers (alle Dateien aus `PPSnOS\ppsn\PPSnMod\bin\[Release/Debug]`) |
   > | Cfg | Konfigurationsdateien des DE-Servers (alle Dateien aus `PPSnOS\ppsn\PPSnModCfg\cfg`) |
   > | Client | Clientanwendung (alle Dateien aus `PPSnOS\ppsn\PPSnDesktop\bin\[Release/Debug]`) |
   > | Data | Datenbankverzeichnis |
   > | Log | Logdateien des Servers |
   > | speedata | Verzeichnis für das Reportingsystem (`update.ps1`aus `PPSnOS\ppsn\PPSnReport\system`) |
   > | Temp | Temporäre Daten |

1. Dateien kopieren
   vom Remotecomputer aus:
   ```bash
   #Binärdateien kopieren
   Copy-Item -Path C:\Projects\PPSnOS\ppsn\PPSnModCfg\bin\Debug\* -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Bin\ -recurse -force
   #ppsn_server.xml aus ppsn.xml erstellen und entsprechend anpassen
   Copy-Item -Path C:\Projects\PPSnOS\ppsn\PPSnModCfg\bin\Debug\cfg\* -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Cfg\ -recurse -force
   # Speedata Setup kopieren
   Copy-Item -Path C:\Projects\PPSnOS\ppsn\PPSnReport\system\update.ps1 -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Speedata\ -recurse -force
   # Datenbankschema kopieren
   Copy-Item -Path "C:\Projects\PPSnOS\ppsn\Extern\ppsncfg\PPSnMaster\bin\Debug\PPSnMaster.publish.sql" -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Temp\
   # Client Kopieren
   Copy-Item -Path C:\Projects\PPSnOS\ppsn\PPSnDesktop\bin\Debug\* -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Client\ -recurse -force
   ```
   *  Gegebenenfalls den Clientordner im Netzwerk freigeben:
      ```bash
      New-SmbShare -Name PPSnClient -Path C:\DEServer\Client\ -FolderEnumerationMode Unrestricted -ReadAccess Jeder
      ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [DES-IP] | ... |
   > | [Hostbenutzer] | `ppsn\Administrator` |
   
   > [!TIP]
   > [PPSn.xml anpassen](<xref:ppsn.mod.installation#iv-ppsn-server-konfigurieren>)