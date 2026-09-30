# SaleTracking runtime architecture

The inspected application is a single ASP.NET Core / .NET 8 process with server-rendered MVC pages, a shared price/stock processor, an in-process background scheduler, local SQLite persistence, and a separately managed Node.js / Chromium WhatsApp bridge. External runtime dependencies are retailer websites, the configured SMTP provider, and WhatsApp Web.

The diagram contains **12 components, 12 relationships, four boundary frames, and six supporting cards**. The components are logical runtime responsibilities, not twelve separately deployed services.

## Primary path

An authenticated customer chooses **Check Now**. `DashboardController` passes the tracker ID and current owner email to the singleton `PriceTrackingProcessor`. The processor obtains its shared lock, resolves the owned tracker, fetches product data through the scraper, records the result, then evaluates price/stock and scheduling conditions. When an alert is eligible, it invokes the selected channel adapters. The highlighted example continues through the WhatsApp adapter, the authenticated local bridge, and WhatsApp Web submission.

The periodic worker is a second entrance into the same processor. It scans every five seconds and asks the store for trackers whose own check interval has elapsed. Tracker creation persists the tracker without immediately sending an alert.

Successful channels receive individual SQLite receipts. Failed channels remain eligible for retry without resending successful channels. Full notification status is recorded only after every selected channel succeeds. The bridge also journals stable alert references before sending, and blocks automatic replay after an uncertain send. These are submission acknowledgements, not delivery/read guarantees or a distributed exactly-once guarantee.

## Component evidence

Source links target this local checkout. Line ranges in the descriptions are inclusive.

| Component | Responsibility and inspected source |
| --- | --- |
| Customer / Admin browser | MVC entry and authenticated dashboard interaction: [AccountController](D:/SaleTracking/SaleTracking/Controllers/AccountController.cs:144), [DashboardController](D:/SaleTracking/SaleTracking/Controllers/DashboardController.cs:9); notification polling: [notifications.js](D:/SaleTracking/SaleTracking/wwwroot/js/notifications.js:150). |
| Web UI & Access | MVC registration and cookie middleware: [Program.cs](D:/SaleTracking/SaleTracking/Program.cs:11); principal validation: [AccountCookieEvents](D:/SaleTracking/SaleTracking/Services/AccountCookieEvents.cs:29); admin controls: [WhatsAppController](D:/SaleTracking/SaleTracking/Controllers/WhatsAppController.cs:7), [EmailController](D:/SaleTracking/SaleTracking/Controllers/EmailController.cs:34). |
| Tracking Worker | Five-second timer calling the shared processor: [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:301), lines 301–316. |
| Tracking Processor | Shared semaphore, due checks and scrape call: [PriceTrackingProcessor](D:/SaleTracking/SaleTracking/Services/PriceTrackingProcessor.cs:5), lines 5–97; conditions, selected channels and receipts: lines 99–286. Schedule rules: [TrackingSchedule](D:/SaleTracking/SaleTracking/Services/TrackingSchedule.cs:12). |
| Product Scraper | HTTP fetch and extraction: [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:29), lines 29–140; Shopify product JSON and variant selection: [ShopifyProductScraping](D:/SaleTracking/SaleTracking/Services/ShopifyProductScraping.cs:14). |
| Retailer Websites | External requests use the tracker URL and derived catalog/product paths: [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:40), [ShopifyProductScraping.cs](D:/SaleTracking/SaleTracking/Services/ShopifyProductScraping.cs:16). No fixed retailer service is assumed. |
| Alert Adapters | Selection and dispatch: [PriceTrackingProcessor.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingProcessor.cs:146); [WhatsAppWebNotificationService](D:/SaleTracking/SaleTracking/Services/WhatsAppWebNotificationService.cs:170), [EmailNotificationService](D:/SaleTracking/SaleTracking/Services/EmailNotificationService.cs:77), [InAppNotificationService](D:/SaleTracking/SaleTracking/Services/InAppNotificationService.cs:83). |
| SQLite + Stores | DI registrations: [Program.cs](D:/SaleTracking/SaleTracking/Program.cs:12); WAL/schema initialization: [SqliteDatabase](D:/SaleTracking/SaleTracking/Services/SqliteDatabase.cs:30); account writes: [SqliteAccountStore](D:/SaleTracking/SaleTracking/Services/SqliteAccountStore.cs:28); tracker/receipt writes: [SqliteTrackingStore](D:/SaleTracking/SaleTracking/Services/SqliteTrackingStore.cs:15); in-app table: [InAppNotificationService](D:/SaleTracking/SaleTracking/Services/InAppNotificationService.cs:55). |
| SMTP Provider | Configurable host, port and TLS: [EmailNotificationService.cs](D:/SaleTracking/SaleTracking/Services/EmailNotificationService.cs:10), lines 10–47. Gmail is a default, not a required fixed dependency. |
| WhatsApp Bridge | App-owned Node lifecycle: [WhatsAppBridgeWorker](D:/SaleTracking/SaleTracking/Services/WhatsAppBridgeWorker.cs:8); LocalAuth and headless Chromium: [server.js](D:/SaleTracking/WhatsAppBridge/server.js:47); HTTP API: [http-server.js](D:/SaleTracking/WhatsAppBridge/http-server.js:5). |
| Bridge Private Files | Token generation and data directory: [server.js](D:/SaleTracking/WhatsAppBridge/server.js:10), lines 10–20; linked session: line 48; receipt journal configuration: line 236; journal writes and uncertain-send handling: [sender.js](D:/SaleTracking/WhatsAppBridge/sender.js:14), lines 14–103. |
| WhatsApp Network | External linked-device client and message submission: [server.js](D:/SaleTracking/WhatsAppBridge/server.js:47), [sender.js](D:/SaleTracking/WhatsAppBridge/sender.js:80). Uses the dependency declared in [package.json](D:/SaleTracking/WhatsAppBridge/package.json:12). |

