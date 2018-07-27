1. Die URL für DE-Server freigeben
   ```bash
   netsh http add urlacl url=http://+:80/ user=[DES-Benutzername] listen=yes
   ```
1. Den Port in der Firewall freigeben
   ```bash
   netsh advfirewall firewall add rule name="DE-Server" dir=in protocol=TCP localport=80 action=allow
   ```