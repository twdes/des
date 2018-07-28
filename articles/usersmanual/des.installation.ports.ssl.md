1. Der Fingerabdruck eines gültigen, installierten Zertifikates muss vorliegen.
   > [!TIP]
   > Der Fingerabdruck eines installierten Zertifikates kann folgendermaßen überprüft werden:
   > ```bash
   > dir cert:\\localmachine\\[Zerfifikatname]
   > ```
   
   > [!TIP]
   > Ein selbtssigniertes Zertifikat kann folgendermaßen erstellt werden:
   > ```bash
   > New-SelfSignedCertificate -CertStoreLocation Cert:\LocalMachine\My -DnsName [DNSName des Servers]
   > ```
   > Und wird folgendermaßen exportiert
   > ```bash
   > Export-PfxCertificate -cert Cert:\LocalMachine\My\[Zertifikatfingerabdruck] -FilePath C:\DEServer\Client\owncert.pfx -Password $(convertto-securestring -string "[Zertifikatpasswort]" -asplaintext -force)
   > ```
   > Auf dem Client dieses Zertifikat für <i>Lokaler Computer</i> im Zertifikatspeicher <i>Vertrauenswürdige Stammzertifizierungsstellen</i> ablegen.
1. Die URL für DE-Server freigeben
   ```bash
   netsh http add urlacl url=http://+:443/ user=[DES-Benutzername] listen=yes 
   ```
1. Das Zertifikat in der HTTPsys installieren
   ```bash
   netsh http add sslcert ipport=0.0.0.0:443 certhash=[Zertifikatfingerabdruck] appid="{$(New-Guid)}"
   ```
1. Den Port in der Firewall freigeben
   ```bash
   netsh advfirewall firewall add rule name="DE-Server" dir=in protocol=TCP localport=443 action=allow
   ```