## Relationship evidence

| Diagram relationship | Observed call or I/O |
| --- | --- |
| Browser → Web | MVC routes, static assets and authentication middleware in [Program.cs](D:/SaleTracking/SaleTracking/Program.cs:58). HTTP(S) reflects development HTTP plus application HTTPS redirection; no deployed TLS terminator is inferred. |
| Web → Processor | `CheckSingleTrackAsync` in [DashboardController.cs](D:/SaleTracking/SaleTracking/Controllers/DashboardController.cs:135), lines 135–142. |
| Worker → Processor | `CheckDueTracksAsync` in [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:311). |
| Processor → Scraper | `ScrapeProductAsync` / `GetCurrentPriceAsync` in [PriceTrackingProcessor.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingProcessor.cs:58). |
| Scraper → Retailers | `HttpClient.SendAsync` in [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:51); [ShopifyProductScraping.cs](D:/SaleTracking/SaleTracking/Services/ShopifyProductScraping.cs:25). The arrow represents a request/response dependency; responses are not duplicated as extra edges. |
| Processor → Adapters | Eligibility gate at [PriceTrackingProcessor.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingProcessor.cs:136), with selection at 146–160 and dispatch at 169–234. |
| Web → SQLite + Stores | `tracks.Create` in [CreateController.cs](D:/SaleTracking/SaleTracking/Controllers/CreateController.cs:129), resolved to the SQL writer in [SqliteTrackingStore.cs](D:/SaleTracking/SaleTracking/Services/SqliteTrackingStore.cs:15). Account store is also part of this grouped persistence node. |
| Processor → SQLite + Stores | Due/read/check/receipt calls resolve through the registered store to [SqliteTrackingStore.cs](D:/SaleTracking/SaleTracking/Services/SqliteTrackingStore.cs:80), lines 80–150. |
| Adapters → SMTP | Configured `SmtpClient.SendMailAsync` in [EmailNotificationService.cs](D:/SaleTracking/SaleTracking/Services/EmailNotificationService.cs:34), lines 34–47. |
| Adapters → Bridge | Loopback address validation and bearer header: [WhatsAppWebNotificationService.cs](D:/SaleTracking/SaleTracking/Services/WhatsAppWebNotificationService.cs:23), lines 23–27 and 79–83; `POST /api/messages`: lines 204–213. |
| Bridge → Private files | LocalAuth, token creation and journal persistence in [server.js](D:/SaleTracking/WhatsAppBridge/server.js:10) and [sender.js](D:/SaleTracking/WhatsAppBridge/sender.js:21). The app also reads the shared token; this supporting access is documented rather than drawn. |
| Bridge → WhatsApp | Client submission in [sender.js](D:/SaleTracking/WhatsAppBridge/sender.js:80). Message ID is a submission receipt. |

