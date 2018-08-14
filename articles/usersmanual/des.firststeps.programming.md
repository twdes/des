---
uid: des.firststeps.programming
title: DE-Server Beispielprogrammierung
---

## Beispiele für die dynamische Erzeugung von Inhalten durch den DE-Server

### Einführung

Der DE-Server unterstützt neben der Auslieferung von statischen Inhalten auch die dynamische Erzeugung mittels eingebettetem Lua-Programmcode. Lua-Scripte können dabei, ähnlich PHP, direkt in HTML eingebtettet werden (Siehe [Einbetten von Lua-Code](<xref:des.firststeps.programming#eingebetteter-code>)). Ebenfalls können Lua-Dateien selbst als Ressource für dynamische Web-Services dienen (Siehe [Lua-Dateien](<xref:des.firststeps.programming#lua-dateien>))

> [!WARNING]
> Heutige Browser rufen Webseiten teilweise mehrfach auf (aufgrund von Prefetch und anderen Techniken). Dabei ist sicherzustellen, das die gewünschten Funktionen problemlos doppelt ausgeführt werden können (zum Beispiel, wenn der Ressourcenverbrauch einer Datenbankabfrage vernachlässigbar ist). Ansonsten ist dies durch ein Transaktionsmodell sicherzustellen.

> [!NOTE]
> Diese Beispiele beziehen sich auf die Grundfunktionalitäten des DE-Servers. Die anwendungspezifischen erweiterten Funktionalitäten werden gesondert beschrieben. Zum Beispiel [hier](xref:ppsn.mod.programming) für PPSn.

### Eingebetteter Code

#### Zugriff auf .Net

```html

<html>
  <body>
    The time of request was: <% print(clr.System.DateTime:Now) %>.
  </body>
</html>

```

### Lua Dateien

#### Erzeugen des Dokumentes

```lua

otext('text/html');
print("<html><body>The time of request was:");
print(clr.System.DateTime:Now);
print("</body></html>")

```

#### Ausliefern einer Binärdatei

```lua

obinary("image/bmp");
local bin = clr.System.Byte[](
    66, 77, 66,  0,0,0,0,0,0,0,62,0,0,0,40,0,
    0,0,8,0,0,0,1,0,0,0,1,0,1,0,0,0,
    0,0,4,0,0,0,0,0,0,0,0,0,0,0,0,0,
    0,0,0,0,0,0,0,0,0,0,255,255,255,0,119,0,
    0,0);

out.Write(bin,0,#bin);

```

### Einführende Codeschnipsel

#### Lesen einer Datei

```lua

local file = io.open("test.txt", "r");
print("<hr/>" .. file:read("*a"));

```

> [!IMPORTANT]
> Der Dateipfad is relativ zur DE-Server.exe anzugeben - nicht zur .lua/.html Datei!

#### Fehlermeldungen in das Log schreiben

```lua

Log.Info("Logeintrag");

```

#### Zugriff auf Konfigurationsknoten des DE-Servers

```lua

UseNode(
  "/Http",
  function (node)
    node.Log.Info("HttpLogeintrag");
  end
);

```

#### Ausführen einer externen Anwendung

```lua

local ipcfg = io.popen("ipconfig /all","r");
print(ipcfg:read("*a"));

```

> [!NOTE]
> Bei POpen ist auf das Fileobjekt kein Close() auszuführen.

#### Rückgabe des Contents als Return

Der Content kann auch mittels einem Return ausgegeben werden:

```lua

local result = "1+2 = ".. (1+2);

return result,"text/html";

```

Dabei ist der zweite Parameter der Contenttype.  
Natürlich können so auch beliebige Daten mit frei wählbarem Contenttype ausgeliefert werden:

```lua

local file = io.open("logo.svg", "r");

return file:read("*a"),"image/svg+xml";

```

> [!IMPORTANT]
> Die Angabe des Contenttypes ist zwingend erforderlich!

#### Setzen von Informationen im Antwortheader

Hier exemplarisch an einem _303 See Other_ gezeigt.

```lua

Context.SetStatus(clr.System.Net.HttpStatusCode.SeeOther,"We can't find Your information.");
Context.OutputHeaders["Location"]="https://google.com";
return;

```

#### Cookies

##### Setzen eines Cokkies

```lua

Context.OutputCookies.Add(clr.System.Net.Cookie("userId","1337"));

```

##### Abrufen eines Cookies

```lua

local userId = Context.InputCookies["userID"].Value;

```

#### Anzeigen in der bevorzugten Sprache des Nutzers

```lua

local found,acceptedlanguages = Context.TryGetProperty("Accept-Language");
if (found) then
  local german = acceptedlanguages.IndexOf("de");
  local english = acceptedlanguages.IndexOf("en");
  if german >= 0 and english < 0 then
    return io.open("index_de.html", "r").read("*a"), "text/html";
  end;
  if english >= 0 and german < 0 then
    return io.open("index_en.html", "r").read("*a"), "text/html";
  end;
  if german < english then
    return io.open("index_de.html", "r").read("*a"), "text/html";
  end;
  if german > english then
    return io.open("index_en.html", "r").read("*a"), "text/html";
  end;
end;
return io.open("index_en.html", "r").read("*a"), "text/html";

```

#### Hardware-Anbindung

Für die Anbindung von Hardware sollten grundsätzlich Services genutzt werden, es ist jedoch auch möglich, über die .Net Schnittstelle, auf Hardware zuzugreifen.

```lua

function ReadSerial()
    local port;
    do 
      port = clr.System.IO.Ports.SerialPort("COM5", 9600, clr.System.IO.Ports.Parity.None, 8, clr.System.IO.Ports.StopBits.One);
      port.Open();
      return port.ReadExisting();
    end(
      function(e)
        Log.Except(e);
      end,
      function
        if port ~= nil then
          port.Close();
        end;
      end
    )
    return;
end;

```

> [!WARNING]
> Heutige Browser rufen Webseiten teilweise mehrfach auf (aufgrund von Prefetch und anderen Techniken). Dabei ist sicherzustellen, das Hardware in der Regel nur von einem Prozess gesteuert werden kann / nicht gleiche Befehle doppelt ausgeführt werden.

