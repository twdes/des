1. Ein gültiges Zertifikat muss vorliegen
2. Das Zertifikat muss sich im Zertifikatspeicher befinden
3. Der Fingerabdruck des Zertifikats muss bekannt sein
   PowerShell
   ```bash
   dir cert:\\localmachine\\[Zerfifikatname]
   ```
4. Unter VII.3. das Zertifikat mit dem Parameter <b>certhash=</b> den Fingerabdruck angeben und ggf. den Port auf 443 setzen