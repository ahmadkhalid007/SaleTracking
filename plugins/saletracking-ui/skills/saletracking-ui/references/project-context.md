# SaleTracking project context

Observed in the project on 2026-09-28. This is a navigation aid; verify the current checkout before relying on these details. Resolve all paths relative to the repository root. The original workspace is `D:\SaleTracking`, but another checkout may have a different location.

## Product and stack

- Repository/application name: SaleTracking. Existing UI brand: SaleTrack.
- This is a product-price tracker with target-price alerts, not an order-processing or revenue analytics application.
- Web project: `SaleTracking/SaleTracking.csproj`, currently `net8.0`, ASP.NET Core MVC.
- Views: Razor `.cshtml`; styling: Bootstrap plus custom CSS; interactions: existing JavaScript.
- `Program.cs` registers controllers with views and maps a conventional MVC route with Account/Login as its default.
- `SaleTracking/Pages/` also exists. Do not assume it is the active UI: the application inspected uses the MVC `Views/` tree and its shared layout.
- Storage uses SQLite. Do not change database schema or notification services to accomplish a visual redesign unless the requested feature needs it.

## Screen map

| Screen | Primary view or files |
| --- | --- |
| Shared sidebar, topbar, notification menu | `SaleTracking/Views/Shared/_Layout.cshtml` |
| Shared alerts | `SaleTracking/Views/Shared/_Alerts.cshtml` |
| Dashboard | `SaleTracking/Views/Dashboard/Index.cshtml` |
| Tracked products | `SaleTracking/Views/Dashboard/TrackedProducts.cshtml` |
| Tracker details and editing | `SaleTracking/Views/Dashboard/Details.cshtml`, `Edit.cshtml` |
| Create tracker | `SaleTracking/Views/Create/Index.cshtml`, `NewSale.cshtml` |
| Login, registration, password reset, verification | `SaleTracking/Views/Account/` |
| Settings and sender configuration | `SaleTracking/Views/Settings/Index.cshtml`, `_EmailSender.cshtml`, `_WhatsAppSender.cshtml` |
| Shared styling | `SaleTracking/wwwroot/css/site.css` |
| WhatsApp sender styling | `SaleTracking/wwwroot/css/whatsapp-sender.css` |
| Shared and notification interactions | `SaleTracking/wwwroot/js/site.js`, `notifications.js` |
| Sender interactions | `SaleTracking/wwwroot/js/email-sender.js`, `whatsapp-sender.js` |

Read the corresponding `SaleTracking/Controllers/` and `SaleTracking/Models/` files to understand the screen's contract before editing.

## UI and data invariants

- Existing shared styling defines `--ink`, `--muted`, and `--blue`. Inspect their current values and later overrides rather than treating this snapshot as a new design specification.
- The authenticated shell includes sidebar navigation and a notification dropdown. Account views use a distinct authentication layout treatment.
- Admin-only settings and role labels must remain conditional. Hiding a control is not a substitute for server-side authorization.
- `TrackingItem` includes product URL, target price, nullable current price, check interval, optional start/end dates, active/notified flags, check and alert errors, and notification timestamps.
- The current model's notified status says `Alert submitted`. Do not turn that into `Delivered` or imply that the recipient read it.
- Preserve unknown/missing prices and errors as distinct states. Do not display a missing price as zero or use color alone to communicate status.
- User-specific data includes contact details and product URLs. Prefer synthetic values when creating Figma examples or screenshots intended for sharing.

## Local validation

- Build from the repository root: `dotnet build SaleTracking/SaleTracking.csproj`.
- Tests: `SaleTracking.Tests/SaleTracking.Tests.csproj`, observed targeting `net10.0`. Check installed SDKs before diagnosing a test-run failure as an application regression.
- `SaleTracking/Properties/launchSettings.json` contains `http` and `https` profiles. The observed HTTP URL is `http://localhost:5011`; verify the actual bound URL and port before browsing.
- `Program.cs` starts hosted price-tracking and WhatsApp workers and initializes SQLite. Inspect supported configuration and use isolated preview data before starting another instance; do not use live notification sending as a visual smoke test.
- Never include `.local/`, database files, SMTP credentials, WhatsApp sessions, personal account data, `bin/`, `obj/`, or `node_modules/` in design deliverables or plugin archives.
