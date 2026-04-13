# RollyRoll

**Open-source Windows fleet deployment and management platform.**

RollyRoll is a modern, all-in-one replacement for WDS, WSUS, and FOG Project — built natively for Windows Server with C#/.NET. Deploy images, manage patches, backup and restore PCs, and schedule fleet-wide operations from a single drag-and-drop web UI.

## What It Does

| Feature | Description |
|---------|-------------|
| **Image Deployment** | PXE boot + WIM imaging via DISM. Supports UEFI and Legacy BIOS. |
| **Backup & Restore** | Backups and deployable images are the same WIM file — deploy with or without user profiles. |
| **Patch Management** | Ring-based Windows Update rollout (Pilot -> Early Adopters -> Broad -> All). Third-party and driver updates. |
| **Auto-Recovery** | If a deployment or patch fails, automatically captures user profiles, rolls back, and re-injects profiles. Users can't tell the difference. |
| **Wake-on-LAN** | Power on PCs remotely. Multi-subnet support. |
| **Scheduling** | Timetable deployments with a calendar UI. Recurring tasks via cron expressions. |
| **Drag & Drop UI** | Blazor Server web UI with real-time SignalR updates. Drag images onto clients for instant deployment. |
| **Auto-Discovery** | Server and clients find each other automatically via broadcast/mDNS/DNS SRV. |
| **Self-Installing** | Single PowerShell script bootstraps everything on a fresh Windows Server. |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    RollyRoll Server (Windows Service)           │
│                                                                 │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────┐  │
│  │ TFTP     │ │ DHCP     │ │ Deploy   │ │ Blazor Web UI    │  │
│  │ Server   │ │ Proxy    │ │ Engine   │ │ (SignalR)        │  │
│  │ :69/UDP  │ │ :4011/UDP│ │          │ │ :443/TCP         │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────────┘  │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────┐  │
│  │ WoL      │ │ Patch    │ │ Recovery │ │ REST API         │  │
│  │ Service  │ │ Manager  │ │ Service  │ │ :8080/TCP        │  │
│  │ :9/UDP   │ │ (Rings)  │ │          │ │ (WinPE Agent)    │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ SQLite Database  │  Image Store (.wim)  │  USMT Profiles │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
         │                    │                      │
    PXE Boot             HTTPS/TLS              UDP Broadcast
    (iPXE)           (Image Transfer)         (Auto-Discovery)
         │                    │                      │
