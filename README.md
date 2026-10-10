# FreeMon

Network device availability monitoring for Windows and Linux: ping and service checks, a map of device families,
history and PDF reports, notifications to e-mail and messengers. Free and open source (MIT).

- **User manual:** [docs/MANUAL.md](docs/MANUAL.md) (Ukrainian: [docs/MANUAL.uk.md](docs/MANUAL.uk.md))
- **Building releases and publishing:** [docs/RELEASING.md](docs/RELEASING.md) (Ukrainian: [docs/RELEASING.uk.md](docs/RELEASING.uk.md))
- Українською: [README.uk.md](README.uk.md)

## What FreeMon does

### Monitoring
- **Ping (ICMP)** of IPv4 and IPv6 addresses and host names: interval, packet count and size, timeout and thresholds.
- **States:** Up, Unstable (packet loss or high latency), Down, Unreachable via parent, Disabled, Unknown.
  A state changes only after N consecutive checks with the new result — short glitches do not cause false alarms.
- **Main check without ping** for devices that do not answer ping: TCP port, HTTP(S) request, HTTP JSON value or DNS query.
- **Additional checks** of services on any device: TCP port, HTTP(S) (response codes, expected text, self-signed
  certificates), DNS (the device is queried as a DNS server), **HTTP JSON** — a value from a JSON response compared with
  conditions, e.g. a Home Assistant sensor state or any REST API.
- **Parameters on three levels:** global → device type → device.
- **Parent devices:** when a router is down, the devices behind it are "Unreachable via parent" and do not flood notifications.

### Interface
- **Map** of device families (parent in the center, children around it): drag markers and family cards,
  collapse families, "problems only" mode, open one family separately (⤢) with back navigation,
  hide frames and labels.
- **Tree** with latency, loss, state and its duration; tooltips with all details.
- **Events** panel at the bottom; the last event can be shown in the status bar.
- **Wall display:** the arrow at the top left collapses everything above the map except the state counters.
- **Right-click a device:** SSH and web connections (your own browser and SSH client), route trace (MTR),
  history, edit.
- Light and dark themes, tray icon (open / restart / exit), autostart, English and Ukrainian interface.

### History and reports
- History of a device for any period: availability, outages, incidents, latency and loss chart.
- **Monitoring gaps** (FreeMon not running, monitoring off) are hatched on the chart, listed in the report
  and excluded from availability.
- **PDF report** with a plain-language summary, chart, incidents, gaps, signature block and a verification code.
- **Raw log (TXT)** of every check, including additional checks.
- The report language can differ from the interface language.

### Notifications
- Popup in the screen corner.
- **Own sounds** for Down, Unstable and a quiet one for recovery; can be turned off per device.
- **Channels:** e-mail (SMTP) and webhooks with templates for Telegram, Virola, Node-RED, ntfy, Gotify,
  Mattermost / Slack. Per channel filters, per device switch, grouping of simultaneous changes, retries.

### Security and data
- **Access modes:** no restrictions; view mode with administrator mode by button; view mode with an administrator
  password. Auto-lock after inactivity.
- **Encryption:** the configuration and channel secrets are encrypted, the key is bound to the computer.
- **Integrity:** the check log is sealed every hour; with an administrator password the seals are also certified.
  Every report has a verification code that FreeMon can check.
- **Backups:** manual (with a password) and automatic on schedule; restore in the setup wizard on a new computer.
- Device list import and export (CSV).

### Network policy
FreeMon connects only to the addresses you configure: monitored devices, the DNS server for host names,
the SMTP server and webhook addresses. No updates, telemetry or downloads. See "About → Network policy".

## Requirements
- Windows 10 / 11 x64, or an x64 Linux distribution supported by .NET 10
  (Debian, Ubuntu, RHEL and compatible, Fedora, openSUSE / SLES).
- Linux: ping without root works through ICMP sockets (`net.ipv4.ping_group_range`), otherwise the system `ping` is used.
- Route trace uses the system tools: `mtr` / `tracepath` / `traceroute` on Linux, `pathping` / `tracert` on Windows.

## Downloads and installation
Releases: the **Releases** page of this repository.

| File | For |
| --- | --- |
| `freemon-<version>-win-x64-setup.exe` | Windows installer |
| `freemon-<version>-win-x64-portable.zip` | Windows portable: unpack and run `freemon.exe` |
| `freemon_<version>-1_amd64.deb` | Debian, Ubuntu: `sudo apt install ./freemon_<version>-1_amd64.deb` |
| `freemon-<version>-1.x86_64.rpm` | RHEL-compatible, Fedora: `sudo dnf install ./freemon-…rpm`; openSUSE: `sudo zypper install ./freemon-…rpm` |
| `freemon-<version>-linux-x64-portable.tar.gz` | any x64 Linux: unpack and run `./freemon` |

On first start the wizard asks for the languages, where to keep the data and the access mode;
it can also restore a backup.

## Where the data is
| Mode | Data folder |
| --- | --- |
| Only for me | Windows: `%APPDATA%\FreeMon`; Linux: `~/.local/share/FreeMon` |
| For all users (Windows) | `C:\ProgramData\FreeMon` |
| Portable | `data` next to the program |
| Custom folder | the folder you chose |

Personal settings (interface language, browser, SSH client, window state) are in `ui.json` in the user profile
(`%APPDATA%\FreeMon`, `~/.config/FreeMon`); in portable mode — in the data folder.

The data is encrypted and bound to the computer: **to move FreeMon to another computer use a backup** —
copying the folder is not enough. Make backups regularly.

## Building from source
Requires the .NET 10 SDK.
```
./build.sh              # Linux and Windows builds into dist/
./build.sh linux-x64    # only Linux
build.cmd               # on Windows (adds the icon and version to the exe)
./release.sh <repo-url> # all release files: see docs/RELEASING.md
```

## License
MIT License, © 2026 ITUASUPPORT. Third-party components are listed in `THIRD_PARTY_NOTICES.txt`.

FreeMon was developed with the help of AI tools under the author's direction. The software is provided "as is",
without warranty of any kind — see `LICENSE.txt`.
