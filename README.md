# AVD Health Check

A single `.exe` for checking an Azure Virtual Desktop session host. Double-click it. It tests the Microsoft endpoints AVD and the Windows App depend on, collects the VM's key facts, and runs a speed test. **Copy report** puts the results on the clipboard, ready to paste into a ServiceNow incident.

- No install, no admin rights, no runtime to add. It uses .NET Framework 4.8, which ships with Windows 10 and 11.
- One file, about 190 KB.
- Nothing is sent anywhere except the test endpoints listed below. Results stay on the machine until someone copies them.

---

## What it checks

### Client app

The **Connection** group shows which app the user connected with:

| Client | Result |
| --- | --- |
| **Windows App** | Green. This is the client users should be on. |
| **Remote Desktop app** (the MSI or the old Store app) | Warning: an old, outdated client replaced by Windows App. |
| **Web client** (browser) | Warning: degraded experience. Redirection is limited (no local drives or USB devices, PDF-only printing) and there is no Teams media optimisation. |
| **Remote Desktop Connection** (`mstsc`) | Warning: not the supported way into AVD. |

**How it is detected:** Windows does not record the client type on the VM. AVD only logs it centrally, in the Azure Monitor `WVDConnections` table. The app instead reads what the RDP client reports to the session: its **install path**, build number, and device name. Rules match on the install path (Windows App is the `MicrosoftCorporationII.Windows365` Store package), and the first match wins.

The rules are in `clientApp.rules` in `endpoints.json`. The row always shows the raw values (device, build, client path). If a client is not recognised, or is labelled wrongly, connect once with that client, read the client path from the report, and add or adjust a rule. You do not need to rebuild.

The **This device** card also shows **Connected from**: the user's device name and, when reported, its address.

### Prisma Access

Shown as a green/red card plus two rows.

| Row | Green when |
| --- | --- |
| **Prisma Access Agent** | The service that runs `PASrv.exe` is running. The service is found by its executable, so its name does not matter. The row shows the service name, start mode, and agent version (from the file version of `PASrv.exe`). |
| **Prisma tunnel** | A network adapter named like **PA Virtual Ethernet Adapter Secure** is up and has an IPv4 address. |

The card shows **Connected**, **No tunnel**, **Stopped**, or **Not found**. The adapter name patterns are in `prisma.adapterMatch` in `endpoints.json`. If no adapter matches, the row lists the virtual adapters it can see, so a renamed adapter is easy to spot.

### Teams media optimisation

Shown as a green/red card plus four rows.

| Row | What it checks |
| --- | --- |
| **Media optimisation** | The verdict. New Teams writes its VDI mode to its own logs (`%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs`), as a word (`slimcore`, `webrtc`) or an undocumented number. Numbers follow the observed pattern "second digit = media stack": `x2xx` is **SlimCore**, so `5200` on AVD (confirmed against Teams *Settings > About*) shows **Optimised**; `x1xx` is legacy **WebRTC** (`3100`), which shows **Fallback**. `currentStack`/`connectedStack = local` shows **Not optimised**: media is running inside the VM. Exact codes can be pinned in `teams.slimCoreModes` / `teams.webRtcModes` in `endpoints.json`. Only logs written since Teams started are read. |
| **New Teams** | `ms-teams.exe` is running in *this user's* session (multi-session hosts run one copy per user), and its version. |
| **VDI registry flag** | `HKLM\SOFTWARE\Microsoft\Teams\IsWVDEnvironment = 1`. Without it, Teams never optimises. |
| **WebRTC Redirector (fallback)** | The Remote Desktop WebRTC Redirector service (`MsRdcWebRTCSvc.exe`) is installed and running. Microsoft recommends keeping it as the fallback when SlimCore is not available on the user's device. |

The card shows **Optimised**, **Fallback**, **Not optimised**, **Unconfirmed**, **Teams closed**, or **N/A**:

- **Unconfirmed:** Teams is running but has not logged a VDI mode yet (for example straight after sign-in). In Teams, *Settings > About* should say "AVD SlimCore Media Optimized".
- **N/A:** the app is not running in a remote session.

### Connectivity

22 endpoints in five groups: **Identity**, **Azure Virtual Desktop service**, **Azure platform**, **Windows App**, and **Microsoft 365**. The list is in [config/endpoints.json](config/endpoints.json).

| Type | How it is tested | Pass means |
| --- | --- | --- |
| `https` / `http` | A real request through the **system proxy** (PAC/WPAD), using the signed-in user's credentials, the same way the user's apps connect. | The service answered with any HTTP status. A 404 from the broker still proves the path works. |
| `tcp` | A direct TCP connect. This cannot go through a web proxy. | The port opened. |
| `imds` | Azure Instance Metadata Service at `169.254.169.254`. Always direct. Not in the default list; the **This device** card still reads it quietly for the VM size, region, and VM ID. | The VM is in Azure and can read its own metadata. |

Results are marked **pass**, **warning**, or **fail**:

- **TLS inspected:** the endpoint answered, but its certificate was not issued by a public certificate authority. Usually this means the proxy is decrypting the traffic. Microsoft does not support TLS inspection for AVD traffic, so this is shown as a warning.
- **Slow response:** the endpoint took longer than 1.5 s (`slowResponseMs`).
- **HTTP 407:** the proxy wanted credentials it did not get.
- An endpoint with `"required": false` shows as a warning when it fails, not a fail.

### This device