┌────────▼────────┐  ┌───────▼───────┐  ┌───────────▼──────────┐
│ WinPE Agent     │  │ Client Agent  │  │ Client Agent         │
│ (During deploy) │  │ (PC #1)       │  │ (PC #2)              │
│ - Partition disk│  │ - Heartbeats  │  │ - Patch install      │
│ - Apply WIM     │  │ - Patch inst. │  │ - Health monitor     │
│ - Inject profs. │  │ - Recovery    │  │ - Auto-discovery     │
└─────────────────┘  └───────────────┘  └──────────────────────┘
```

## Quick Start

### Prerequisites
- Windows Server 2019 or later
- Administrator privileges
- Network connectivity

### Install
```powershell
# Clone the repository
git clone https://github.com/jakobeichberger/RollyRoll.git
cd RollyRoll

# Build
dotnet build RollyRoll.sln -c Release

# Publish
dotnet publish src/RollyRoll.WindowsService -c Release -r win-x64 --self-contained -o C:\RollyRoll\bin

# Run installer (sets up firewall, certs, directories, Windows Service)
.\installer\Install-RollyRoll.ps1
```

### First Steps
1. Open `https://localhost` in your browser
2. Configure your DHCP server to point PXE clients to this server:
   - **Option 66** (Boot Server): `<RollyRoll server IP>`
   - **Option 67** (Boot File): `ipxe/ipxe.efi` (UEFI) or `ipxe/undionly.kpxe` (BIOS)
3. Capture your first gold image from a reference PC
4. Drag the image onto a client in the web UI to deploy

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Backend | C# / .NET 8 (LTS), ASP.NET Core |
| Frontend | Blazor Server + SignalR |
| Database | SQLite (default) / SQL Server Express |
| Imaging | DISM API (WIM format) |
| PXE Boot | iPXE chainloading + custom TFTP/DHCP proxy |
| Scheduling | Hangfire |
| Boot Environment | Custom WinPE with embedded agent |

## Project Structure

```
RollyRoll/
├── src/
│   ├── RollyRoll.Core/              # Domain models, interfaces, enums
│   ├── RollyRoll.Infrastructure/     # Service implementations (TFTP, DHCP, DISM, WoL, etc.)
│   ├── RollyRoll.Web/               # Blazor Server web UI
│   ├── RollyRoll.WindowsService/    # Windows Service host
│   ├── RollyRoll.WinPEAgent/        # Agent running in WinPE during deployment
│   └── RollyRoll.ClientAgent/       # Agent running on managed PCs
├── installer/
│   ├── Install-RollyRoll.ps1        # Server installer
│   ├── Update-RollyRoll.ps1         # In-place updater with rollback
│   └── Build-WinPE.ps1              # WinPE image builder
├── tests/
└── RollyRoll.sln
```

## Unified Image Model

A core design principle: **backups and deployment images are the same WIM file.** The only difference is a deploy-time option:

- **Clean Deploy** — Wipe user profiles, deploy a fresh image
- **Restore With Profiles** — Apply the image and re-inject user profiles

This means every image capture is both a backup AND a deployable image.

## Patch Management

Ring-based rollout inspired by Windows Update for Business:

```
Ring 1: Pilot (5-10 key users)      → Deploy immediately
Ring 2: Early Adopters               → Deploy after X days if Ring 1 OK
Ring 3: Broad (most PCs)            → Deploy after Y days if Ring 2 OK
Ring 4: All                          → Final rollout
```

If a ring exceeds the failure threshold (e.g. >10%), rollout auto-pauses and alerts the admin.

## Auto-Recovery

If a deployment or patch causes a failure:

1. Client Agent detects the problem (boot failures, health check timeout)
2. **Before rollback**: captures current user profiles to the server
3. Rolls back to the last known-good image
4. **Re-injects the just-captured profiles** (not the snapshot's old profiles)
5. Result: the user sees their familiar desktop — seamless recovery

## Security

- HTTPS/TLS for all image transfers and API communication
- AES-256 encryption for user profile data at rest
- No plaintext credentials in answer files (avoids CVE-2026-0386)
- Mutual TLS between WinPE agent and server
- DHCP proxy mode — does not replace your existing DHCP server
- Audit logging on all destructive actions
- Windows Authentication integration (AD/NTLM)

## Comparison with FOG Project

| Feature | FOG Project | RollyRoll |
|---------|------------|-----------|
| Server OS | Linux only | **Windows Server** (native) |
| Imaging | Block-level (Partclone) | **WIM/DISM** (Windows native) |
| Secure Boot | Not supported | **Supported** (signed WinPE) |
| BitLocker | Not supported | **Supported** (DISM handles it) |
| Patch Management | None | **Ring-based rollout** |
| UI | PHP (dated) | **Blazor + SignalR** (modern, real-time) |
| Transfers | NFS (unencrypted) | **HTTPS/TLS** (encrypted) |
| Auto-Recovery | None | **Built-in** (transparent to users) |
| User Profiles | None | **USMT integration** |
| Auto-Discovery | Manual registration | **Broadcast/mDNS/DNS SRV** |

## Update

```powershell
.\installer\Update-RollyRoll.ps1 -LocalPath "path\to\new\build"
```

The updater automatically backs up, migrates the database, and rolls back if the health check fails.

## Contributing

RollyRoll is open source under the MIT License. Contributions welcome!

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Run tests: `dotnet test`
5. Submit a pull request

## License

MIT License. See [LICENSE](LICENSE) for details.
