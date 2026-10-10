# FreeMon — user manual

## 1. First start

On first start the setup wizard opens.

1. **Interface language** — the first line. The wizard is redrawn immediately.
2. **Report language** (PDF, raw log) — can differ from the interface language.
3. **Restore from backup…** — when moving FreeMon from another computer (see section 10).
4. **Where to store the data:**
   - **Only for me** — in your user profile;
   - **For all users** (Windows) — `C:\ProgramData\FreeMon`, one list for all users of the computer;
   - **Portable** — the `data` folder next to the program (USB drive);
   - **Custom folder**.
5. **Access to changes:**
   - **No restrictions** — anyone can change the list and settings;
   - **View mode, administrator by button** — protects against accidental changes;
   - **View mode, administrator with a password** — protects against outsiders.

"What FreeMon can do" opens the feature description, "Important about encryption" — short rules about the data.

## 2. Main window

- **Toolbar** (in groups):
  - **Device:** Add · Edit · On/off · Remove;
  - **Data ▾:** device list import and export (CSV), create a backup, restore;
  - **Reports:** History and reports;
  - **Program:** Settings · mode button (administrator / view).
- **State counters** at the top right: devices up, unstable, down, disabled, unknown.
- **"Map" and "Tree" tabs** — two views of the same list.
- **"Events"** at the bottom — the log of state changes. Collapsed with the arrow; when collapsed the last event is shown
  in the status bar.
- **Status bar:** "Data folder" link in administrator mode; "About" always.
- **Wall display:** the ⌃ arrow at the top left collapses everything above the map or tree, leaving only the counters.
  Click again (⌄) to expand. The state is remembered.

## 3. Devices

**Add** creates a device (the device selected in the tree or on the map becomes its parent).

- **Name, address** — IPv4, IPv6 or a host name.
- **Type** — sets the icon and default ping parameters.
- **Parent device** — the one this device is connected through (router, switch). When the parent is down,
  its children are shown as "Unreachable via parent".
- **Group, description** — free text; the description is shown in the tooltip.
- **Monitoring enabled** — a disabled device is not checked.
- **Send events to channels** — e-mail and webhooks for this device.
- **Play sounds** — sound notifications for this device.
- **How to determine the state:** ping (default) or — for devices that do not answer ping — a TCP port, an HTTP(S)
  request or a DNS query. Then no ping is sent at all.
- **Ping parameters:** an empty field inherits from the type or the global settings.
- **Connections** — SSH and web links for the right-click menu (the port is filled in automatically; for web you can add
  a path, e.g. `settings`).
- **Additional checks** — TCP port, HTTP(S), DNS, HTTP JSON. They have their own states and events and are shown as a colored
  dot on the device icon. They are not in the PDF report but are in the log and the raw log.
- **HTTP JSON** — a GET request; a value is taken from the JSON response by a path and compared with conditions:
  "Up when…", otherwise "Unstable when…" (optional), otherwise "Down". Comparisons: exists, =, ≠, contains, <, ≤, >, ≥,
  between, regex. Path: `state`, `attributes.temperature`, `data[0].value`.
  Home Assistant example: URL `http://<HA>:8123/api/states/sensor.<name>`, header `Authorization: Bearer <long-lived token>`
  (HA user profile → Security → Long-lived access tokens), path `state`, "Up when the value < 30", "otherwise Unstable when < 40".
  "Check now" shows the value received.

**On/off** quickly stops or resumes monitoring. **Remove** deletes the device from the list (its history stays in the log).

## 4. States and parameters

| State | Meaning |
| --- | --- |
| Up | replies arrive, loss and latency within thresholds |
| Unstable | packet loss or latency above the threshold |
| Down | loss at least the "Down" threshold (100 % by default) |
| Unreachable via parent | the device does not answer and its parent is down too |
| Disabled / Unknown | monitoring off / no result yet |

A state changes only after several consecutive checks with the same new result ("Consecutive checks to change the state").
Parameters are set on three levels: **global** ("Settings → General") → **device type** ("Settings → Device types") →
**device**. An empty field inherits the level above.

## 5. Map

- Every **family** is a card: the parent in the center, children around it; "Standalone devices" have neither parent nor children.
- **Buttons in the card header** (right side):
  - **←** — back (only in a family opened separately);
  - **⤢** — open only this family;
  - **↺** — restore the marker positions of the family (administrator mode);
  - **▾ / ▸** — collapse / expand.
- **Navigation:** when a family is opened separately, "‹ Back · All families › …" appears under the map toolbar.
- **Dragging** (administrator mode): markers inside a card; cards by their header.
  **"↺ Arrange families automatically"** removes saved card positions.
- **Modes:** all expanded / problems only / all collapsed; the "family frames and labels" box.
- **Map layout origin** (top left, top center, center) — "Settings → General".

## 6. Notifications

**"Settings → Notifications":**
- **popup** in the bottom right corner: for which changes, after how many seconds it closes;
- **sounds:** for "Down" / "Unstable" and a separate quiet recovery sound; ▶ buttons to listen;
- **master switch for channels** (e-mail, webhooks).

