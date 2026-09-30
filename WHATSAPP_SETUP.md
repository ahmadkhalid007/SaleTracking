# WhatsApp sender inside SaleTrack

The WhatsApp sender is managed in **Settings → WhatsApp Sender**, visible only to the administrator. There is no separate connection page, browser tab, sidebar tab, or WhatsApp status banner on customer pages. The existing Node bridge now runs as an internal background worker owned by the ASP.NET application.

## First setup

1. Install .NET 8 and Node.js 22 or newer. From the repository root, install the bridge dependencies once:

   ```powershell
   .\WhatsAppBridge\start.ps1 -InstallOnly
   ```

2. Start SaleTracking from Visual Studio, or run:

   ```powershell
   dotnet run --project .\SaleTracking\SaleTracking.csproj --launch-profile http
   ```

3. Open `http://localhost:5011/Account/Login` and select **Admin Login**. This option is always visible. On the first local visit, it opens admin setup; enter your own name, email, and password. Later visits open the admin sign-in form, which takes you directly to WhatsApp settings. There is no default admin password. Initial account creation is available only over a direct loopback connection, before an administrator exists. Complete setup locally before placing the app behind a reverse proxy.
4. Setup signs you in and opens **Settings → WhatsApp Sender**. On the phone whose WhatsApp account should SEND alerts, open **WhatsApp → Settings / Menu → Linked devices → Link a device**, then scan the QR code shown inside SaleTrack.
5. Wait for **Connected** and confirm the displayed sender number. A previously saved WhatsApp session reconnects automatically, so a fresh QR is only needed if the session is no longer linked.

For later runs, start **only SaleTracking**. It starts the background bridge without a terminal or browser window. Closing Settings does not disconnect WhatsApp. The application must remain running and connected to the internet to send alerts.

To unlink the sender, use **Disconnect WhatsApp** on the connected sender card and confirm. This logs out the SaleTrack linked device and pauses WhatsApp alerts for all users; price monitoring continues. Use **Reconnect WhatsApp** and scan a new QR code to resume alerts. An alert already being submitted may still arrive.

The admin account is separate from customer registration. Customers keep their recipient numbers in their profiles; linking the sender does not change those numbers. Customer registration still uses the existing development verification-code flow.

## Administrator access and persistence

- Administrator and customer credentials (salted password hashes), profiles, and stable account IDs are saved in `.local/saletracking.db`. The former `.local/admin/account.json` is imported automatically once, without resetting the admin password.
- Public registration never grants admin access. QR/status management, reconnect, and disconnect endpoints require an authenticated admin. Reconnect and disconnect are POST requests protected by ASP.NET antiforgery validation.
- QR responses are not cached. Credentials stay on the server; no bridge token or direct bridge URL is placed in a web page.
- The old standalone bridge page and `/ui/*` endpoints have been removed. All bridge APIs require the shared server token and bind only to `127.0.0.1`.
- Back up the SQLite database and `.local/whatsapp` privately. Restrict filesystem access to the account running SaleTracking. A damaged legacy admin file fails closed during initial migration instead of reopening setup.
- Customer accounts, settings, verification status, trackers, schedules, prices, and alert results survive app restarts. See [SQLite setup](DATABASE_SETUP.md) for schema and backup details.

## Configuration

Default paths are relative to the ASP.NET content root (`SaleTracking/`). The source layout works as provided. For a deployment, copy the bridge and its installed dependencies to the configured directory and give the service account access to Node, Chrome, and the private data folders. A `dotnet publish` output alone does not contain the sibling bridge.

| ASP.NET configuration / environment variable | Default / purpose |
| --- | --- |
| `WhatsApp__Web__Enabled` | `true`; enables sender integration |
| `WhatsApp__Web__AutoStart` | `true`; runs bridge with the app; disable for an externally supervised bridge |
| `WhatsApp__Web__BridgeUrl` | `http://127.0.0.1:3210`; loopback HTTP only |
| `WhatsApp__Web__BridgeDirectory` | `../WhatsAppBridge` |
| `WhatsApp__Web__DataDirectory` | `../.local/whatsapp`; sessions, browser cache, receipts |
| `WhatsApp__Web__TokenFile` | `../.local/whatsapp/bridge-token` |
| `WhatsApp__Web__NodeExecutable` | Optional path to Node.js; defaults to installed Node, the local bundled runtime, or PATH |
| `WhatsApp__Web__ApiKey` | Optional server secret, at least 32 characters; normally use generated shared token |
| `Database__Path` | `../.local/saletracking.db`; account and tracker database |
| `Admin__AccountFile` | `../.local/admin/account.json`; legacy admin import source only |

The managed worker passes its port, token path, data path, and API key to Node so both sides use the same settings. `WhatsAppBridge/.env` may set `WHATSAPP_CHROME_PATH` (an existing Chrome executable) and `WHATSAPP_SEND_DELAY_MS` (1000–60000; default 3000). No credentials need to be copied into the browser.

`start.ps1` without `-InstallOnly` remains available for manual diagnostics. Do not keep an older bridge version running while upgrading: stop that old bridge once and let SaleTracking start the new one. A correctly configured existing bridge is reused and is not terminated by the application.

## Alert behavior

The worker checks due trackers every five seconds, respects their selected intervals, and sends when the scraped price is at or below the target. Creating a tracker does not immediately send a message. **Check Now** also respects active status and date range. Alerts use the track owner's current registered recipient number, and the existing receipt journal retains duplicate protection.

An accepted send is a submission confirmation, not a delivery/read receipt. Failed or uncertain sends remain visible in track details. Do not delete the receipt journal to force retries. The QR integration does not change the existing sender or price-scraping behavior.

## Troubleshooting

- **Preparing / unavailable:** Allow the first browser startup to finish. If it persists, run `start.ps1 -InstallOnly` and check the application's logs. Verify Node 22+ and the configured bridge path.
- **Needs attention:** Configure `WHATSAPP_CHROME_PATH` if a bundled browser is unavailable, restart SaleTracking, then use **Reconnect WhatsApp** in Settings.
- **Disconnected / link expired:** Click **Reconnect WhatsApp** in Settings and scan the refreshed QR. If needed, remove only the **SaleTrack Alerts** device on the phone before linking again.
- **No admin card:** Sign in using the installation admin account. Regular users cannot see or control the sender.
- **Admin sign-in:** Select **Admin Login** on the login screen any time. The old `/Account/AdminSetup` link also redirects to admin login once setup is complete.
- **Update required / older WhatsApp service:** An older standalone bridge can answer `/api/status` but return 404 for the integrated `/api/connection` endpoint. Stop that old bridge once; the running SaleTracking app will start the updated worker with the saved session. A saved, connected sender displays its number instead of a QR; a QR appears when WhatsApp needs linking.
- **Old screen still showing:** Stop/rebuild/restart the running ASP.NET app and refresh the browser. Accounts and trackers saved by the SQLite version persist across restarts.

The connector uses the existing unofficial `whatsapp-web.js` implementation. Its compatibility and account restrictions remain those of that library; this is not the official WhatsApp Business API.

## Verification

Automated tests use fake recipients/clients and do not send WhatsApp messages:

```powershell
dotnet test .\SaleTracking.Tests\SaleTracking.Tests.csproj --no-restore --output .verification\merged-whatsapp-tests
cd WhatsAppBridge
node --test --test-isolation=none
```
