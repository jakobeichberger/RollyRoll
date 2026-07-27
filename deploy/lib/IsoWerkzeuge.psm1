<#
.SYNOPSIS
    Hilfsfunktionen zum Bauen von ISO-Abbildern unter Windows.

.DESCRIPTION
    Zwei Wege, damit auf einem Hyper-V-Server nichts nachinstalliert werden muss:

      1. oscdimg.exe aus dem Windows ADK, falls vorhanden – der zuverlaessigste Weg.
      2. IMAPI2 (die Brenn-Schnittstelle, die in jedem Windows steckt) als
         Rueckfallebene ohne jede zusaetzliche Software.

    Getestet mit Windows PowerShell 5.1 auf Windows Server 2019/2022/2025.
#>

# COM-Objekte (IMAPI2, ADODB) vertragen die strenge Eigenschaftspruefung nicht.
Set-StrictMode -Version 1.0

# ------------------------------------------------------------------ #
# Hilfsklasse: IStream in eine Datei schreiben
# ------------------------------------------------------------------ #
function Initialize-IsoSchreiber {
    if ('SchulMonitoring.IsoSchreiber' -as [type]) { return }

    $quelltext = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SchulMonitoring
{
    public static class IsoSchreiber
    {
        public static void Schreiben(string pfad, object stream, int blockGroesse, int bloecke)
        {
            IStream quelle = stream as IStream;
            if (quelle == null) throw new ArgumentException("Kein gueltiger IStream.");

            IntPtr gelesenZeiger = Marshal.AllocHGlobal(4);
            try
            {
                byte[] puffer = new byte[blockGroesse];
                using (FileStream ziel = File.Open(pfad, FileMode.Create, FileAccess.Write))
                {
                    while (bloecke-- > 0)
                    {
                        quelle.Read(puffer, blockGroesse, gelesenZeiger);
                        int gelesen = Marshal.ReadInt32(gelesenZeiger);
                        if (gelesen <= 0) break;
                        ziel.Write(puffer, 0, gelesen);
                    }
                    ziel.Flush();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(gelesenZeiger);
            }
        }
    }
}
'@

    Add-Type -TypeDefinition $quelltext -ErrorAction Stop
}

# ------------------------------------------------------------------ #
# oscdimg suchen
# ------------------------------------------------------------------ #
function Find-Oscdimg {
    <#
    .SYNOPSIS
        Liefert den Pfad zu oscdimg.exe, falls das Windows ADK installiert ist.
    #>
    [CmdletBinding()]
    param()

    $befehl = Get-Command 'oscdimg.exe' -ErrorAction SilentlyContinue
    if ($befehl) { return $befehl.Source }

    $kandidaten = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
        "${env:ProgramFiles}\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
        "${env:ProgramFiles(x86)}\Windows Kits\8.1\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
    )
    foreach ($pfad in $kandidaten) {
        if ($pfad -and (Test-Path -LiteralPath $pfad)) { return $pfad }
    }
    return $null
}

# ------------------------------------------------------------------ #
# ISO mit IMAPI2 bauen
# ------------------------------------------------------------------ #
function New-IsoMitImapi {
    <#
    .SYNOPSIS
        Baut ein ISO-Abbild aus einem Verzeichnis – nur mit Bordmitteln.

    .PARAMETER Quellverzeichnis
        Inhalt, der ins Abbild soll.

    .PARAMETER Zieldatei
        Pfad der zu erzeugenden .iso-Datei.

    .PARAMETER Datentraegername
        Datentraegerbezeichnung (max. 32 Zeichen).

    .PARAMETER EfiStartabbild
        Optional: Pfad zu einem EFI-Startabbild (bei Ubuntu boot\grub\efi.img).
        Fehlt der Wert, entsteht ein reines Datenabbild.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Quellverzeichnis,
        [Parameter(Mandatory)][string]$Zieldatei,
        [Parameter(Mandatory)][string]$Datentraegername,
        [string]$EfiStartabbild
    )

    Initialize-IsoSchreiber

    $abbild = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    try {
        # 1 = ISO9660, 2 = Joliet, 4 = UDF
        $abbild.FileSystemsToCreate = 7
        $abbild.UDFRevision = 0x102
        $abbild.VolumeName = $Datentraegername.Substring(0, [Math]::Min(32, $Datentraegername.Length))
        $abbild.FreeMediaBlocks = 0   # 0 = keine Groessenbegrenzung

        if ($EfiStartabbild) {
            if (-not (Test-Path -LiteralPath $EfiStartabbild)) {
                throw "EFI-Startabbild nicht gefunden: $EfiStartabbild"
            }

            $adodb = New-Object -ComObject ADODB.Stream
            $adodb.Open()
            $adodb.Type = 1                      # binaer
            $adodb.LoadFromFile($EfiStartabbild)
            $adodb.Position = 0

            $start = New-Object -ComObject IMAPI2FS.BootOptions
            $start.AssignBootImage($adodb)
            $start.PlatformId = 0xEF             # EFI
            $start.Emulation = 0                 # keine Emulation
            $start.Manufacturer = 'SchulMonitoring'

            $abbild.BootImageOptions = $start
            Write-Verbose "EFI-Startabbild eingebunden: $EfiStartabbild"
        }

        $abbild.Root.AddTree($Quellverzeichnis, $false)

        $ergebnis = $abbild.CreateResultImage()
        [SchulMonitoring.IsoSchreiber]::Schreiben(
            $Zieldatei, $ergebnis.ImageStream, $ergebnis.BlockSize, $ergebnis.TotalBlocks)
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($abbild)
        [GC]::Collect()
    }

    if (-not (Test-Path -LiteralPath $Zieldatei)) {
        throw "Das ISO-Abbild wurde nicht erzeugt: $Zieldatei"
    }
    return (Get-Item -LiteralPath $Zieldatei)
}