## Trust boundaries and qualifications

- **Local host** groups the default application, managed bridge and local storage deployment. It expresses ownership and location, not a demonstrated network firewall or container sandbox. The ASP.NET modules share one process; Node owns a separate Chromium process.
- **Browser / application boundary** checks cookie identity, tracker ownership and administrator role. Account, tracker and sender-control modifying forms use antiforgery. Notification read-state POST endpoints explicitly opt out with `IgnoreAntiforgeryToken`: [NotificationsController.cs](D:/SaleTracking/SaleTracking/Controllers/NotificationsController.cs:41).
- **App / bridge boundary** enforces loopback addressing, a shared bearer secret and Host/Origin checks: [WhatsAppWebNotificationService.cs](D:/SaleTracking/SaleTracking/Services/WhatsAppWebNotificationService.cs:23), [http-server.js](D:/SaleTracking/WhatsAppBridge/http-server.js:5), [server.js](D:/SaleTracking/WhatsAppBridge/server.js:251). The bridge uses HTTP on loopback. No direct browser-to-bridge edge exists.
- **Private persistence boundary** depends on operating-system filesystem permissions, outside public static files. The code does not encrypt the SQLite file or SMTP password stored in metadata: [ISmtpConfigurationStore.cs](D:/SaleTracking/SaleTracking/Services/ISmtpConfigurationStore.cs:99). The boundary is not an assertion that deployed ACLs were inspected.
- **External-service boundary** treats product URLs and downloaded HTML/JSON as untrusted. The inspected URL validator restricts scheme and credentials, but no private-network/DNS destination restriction was found in that path: [ProductUrlAttribute.cs](D:/SaleTracking/SaleTracking/Models/ProductUrlAttribute.cs:13), [PriceTrackingServices.cs](D:/SaleTracking/SaleTracking/Services/PriceTrackingServices.cs:40). Chromium is started with `--no-sandbox` in [server.js](D:/SaleTracking/WhatsAppBridge/server.js:53).

## Scope and provenance

Analysis date: 2026-09-29. Source root: `D:\SaleTracking`.

The folder has no `.git` repository, origin or commit ID. Archify's commit-based source verification cannot apply. To avoid inventing repository identity, the typed candidate omits `meta.repository` and machine `sources`; cards and this document preserve the source references. `source-manifest.json` fingerprints inspected files for this working-copy analysis; it is not a Git revision or runtime attestation.

`DevelopmentWhatsAppVerificationService` is registered for account signup and only logs verification codes. It is distinct from the real alert sender. The diagram does not infer a production deployment, reverse proxy, external queue, distributed scheduler or guarantee that external accounts are configured and reachable.

Application code and live account data were not modified. The application was not launched, and no retailer requests, email alerts or WhatsApp messages were sent for this analysis. Validation covers the diagram artifact and its browser rendering, not application integration tests.

## Artifacts

- `saletracking-runtime.html`: self-contained interactive Archify diagram.
- `candidate.json`: editable typed diagram specification.
- `source-manifest.json`: SHA-256 hashes of the local source files supporting this analysis.
- `review-2/saletracking-runtime.finalize-summary.json` (or the latest review receipt): artifact validation, hashes and browser evidence.
- `handoff.json`: final artifact identity and distinct validation / browser / visual review results.

Archify source was acquired from [tt-a1i/archify](https://github.com/tt-a1i/archify) into the ignored `.local/tools` directory. No global skill installation was required.

