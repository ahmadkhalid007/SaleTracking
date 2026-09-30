# Email Alerts inside SaleTrack (SMTP via Google Authenticator)

SaleTrack supports delivering rich, automated price drop alert emails to your customers whenever their monitored products drop to or below their target threshold.

Users can choose to receive alerts via **WhatsApp**, **Email**, or **Both (WhatsApp + Email)** simultaneously for maximum visibility.

---

## 1. Google Account & Authenticator Setup

Google accounts do not permit automated SMTP logins using standard account passwords. Google requires **2-Step Verification** and a dedicated **App Password**.

### Step-by-Step Instructions:

1. **Open Google Security:**
   Navigate to [Google Account Security](https://myaccount.google.com/security).

2. **Enable 2-Step Verification:**
   - Under the *"How you sign in to Google"* section, click on **2-Step Verification**.
   - Follow the prompts to configure it using the **Google Authenticator** app on your phone (scan the QR code in Google Authenticator and enter the 6-digit code).
   - Ensure 2-Step Verification shows as **Turned ON**.

3. **Generate an App Password:**
   - Once 2-Step Verification is active, search for **App passwords** in the top search bar, or visit [myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords).
   - Enter an App name, such as `SaleTrack Alerts`.
   - Click **Create**.
   - Google will display a **16-character password** (e.g. `abcd efgh ijkl mnop`).
   - Copy this 16-character password (spaces can be omitted or kept; SaleTrack trims them automatically).

---

## 2. Configuring Email in SaleTrack

You can configure your SMTP credentials either directly through the **Admin Web Interface** or via configuration files.

### Option A: Via Admin Settings (Recommended)

1. Sign in to SaleTrack with your **Admin Account**.
2. Navigate to **Settings** (`/Settings`).
3. Scroll to the **Email Sender (SMTP & Google Authenticator)** card.
4. Enter the configuration:
   - **SMTP Host:** `smtp.gmail.com`
   - **Port:** `587`
   - **Enable SSL / TLS:** Checked (`true`)
   - **Gmail / Sender Email:** Your Google email address (e.g. `youralerts@gmail.com`)
   - **Sender Display Name:** `SaleTrack Alerts`
   - **Google App Password:** Your 16-character generated App Password
5. Click **Save SMTP Settings**.
6. Use the **Test Google SMTP Email Delivery** tool below the form to send an instant test email and confirm that your setup is working.

### Option B: Via `appsettings.json` or Environment Variables

You can pre-configure credentials in `SaleTracking/appsettings.json`:

```json
{
  "Smtp": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "EnableSsl": true,
    "SenderEmail": "youralerts@gmail.com",
    "SenderName": "SaleTrack Alerts",
    "Username": "youralerts@gmail.com",
    "Password": "your-16-char-app-password"
  }
}
```

Or using environment variables (ideal for Docker / production deployments):
- `Smtp__Host=smtp.gmail.com`
- `Smtp__Port=587`
- `Smtp__EnableSsl=true`
- `Smtp__SenderEmail=youralerts@gmail.com`
- `Smtp__Password=your-16-char-app-password`

*Note:* Settings saved through the Admin Settings UI are persisted in the SQLite database (`ApplicationMetadata`) and take precedence over `appsettings.json` without requiring application restarts.

---

## 3. Multi-Channel Alert Behavior

When a tracked product is checked:

1. If the scraped price is **at or below** the user's `TargetPrice`:
2. SaleTrack reads the user's `DefaultNotificationPreference`:
   - **`WhatsApp`**: Submits a WhatsApp message via the connected WhatsApp sender.
   - **`Email`**: Sends a rich HTML alert email to the user's registered email address.
   - **`Both`**: Dispatches **both** WhatsApp message and Email alert!
3. **Resilience & Tracking:**
   - If both succeed, the tracker logs confirmation IDs for both channels (e.g., `WA:...; EMAIL:...`).
   - If one channel has a temporary issue (e.g. phone offline or recipient number issue), the other channel is still dispatched, and the partial failure is recorded in tracker diagnostics.
   - Once notified, duplicate protection prevents repeated alerts unless the user re-arms the tracker.

---

## 4. Troubleshooting Common SMTP Issues

| Issue / Error | Cause | Resolution |
| --- | --- | --- |
| **535: 5.7.8 Username and Password not accepted** | Using normal Gmail password instead of App Password, or 2FA was turned off. | Ensure 2-Step Verification is enabled in Google Account and generate a new 16-character App Password at `myaccount.google.com/apppasswords`. |
| **Connection timed out / Refused** | Firewall or ISP blocking outbound SMTP port 587. | Verify port 587 is open. Alternatively try Port 465 with SSL enabled. |
| **User does not receive email** | Notification preference is set to `WhatsApp` only, or email landed in Spam. | Verify the customer's profile setting is `Email` or `Both`, and check spam/junk folders. Add your sender address to contacts. |
