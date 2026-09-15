# Windows MSIX-pakking

MSIX-prosjektet ligger i [`src/MISX-Install`](../../src/MISX-Install) og pakker
`MdViewer.App` som en Windows-app med filtilknytning for `.md`, `.markdown`,
`.mdown` og `.mkd`.

## Bygge pakken

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/build-msix.ps1
```

Resultatet havner i `artifacts/msix/…_Test/…_x64.appxbundle`.

## Installere lokalt (usignert)

Pakken signeres ikke foreløpig. En usignert `.appxbundle` kan ikke
dobbeltklikkes — da får du feilen *«This app package's publisher certificate
could not be verified … (0x800B010A)»*. Registrer i stedet byggeutdataene
direkte, som hopper over signaturvalidering. Krever at **Utviklermodus** er slått
på under Innstillinger > System > For utviklere:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/install-dev.ps1
```

Avinstaller igjen med:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/install-dev.ps1 -Uninstall
```

Skriptet fjerner en eventuell tidligere registrering først; uten dette feiler
installasjonen med `0x80073CF3` når arkitektur eller utgiver har endret seg.

Signering med et kodesigneringssertifikat som matcher
`Publisher="CN=HansOlavSorteberg"` i `Package.appxmanifest` må på plass før
pakken kan distribueres til andre maskiner.

## Ikoner og fliser

Alle bildene under `src/MISX-Install/Images` genereres fra ikonmasteren:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/windows/build-msix-images.ps1
```

Kilden er `packaging/linux/hicolor/512x512/apps/mdviewer.png`, som igjen bygges
av `design/icon/build-icons.py`. Ikke rediger PNG-ene for hånd.