# ------------------------------------------------------------------ #
# ISO bauen – nimmt automatisch den besten verfuegbaren Weg
# ------------------------------------------------------------------ #
function New-BootfaehigesIso {
    <#
    .SYNOPSIS
        Baut ein UEFI-startfaehiges ISO. Nutzt oscdimg, falls vorhanden,
        sonst IMAPI2.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Quellverzeichnis,
        [Parameter(Mandatory)][string]$Zieldatei,
        [Parameter(Mandatory)][string]$Datentraegername,
        [string]$EfiStartabbild
    )

    if (Test-Path -LiteralPath $Zieldatei) {
        Remove-Item -LiteralPath $Zieldatei -Force
    }

    $oscdimg = Find-Oscdimg
    if ($oscdimg) {
        Write-Verbose "Verwende oscdimg: $oscdimg"

        $argumente = @('-m', '-o', '-u2', '-udfver102', "-l$Datentraegername")
        if ($EfiStartabbild -and (Test-Path -LiteralPath $EfiStartabbild)) {
            # b = Startabbild, e = keine Emulation (nur EFI, kein BIOS-Start noetig)
            $argumente += "-bootdata:1#pEF,e,b$EfiStartabbild"
        }
        $argumente += @($Quellverzeichnis, $Zieldatei)

        $ausgabe = & $oscdimg @argumente 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $Zieldatei)) {
            return (Get-Item -LiteralPath $Zieldatei)
        }
        Write-Warning "oscdimg ist fehlgeschlagen (Code $LASTEXITCODE), versuche es mit Bordmitteln."
        Write-Verbose ($ausgabe | Out-String)
    }

    return New-IsoMitImapi -Quellverzeichnis $Quellverzeichnis -Zieldatei $Zieldatei `
        -Datentraegername $Datentraegername -EfiStartabbild $EfiStartabbild
}

# ------------------------------------------------------------------ #
# Datenabbild (ohne Startfaehigkeit) – z. B. eine cloud-init Seed-CD
# ------------------------------------------------------------------ #
function New-DatenIso {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Quellverzeichnis,
        [Parameter(Mandatory)][string]$Zieldatei,
        [string]$Datentraegername = 'CIDATA'
    )

    if (Test-Path -LiteralPath $Zieldatei) { Remove-Item -LiteralPath $Zieldatei -Force }

    return New-IsoMitImapi -Quellverzeichnis $Quellverzeichnis -Zieldatei $Zieldatei `
        -Datentraegername $Datentraegername
}

# ------------------------------------------------------------------ #
# ISO entpacken
# ------------------------------------------------------------------ #
function Expand-Iso {
    <#
    .SYNOPSIS
        Haengt ein ISO ein und kopiert den Inhalt in ein Arbeitsverzeichnis.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IsoPfad,
        [Parameter(Mandatory)][string]$Zielverzeichnis
    )

    if (Test-Path -LiteralPath $Zielverzeichnis) {
        Remove-Item -LiteralPath $Zielverzeichnis -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Zielverzeichnis -Force | Out-Null

    $abbild = Mount-DiskImage -ImagePath $IsoPfad -PassThru -ErrorAction Stop
    try {
        $laufwerk = ($abbild | Get-Volume).DriveLetter
        if (-not $laufwerk) { throw "Das ISO konnte nicht eingehaengt werden: $IsoPfad" }
        $quelle = "${laufwerk}:\"

        Write-Verbose "ISO eingehaengt unter $quelle – Inhalt wird kopiert"

        # robocopy ist deutlich schneller als Copy-Item bei tausenden Dateien.
        # Rueckgabewerte < 8 bedeuten Erfolg.
        $null = & robocopy $quelle $Zielverzeichnis /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
        if ($LASTEXITCODE -ge 8) {
            throw "Der Inhalt des ISO konnte nicht kopiert werden (robocopy-Code $LASTEXITCODE)."
        }
        $global:LASTEXITCODE = 0

        # Kopien von einem ISO sind schreibgeschuetzt – das muss weg,
        # sonst laesst sich die grub.cfg nicht anpassen.
        Get-ChildItem -LiteralPath $Zielverzeichnis -Recurse -File |
            ForEach-Object { $_.Attributes = 'Normal' }
    }
    finally {
        Dismount-DiskImage -ImagePath $IsoPfad -ErrorAction SilentlyContinue | Out-Null
    }

    return $Zielverzeichnis
}

Export-ModuleMember -Function Find-Oscdimg, New-IsoMitImapi, New-BootfaehigesIso, New-DatenIso, Expand-Iso
