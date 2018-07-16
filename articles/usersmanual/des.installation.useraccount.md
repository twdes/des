1. Es sollte ein neuer Benutzer mit <span style="color:red">ToDo: erforderliche Rechte herausfinden</span> eingerichtet werden.  
   Dazu Eingabeaufforderung mit erhöhten Rechten starten.
   ```bash
   net user /add [DES-Benutzername] [DES-Passwort]
   net user [DES-Benutzername] /passwordchg:no
   net user [DES-Benutzername] /expires:never
   net localgroup Administratoren [DES-Benutzername] /add
   ```
   <span style="color:red">oder Managed Service Account?</span>