- Device name, domain, and signed-in user.
- Session type (console or remote/AVD).
- OS edition, version, and build.
- Up time and last boot.
- Last OS update (KB, month, install date), including whether it is still **waiting for a restart**.
- Restart pending.
- Defender intelligence date.
- BIOS serial number. Virtual machines often have none, and the report says so.
- Model.
- Azure VM size, region, and image, resource group, and VM ID. These come from Azure Instance Metadata.
- CPU, memory, disk C:, and IP address.

### Speed test

Download, upload, latency, and jitter, measured through the system proxy so they reflect the user's real path.

**Method (like fast.com):** 8 parallel connections. The first 1.5 s is ignored while connections ramp up. Each direction runs until the rate levels off (three 1-second readings within 5%, after at least 3 s), or until 10 s or the data ceiling (1.5 GB down, 400 MB up), whichever comes first. On a 1.6 Gbps line it measured 1671 Mbps, against 1.6 Gbps on fast.com. Slower links stop sooner and use far less data.

**Providers, tried in order:**

1. **Cloudflare** (the `speed.cloudflare.com` endpoints). Highest capacity, and the report shows the data centre (for example `LHR`). Cloudflare **rate-limits by data volume per public IP**, and every VM behind the proxy shares that IP.
2. **LibreSpeed**, the open-source speed test with a public server list. It only runs the parts Cloudflare refused (latency, download, or upload). It picks the fastest-answering of London, Amsterdam, and Frankfurt. Every request carries a unique query so the proxy cannot serve a cached copy.

When a fallback is used, the speed card says **fallback** and the report lists what was refused, for example `Cloudflare LHR download: rate-limited (HTTP 429, about 27 min)`. A reading cut short (under 2 s) is never reported; the next provider measures it again.

**Guaranteed results:** for a target that is never rate-limited, host a LibreSpeed server (a PHP or Go backend) in the bank's own Azure. Add it to `speedTest.providers` in `endpoints.json` as `{ "type": "librespeed", "name": "LBG", "fallbackServers": ["https://..."] }` and put it first. Any plain file URL also works as a `"type": "fixed"` provider.

Third-party speed test sites are not embedded: their results cannot be read into the report, and Ookla's terms do not allow commercial embedding. fast.com is not used either: it has no public API, and its test servers are only available through a token taken from its web app.

---

## Using it

1. Copy `AVDHealthCheck.exe` to the VM and double-click it. Checks start straight away and take about 15–30 seconds.
2. Read the summary cards and the **Connectivity** list. If anything failed, **Show issues only** hides the passes.
3. Select **Copy report** (or press Ctrl+C) and paste it into the incident. The report lists problems first, then device facts and every endpoint.
4. Select **Run again** (or press F5) after a fix.

Single values in **This device** can also be selected and copied.

---

## Changing the endpoint list

A built-in copy of `config/endpoints.json` is embedded in the exe. To change the list **without rebuilding**, put an `endpoints.json` next to `AVDHealthCheck.exe`. The footer shows which list was used. If the file next to the exe cannot be read, the app falls back to the built-in list and says why.

```json
{ "name": "RD Gateway", "host": "rdgateway.wvd.microsoft.com", "type": "https" },
{ "name": "Windows activation (KMS)", "host": "azkms.core.windows.net", "port": 1688, "type": "tcp" },
{ "name": "Microsoft Teams", "host": "teams.microsoft.com", "type": "https", "required": false }
```

`publicIssuers` lists the certificate issuers treated as public CAs. Any other issuer is reported as TLS inspection.

---

## Building

Run `build.cmd`. It uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so there is nothing to install. The output is `dist\AVDHealthCheck.exe`. If the app is open, the exe is locked; build somewhere else with `build.cmd dist\AVDHealthCheck-next.exe`.

Because of that compiler, the code is written for **C# 5**: no `$"..."` string interpolation, no `?.`, no `=>` property bodies, no `await` inside `catch`. The window is loaded from XAML at runtime (`XamlReader`) rather than compiled, so the WPF build tasks are not needed either.

`tools/make-icon.ps1` rebuilds `assets/app.ico` from `tools/icon.html` using Edge. You only need it if the icon design changes.

### Running it inside the bank

The exe is **not code-signed**. Windows Defender Application Control, AppLocker, and Smart App Control may block unsigned or unknown executables (Smart App Control blocked a rebuild on the development PC). Before rolling it out, either:

- sign it with the bank's code-signing certificate (`signtool sign /fd SHA256 ...`), or
- ask for an allow rule (publisher, hash, or path) for the AVD estate.

---

## Files

| Path | What it is |
| --- | --- |
| `src/Program.cs` | Entry point and connection limits |
| `src/MainController.cs` | Loads the window, runs the checks, and builds the summary |
| `src/Ui/MainWindow.xaml` | Layout and styling (Incident Lab palette: Lloyds black and Grove green `#006a4d`) |
| `src/Checks/EndpointChecker.cs` | HTTPS/HTTP through the proxy, TLS inspection detection, TCP, and IMDS |
| `src/Checks/AgentChecker.cs` | Prisma Access Agent (service + tunnel) and Teams media optimisation |
| `src/Checks/SystemInfoCollector.cs` | WMI, registry, Windows Update history, and Azure metadata |
| `src/Checks/SpeedTester.cs` | Latency, download, and upload with data caps |
| `src/Report.cs` | Plain-text report for ServiceNow |
| `config/endpoints.json` | Endpoint list and speed test settings |
| `assets/` | Horse logo and app icon |
