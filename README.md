# AVD Health Check

A single `.exe` for checking an Azure Virtual Desktop session host. Double-click it. It tests the Microsoft endpoints AVD and the Windows App depend on, collects the VM's key facts, and runs a speed test. **Copy report** puts the results on the clipboard, ready to paste into a ServiceNow incident.

- No install, no admin rights, no runtime to add. It uses .NET Framework 4.8, which ships with Windows 10 and 11.
- One file, about 190 KB.
- Nothing is sent anywhere except the test endpoints listed below. Results stay on the machine until someone copies them.

---

## What it checks

### Connectivity

25 endpoints in five groups: **Identity**, **Azure Virtual Desktop service**, **Azure platform**, **Windows App**, and **Microsoft 365**. The list is in [config/endpoints.json](config/endpoints.json).

| Type | How it is tested | Pass means |
| --- | --- | --- |
| `https` / `http` | A real request through the **system proxy** (PAC/WPAD), using the signed-in user's credentials, the same way the user's apps connect. | The service answered with any HTTP status. A 404 from the broker still proves the path works. |
| `tcp` | A direct TCP connect. This cannot go through a web proxy. | The port opened. |
| `imds` | Azure Instance Metadata Service at `169.254.169.254`. Always direct. | The VM is in Azure and can read its own metadata. |

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

Download, upload, latency, and jitter, measured through the system proxy against [Cloudflare's speed test endpoints](https://speed.cloudflare.com). These are the same endpoints that page uses, and they need no account or token. The report includes the Cloudflare data centre that answered (for example `LHR`).

**Data use and rate limits:** Cloudflare limits test traffic **per public IP**. Every VM behind the corporate proxy shares that IP, so each run is capped at 150 MB down and 40 MB up, or 6 seconds per direction, whichever comes first. If Cloudflare still answers with HTTP 429, the app says how long to wait instead of showing a raw error.

**Using a different target:** set `downloadUrl` to any large file that answers a GET (for example a blob in an approved storage account) and `uploadUrl` to anything that accepts a POST, or leave `uploadUrl` empty to skip upload.

fast.com is not used. It has no public API: its test servers are only available through a token taken from its own web app.

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

Run `build.cmd`. It uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so there is nothing to install. The output is `dist\AVDHealthCheck.exe`.

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
| `src/Checks/SystemInfoCollector.cs` | WMI, registry, Windows Update history, and Azure metadata |
| `src/Checks/SpeedTester.cs` | Latency, download, and upload with data caps |
| `src/Report.cs` | Plain-text report for ServiceNow |
| `config/endpoints.json` | Endpoint list and speed test settings |
| `assets/` | Horse logo and app icon |
