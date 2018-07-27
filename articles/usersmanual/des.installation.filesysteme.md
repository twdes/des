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
   New-item -ItemType Directory Bin,Cfg,Log,Temp
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [Arbeitsverzeichnis] | `C:\DE-Server` |
   >
   > | Ordnername | Verwendung |
   > | --- | --- |
   > | Bin | Binärdateien des DE-Servers (alle Dateien aus `PPSnOS\ppsn\PPSnMod\bin\[Release/Debug]`) |
   > | Cfg | Konfigurationsdateien des DE-Servers (alle Dateien aus `PPSnOS\ppsn\PPSnModCfg\cfg`) |
   > | Log | Logdateien des Servers |
   > | Temp | Temporäre Daten |

1. Dateien kopieren
   vom Remotecomputer aus:
   ```bash
   #Binärdateien kopieren
   Copy-Item -Path C:\Projects\PPSnOS\twdes\des\Server\bin\Debug\* -toSession (New-PSSession -ComputerName [DES-IP] -Credential [Hostbenutzer]) -Destination C:\DEServer\Bin\ -recurse -force
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [DES-IP] | ... |
   > | [Hostbenutzer] | `ppsn\Administrator` |
   
   > [!TIP]
   > [PPSn.xml anpassen](<xref:ppsn.mod.installation#iv-ppsn-server-konfigurieren>)