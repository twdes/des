# Passwords

Passwörter können in 3 verschiedenen formen in der Konfiguration abgelegt werden.

+----------+------------------------------------------+
| Prefix   | Beschreibung                             |
+----------+------------------------------------------+
| `win0x`  | Das Password wird verschlüsselt und kann |
|          | nur von der lokalen Maschine wieder      |
| `win64`  | entschlüsselt werden.                    |
+----------+------------------------------------------+
| `usr0x`  | Das Password wird verschlüsselt und kann |
|          | nur von dem aktuellen Nutzer wieder      |
| `usr64`  | entschlüsselt werden.                    |
+----------+------------------------------------------+
| `plain`  | Das Passwort wird in klartext verwendet. |
+----------+------------------------------------------+

Das `0x` und `64` stehen für die Kodierung der Bytefolge. Einmal Hexadezimal bzw. Base64.

Die Hexadezimalkodierung kann optional das prefix `0x` haben.


## Lua-Funktionen

Es gibt die zwei funktionen zum en/decodieren des Passwortes.
- EncodePassword(password, passswordType = "win64")
- DecodePassword(passwordValue)
- DecodePasswordSecure(passwordValue)
- 
```Lua
return DecodePassword("win0x:" + password);
```

## Passwörte via Powershell erzeugen

Ein "lokal Machine"-Passwort kann über die Powershell generiert werden.

```PS
 "win0x:" + (ConvertFrom-SecureString -SecureString (Read-Host -AsSecureString -Prompt "Passwort"))
```

# Passwort-Hash

Bei einem Passwort-Hash wird nur die Prüfsumme des Passwortes abgelegt. D.h.
es können nur Passwörter dagegen geprüft werden.

Der Hash kann ein Hex-Byte folge sein (prefix `0x`) oder Base64 enkodiert.

Der MS-SQL-Server verwendet den selben Algorithmus, dadurch kann ein Passwort mittels erzeugt werden.

```sql
select PWDENCRYPT('test')
```

## Lua-Funktionen

- EncodePasswordHash(password)
- ComparePasswordHash(string password, string passwordHash)
