CREATE TABLE Users (
    Id TEXT NOT NULL PRIMARY KEY,
    Name TEXT NOT NULL,
    Email TEXT NOT NULL COLLATE NOCASE UNIQUE,
    WhatsAppNumber TEXT NOT NULL DEFAULT '',
    PasswordHash TEXT NOT NULL,
    IsWhatsAppVerified INTEGER NOT NULL DEFAULT 0 CHECK (IsWhatsAppVerified IN (0, 1)),
    IsAdmin INTEGER NOT NULL DEFAULT 0 CHECK (IsAdmin IN (0, 1)),
    VerificationCode TEXT,
    CodeExpiresAt INTEGER,
    DefaultCheckIntervalMinutes INTEGER NOT NULL DEFAULT 1 CHECK (DefaultCheckIntervalMinutes BETWEEN 1 AND 10080),
    DefaultNotificationPreference TEXT NOT NULL DEFAULT 'WhatsApp'
);
CREATE UNIQUE INDEX IX_Users_InstallationAdmin ON Users(IsAdmin) WHERE IsAdmin = 1;

CREATE TABLE TrackingItems (
    TrackingId TEXT NOT NULL PRIMARY KEY,
    OwnerId TEXT NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    ProductUrl TEXT NOT NULL,
    TargetPrice TEXT NOT NULL,
    CheckIntervalMinutes INTEGER NOT NULL CHECK (CheckIntervalMinutes BETWEEN 1 AND 10080),
    StartDate TEXT,
    EndDate TEXT,
    IsActive INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0, 1)),
    IsNotified INTEGER NOT NULL DEFAULT 0 CHECK (IsNotified IN (0, 1)),
    DisableAfterNotification INTEGER NOT NULL DEFAULT 0 CHECK (DisableAfterNotification IN (0, 1)),
    CurrentPrice TEXT,
    LastCheckedAt INTEGER,
    NotifiedAt INTEGER,
    NotificationMessageSid TEXT,
    LastCheckError TEXT,
    LastAlertError TEXT,
    TargetWhatsAppNumber TEXT,
    CreatedAt INTEGER NOT NULL,
    CheckType TEXT NOT NULL DEFAULT 'TargetPrice',
    IsInStock INTEGER,
    StartTime TEXT,
    EndTime TEXT
);
CREATE INDEX IX_TrackingItems_OwnerCreated ON TrackingItems(OwnerId, CreatedAt DESC);
CREATE INDEX IX_TrackingItems_ActiveSchedule ON TrackingItems(IsActive, EndDate, StartDate);

CREATE TABLE ApplicationMetadata (
    Key TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL
);

CREATE TABLE InAppNotifications (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserEmail TEXT NOT NULL,
    TrackingId TEXT NOT NULL,
    ProductName TEXT,
    ProductUrl TEXT NOT NULL,
    CurrentPrice REAL NOT NULL,
    TargetPrice REAL NOT NULL,
    Message TEXT NOT NULL,
    IsRead INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_InAppNotifications_UserEmail ON InAppNotifications(UserEmail, CreatedAt DESC);
