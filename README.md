# Dell Warranty Scanner

A standalone Windows desktop application that scans your network for Dell systems and retrieves warranty expiration dates for each one via the Dell TechDirect API. Includes a manual service tag lookup tab for querying individual systems or bulk lists.

---

## Features

- **Network Scan** — ping-sweeps a subnet, identifies Dell machines via WMI, and retrieves their service tags automatically
- **Service Tag Lookup** — paste in one or more service tags manually, or import them from a CSV file
- **CSV Import** — smart column picker for multi-column spreadsheets; auto-detects columns named "tag", "serial", or "asset"
- **Warranty Data** — pulls warranty status, end date, and days remaining from the Dell TechDirect API for up to 100 tags per request
- **Color-coded results** — green (active), yellow (expiring within 90 days), red (expired)
- **Export to CSV** — export results from either tab
- **Auto-detect IP range** — reads your local network adapter and pre-fills the subnet
- **Stop at any time** — cancel a running scan or lookup mid-flight and keep partial results
- **Standalone .exe** — no installation or .NET runtime required on the target machine

---

## Requirements

### Dell TechDirect API Credentials (free)

Warranty lookups require a free API key from Dell TechDirect:

1. Go to **[https://tdm.dell.com](https://tdm.dell.com)** and sign in with a Dell account
2. Navigate to **API Management → My Applications**
3. Create a new application and select the **Warranty** API
4. Copy your **Client ID** and **Client Secret**
5. In the app: **Tools → Settings** → paste them in

Credentials are saved to `%APPDATA%\DellWarrantyScanner\settings.json`.

### WMI Access (for Network Scan)

The network scan uses WMI to query remote Windows machines for manufacturer and service tag. The account running the scanner needs remote WMI access to the target machines (typically a domain admin account). If your current Windows session already has that access, no extra credentials are needed. Alternate credentials can be configured in **Tools → Settings**.

---

## Usage

### Network Scan Tab

1. Enter an IP range in any of these formats:
   - `192.168.1.0/24` — CIDR notation
   - `10.0.0.1-254` — short range (last octet)
   - `10.0.0.1-10.0.0.100` — full range
2. Click **Auto-detect** to pre-fill the range from your local adapter
3. Click **Scan Network**
4. Results appear as they're found; click **Stop Scan** to halt early

### Service Tag Lookup Tab

1. Type tags directly into the text box — one per line, or comma/space separated
2. **Or** click **Import from CSV...** to load tags from a spreadsheet:
   - Single-column files are imported automatically
   - Multi-column files show a column picker with a live preview
3. Click **Look Up Warranty**

### Export

Click **Export CSV** (toolbar) or **File → Export to CSV...** to save results from the active tab.

---

## Building from Source

**.NET 9 SDK** is required ([download](https://dotnet.microsoft.com/download)).

```bash
git clone https://github.com/kbaker827/DellWarrantyScanner.git
cd DellWarrantyScanner

# Run directly
dotnet run

# Build a self-contained single .exe
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish
```

The compiled exe will be at `publish\DellWarrantyScanner.exe` (~115 MB, no dependencies).

> **Icon generation** — the app icon is pre-built at `Resources/app.ico`. To regenerate it, run the helper project:
> ```bash
> cd IconGen
> dotnet run -- ../Resources/app.ico
> ```

---

## Project Structure

```
DellWarrantyScanner/
├── Program.cs                  # Entry point
├── MainForm.cs                 # Main window and all UI logic
├── SettingsForm.cs             # Settings dialog
├── Models/
│   ├── AppSettings.cs          # Settings persistence
│   └── DeviceInfo.cs           # Result row model
├── Services/
│   ├── NetworkScanner.cs       # Ping sweep, WMI queries, subnet detection
│   └── DellWarrantyService.cs  # Dell TechDirect OAuth2 + warranty API
├── Resources/
│   └── app.ico                 # Embedded application icon
└── IconGen/                    # Small utility that generates app.ico
    ├── IconGen.csproj
    └── Program.cs
```

---

## License

MIT License — see [LICENSE](LICENSE) for details.