**"Settings → Channels"** — where to send changes:
- **E-mail (SMTP)** — server, port, encryption (STARTTLS / TLS), user, password, from, to.
- **Webhook** — templates: Telegram, Virola, Node-RED / JSON, ntfy, Gotify, Mattermost / Slack, custom.
  Placeholders: `{text}`, `{device}`, `{address}`, `{from}`, `{to}`, `{status_code}`, `{time}` and more (listed in the channel dialog).
- **"Send test message"** checks the channel right away.

Changes that happen almost at the same time are sent in one message; failed sends are retried.
**Note:** public services (Telegram, external e-mail) receive the names and addresses of your devices.

## 7. History and reports

**"History and reports"** (or right-click a device):
1. Choose the device and period (today, yesterday, 7 / 30 days, current / previous month, custom).
2. The plain-language summary, chart and incident list appear immediately.
3. **PDF report** — with organization, author, comment, signature block and a verification code.
4. **Raw log (TXT)** — every check, state changes, additional checks, monitoring gaps.

**Chart:** the line is the average latency, bars are packet loss, the strip at the bottom is the state.
**Hatched grey areas are monitoring gaps** (FreeMon was not running or monitoring of the device was off).
They are listed in the report (in the incident table and in a separate section) and excluded from availability.
**Chart zoom** is for viewing only, like a DVR timeline: the mouse wheel over the chart zooms in or out around the cursor,
dragging moves the chart left and right, a double-click or "Reset zoom" shows the whole period again. A bar above the chart
shows which part of the period is visible. The summary, incidents, PDF and TXT always cover the whole selected period.
After saving a PDF or TXT the program offers to open the file.

**Verify report…** — paste the code from a PDF ("Report authenticity"): the program recalculates the figures from the log
and tells whether the report is genuine and the data unchanged.

## 8. Security

- **View mode:** state, log and reports are visible; only the administrator can change the list and settings or exit
  the program. The mode button is on the toolbar.
- **Administrator password** and **auto-lock** — "Settings → Security". There you also choose whether "History and reports"
  and device connections are allowed in view mode.
- **Changes outside the program.** If the configuration was changed not by FreeMon, the administrator sees the list of
  changes at the next login and can roll them back.
- **Encryption:** the configuration, channel secrets and the key are bound to the computer.
  Details — "About → Encryption policy".

## 9. Personal settings

**"Settings → Personal"** — only for you on this computer (in view mode this is the only tab):
- interface language (takes effect after a restart);
- how to open web connections: default browser or a selected one (with a profile, e.g. `firefox -P Network {url}`);
- how to open SSH: system terminal, `ssh://` handler or a custom command (e.g. PuTTY).

## 10. Backups and moving to another computer

**"Settings → Backups"** or **"Data ▾"**:
- **Create backup…** — an `.fmbackup` file protected by a password: settings, lists, channel secrets and optionally the log.
- **Automatic backups** — daily or weekly into a folder (better on another disk or a network share). The automatic
  backup password is set once; backups are made without it, but can be opened only with it.
- **Restore from backup…** — completely replaces the current data (no merging) and restarts FreeMon.

**Moving to another computer:** make a backup → install FreeMon on the new computer → **"Restore from backup…"** in the
wizard. Copying the data folder does not work: it is encrypted with the key of the old computer.

## 11. CSV import and export

Format: `;` separator, UTF-8, the first line is the header. Required columns `name` and `address`; others: `type`,
`parent_address`, `group`, `description`, `enabled` (1/0) and ping parameters (`interval_s`, `packet_count`,
`packet_interval_ms`, `timeout_ms`, `packet_size`, `down_loss_pct`, `deg_loss_pct`, `deg_avg_ms`, `deg_max_ms`,
`deg_jitter_ms`, `confirm_cycles`). Example: `sample-devices.csv`. Import updates devices with the same address and adds
new ones. Main and additional checks and connections are not transferred via CSV.

## 12. Right-click menu

- **Connections** — the configured SSH and web links.
- **Route trace (MTR)…** — the route and loss on every hop, using system tools: `mtr` (Linux; otherwise `tracepath` /
  `traceroute`), `pathping` / `tracert` (Windows). openSUSE: `sudo zypper install mtr`; Debian / Ubuntu: `sudo apt install mtr-tiny`.
- **History and reports**, **Edit…** (administrator).

## 13. Tray and autostart

- The tray icon shows the worst state; the tooltip shows device counts by state.
- Menu: **Open**, **Restart** (administrator, with confirmation), **Exit** (in view mode — with confirmation or password).
- The window "Close" button hides the program to the tray (configurable). Starting FreeMon again opens the window of the
  running instance.
- **Start at login** — "Settings → General".

## 14. FAQ

- **Ping on Linux "does not work" without root.** FreeMon uses ICMP sockets allowed by the kernel parameter
  `net.ipv4.ping_group_range`; otherwise the system `ping`. The ping mode is shown in "About".
- **No sound.** Check that sounds are on in "Notifications" and for the device; Linux needs `paplay`, `pw-play` or `aplay`.
- **Windows warns about an unknown publisher.** The program is not code-signed: "More info → Run anyway".
- **The data "cannot be decrypted".** The folder was copied from another computer or the OS was reinstalled — restore from a
  backup in the wizard.
- **The log takes a lot of space.** Retention — "Settings → General" (30 days by default).
