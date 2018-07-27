---
uid: des.installation.dc
title: Installation des Domain-Controllers
---

#### Installation des Servers

##### Installation des Domain Controllers

1. IP-Adresse auf fixed setzen (Sconfig)
1. Hostnamen einstellen (Sconfig)
1. ```bash
   # Windows Rolle Ad installieren
   Install-Windowsfeature AD-Domain-Services
   # Powershell-Modul zur Verwaltung der AD installieren
   Import-Module ADDSDeployment
   # AD-Forest anlegen
   Install-ADDSForest -CreateDnsDelegation:$false -DatabasePath “C:\Windows\NTDS” -DomainMode “Win2012R2” -DomainName “[Domänenname]” -DomainNetbiosName "[Domänennetbiosname]" -ForestMode “Win2012R2” -InstallDns:$true -LogPath “C:\Windows\NTDS” -NoRebootOnCompletion:$false -SysvolPath “C:\Windows\SYSVOL” -Force:$true -SafeModeAdministratorPassword $(convertto-securestring -string "[SafeModeAdministratorPassword]" -asplaintext -force)
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [Domänenname] | "ppsn.tecware-gmbh.de" |
   > | [Domänennetbiosname] | "PPSn" |
   > | [SafeModeAdministratorPassword] | ... |
1. Server startet selbstständig neu
   > [!TIP]
   > Wenn der Server beim Neustart lange auf <i>gpserv</i> wartet:
   > ```bash
   > gpupdate /force
   > ```
1. DHCP einrichten
   ```bash
   Install-WindowsFeature DHCP -IncludeManagementTools
   netsh dhcp add securitygroups
   Restart-service dhcpserver
   # dhcp in domäne anmelden
   Add-DhcpServerInDC -DnsName [FQDN] -IPAddress [DC-IPaddress]
   #überprüfen
   Get-DhcpServerInDC
   #Notify Server Manager that post-install DHCP configuration is complete (Optional)
   Set-ItemProperty –Path registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\ServerManager\Roles\12 –Name ConfigurationState –Value 2

   Add-DhcpServerv4Scope -name [Domänennetbiosname] -StartRange 192.168.[x.y] -EndRange 192.168.[x.y] -SubnetMask 255.255.255.0 -State Active
   Set-DhcpServerv4OptionValue -DnsDomain [Domänenname] -DnsServer [DC-IPaddress]
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [FQDN] | "PPSnServer.ppsn.tecware-gmbh.de" |
   > | [DC-IPaddress] | "192.168.200.1" |
   > | [Domänennetbiosname] | "PPSn" |
   > | [Domänenname] | "ppsn.tecware-gmbh.de" |

#### Einrichtung des MSSQL-Servers 2016

1. Server installieren
   ```bash
   D:\setup.exe /Q /ACTION=Install /IACCEPTSQLSERVERLICENSETERMS /INDICATEPROGRESS /UpdateEnabled=False /FEATURES=SQLEngine /INSTANCENAME=[Instanzname] /SQLSVCACCOUNT=[SQL Serviceaccount] /SQLSYSADMINACCOUNTS=[Systemadministratoraccount] /SQLSVCSTARTUPTYPE=Automatic /FILESTREAMLEVEL=2 /FILESTREAMSHARENAME="MSSQLFileStreamShare"
   ```

   > [!NOTE]
   > | Variable | Beispiel |
   > | --- | --- |
   > | [SQL Serviceaccount] | "ppsn\PPSnServiceUser$" |
   > | [Instanzname] | "PPSnDatabase" |
   > | [Systemadministratoraccount] | "ppsn\Administrator" |

   > [!NOTE]
   > | Parameter | Auswirkung |
   > | --- | --- |
   > | /Q | Installation ohne GUI |
   > | /ACTION=Install | Installation |
   > | /IACCEPTSQLSERVERLICENSETERMS | den Lizenzbestimmungen wird zugestimmt |
   > | /INDICATEPROGRESS | zeigt Verlauf der Installation und somit ggf. wobei die Installation abgebrochen ist |
   > | /UpdateEnabled=False | während der Installation keine Onlineupdates abrufen |
   > | /FEATURES=SQLEngine | nur die Datenbankengine installieren |
   > | /INSTANCENAME | benannte instanz installieren |
   > | /SQLSVCACCOUNT | Service Account für die Datenbank |
   > | /SQLSYSADMINACCOUNTS | Administrationsaccount |
   > | /SQLSVCSTARTUPTYPE | Startverhalten des Service |
   > | /FILESTREAMLEVEL | NTFS FileStreams aktivieren - mindestens Level 2 wird von PPSn gefordert |
   > | /FILESTREAMSHARENAME | Freigabename der Filestreams |
1. Automatischen Start festlegen
   ```bash
   sc.exe config $((Get-Service *mssql*).Name) start= delayed-auto
   ```