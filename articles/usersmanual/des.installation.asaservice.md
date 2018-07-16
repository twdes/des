1. Eingabeaufforderung mit erhöhten Rechten starten:
   ```bash
   #Service registrieren
   [Arbeitsverzeichnis]\\Bin\\DEServer.exe register --config [Arbeitsverzeichnis]\\Cfg\\[Config].xml --name [Unternehmensname]
   #Benutzer für den Service festlegen
   sc.exe config "Tw_DES_[Unternehmensname]" obj= ".\[DES-Benutzername]" password= "[DES-Passwort]"
   #URL für DE-Server freigeben
   netsh http add urlacl url=http://+:80/[Anwendungsname] user=[DES-Benutzername] listen=yes
   #Ausnahme in der Firewall erstellen
   netsh advfirewall firewall add rule name="DE-Server" dir=in protocol=TCP localport=80 action=allow
   #oder
   netsh advfirewall firewall add rule name="DE-Server" dir=in program="[Arbeitsverzeichnis]\\Bin\\DE-Server.exe" action=allow
   #DE-Server als Service starten
   net start Tw_DES_[Unternehmensname]
   ```
2. Gegebenenfalls müssen in übergeordneten Routern Exposed Hosts/Portweiterleitungen eingerichtet werden, um den DE-Server aus weiteren Netzsegmenten erreichen zu können