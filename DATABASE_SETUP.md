# SaleTracking SQLite database

SaleTracking now stores customer accounts, the installation administrator, and product trackers in SQLite using Microsoft's `Microsoft.Data.Sqlite` provider. No separate database server is needed.

## Location and startup

Default database: **`D:\SaleTracking\.local\saletracking.db`** for this checkout.

`Database:Path` in `SaleTracking/appsettings.json` is resolved against the ASP.NET content root. Override it with `Database__Path` when deploying. Use a private, writable local directory outside `wwwroot`; keep the same database path across upgrades and restarts.

```powershell
dotnet restore .\SaleTracking\SaleTracking.csproj
dotnet run --project .\SaleTracking\SaleTracking.csproj --launch-profile http
```

The app creates the database and applies the embedded versioned schema before accepting requests or starting background workers. `PRAGMA user_version` records the schema version. Startup refuses to open a database from a newer schema version instead of overwriting it.

## Tables

See [Database ERD and structure](DATABASE_ERD.md) for the relationship diagram, all columns, keys, defaults, constraints, and indexes. The editable Mermaid diagram is in [DATABASE_ERD.mmd](DATABASE_ERD.mmd).

| Table | Stored data |
| --- | --- |
| `Users` | Stable account ID, unique email, name, recipient number, salted password hash, admin role, verification code/expiry/status, and tracking/notification defaults |
| `TrackingItems` | Owner ID, URL, target/current prices, check interval, date range, active/notified state, last check/error, alert receipt/error, recipient snapshot, creation and notification times |
| `ApplicationMetadata` | Migration markers, including the former admin-file import |

The initial schema is in `SaleTracking/Database/Migrations/001_Initial.sql`. Track ownership uses `Users.Id` as a foreign key, so changing an email preserves all tracks. Queries that display, edit, pause, or delete a tracker still enforce its owner. Prices are stored as decimal text to avoid floating-point rounding; schedule timestamps use UTC ticks to preserve precise check times and existing alert references.

## Existing accounts and WhatsApp

On the first database startup, the app imports `.local/admin/account.json` if present, preserving its account ID and password hash. `Admin:AccountFile` remains available to specify a different **legacy import source**. The former file is left intact as a backup; once the import is recorded, future logins and updates use SQLite and never reload an old password from that file.

A damaged legacy admin file stops initial migration rather than opening admin setup over an existing account. Restore the file from a known backup and start again. If no previous admin exists, use the always-visible **Admin Login** option to complete initial local setup.

Accounts and trackers that existed only in a previous process's memory cannot be recovered after that process stopped. All accounts and trackers created with this version persist through app restarts. Verification is also saved and its code can be consumed only once before expiry. The existing demo verification behavior remains unchanged.

WhatsApp linked-device credentials and the message receipt journal remain in `.local/whatsapp`, managed by the bridge. They are not copied into SQL. Database-backed alert status and stable references prevent a successful alert from being sent again after an app restart.

## Backup and deployment

- Stop SaleTracking before making a plain file copy of `saletracking.db`. SQLite uses WAL mode; if `-wal` and `-shm` sidecars remain, keep them with the database. Use a SQLite-aware online backup tool if the app must remain running.
- Back up `.local/whatsapp` separately to retain the sender connection and duplicate-send journal.
- Restrict access to the database directory. Passwords are hashed, but the SQLite file itself is not encrypted and includes account contact details.
- Database files and sidecars are ignored by Git. Do not place or publish them with static web assets.
- This setup uses one SaleTracking application instance with a local SQLite database. Multiple distributed app instances need coordinated scheduling and storage beyond this change.

## Tests

```powershell
dotnet test .\SaleTracking.Tests\SaleTracking.Tests.csproj
```

Database tests use temporary files and fake notification services. They cover restart persistence, admin migration, verification expiry/replay, ownership and email changes, schedule/receipt persistence, concurrent writes, and edits during background checks. No real WhatsApp messages are sent by the tests.

Provider reference: [Microsoft.Data.Sqlite documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/).
