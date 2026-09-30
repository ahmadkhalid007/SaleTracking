using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaleTracking.Services;

namespace SaleTracking.Controllers;

public sealed class SaveSmtpSettingsModel
{
    [Required, StringLength(120)]
    public string Host { get; set; } = "smtp.gmail.com";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    [Required, EmailAddress, StringLength(120)]
    public string SenderEmail { get; set; } = "";

    [StringLength(80)]
    public string SenderName { get; set; } = "SaleTrack Alerts";

    [DataType(DataType.Password)]
    public string? Password { get; set; }
}

public sealed class SendTestEmailModel
{
    [Required, EmailAddress]
    public string RecipientEmail { get; set; } = "";
}

[Authorize(Roles = "Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmailController(
    ISmtpConfigurationStore configStore,
    IEmailNotificationService emailService,
    ILogger<EmailController> logger) : Controller
{
    [HttpGet]
    public IActionResult Status()
    {
        var options = emailService.CurrentOptions;
        return Json(new
        {
            isConfigured = emailService.IsConfigured,
            host = options.Host,
            port = options.Port,
            enableSsl = options.EnableSsl,
            senderEmail = options.SenderEmail,
            senderName = options.SenderName,
            hasPassword = !string.IsNullOrWhiteSpace(options.Password)
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Save([FromBody] SaveSmtpSettingsModel model)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { success = false, message = "Invalid SMTP settings provided." });
        }

        var current = configStore.GetOptions();
        var updated = new SmtpOptions
        {
            Host = model.Host.Trim(),
            Port = model.Port,
            EnableSsl = model.EnableSsl,
            SenderEmail = model.SenderEmail.Trim(),
            SenderName = string.IsNullOrWhiteSpace(model.SenderName) ? "SaleTrack Alerts" : model.SenderName.Trim(),
            Username = model.SenderEmail.Trim(),
            // Retain existing password if blank in edit; strip spaces from 16-character App Passwords
            Password = !string.IsNullOrWhiteSpace(model.Password) ? model.Password.Replace(" ", "").Trim() : current.Password
        };

        configStore.SaveOptions(updated);

        return Ok(new
        {
            success = true,
            isConfigured = emailService.IsConfigured,
            message = "SMTP configuration saved successfully."
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Test([FromBody] SendTestEmailModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(model.RecipientEmail))
        {
            return BadRequest(new { success = false, message = "Please enter a valid recipient email address." });
        }

        if (!emailService.IsConfigured)
        {
            return BadRequest(new
            {
                success = false,
                message = "SMTP is not configured. Please fill in Host, Port, Sender Email, and your Google App Password first."
            });
        }

        try
        {
            await emailService.SendTestEmailAsync(model.RecipientEmail.Trim(), cancellationToken);
            return Ok(new
            {
                success = true,
                message = $"Test email sent successfully to {model.RecipientEmail.Trim()} via Google SMTP!"
            });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to send test email to {Recipient}", model.RecipientEmail);

            var friendlyMessage = ex.Message;
            if (friendlyMessage.Contains("535") || friendlyMessage.Contains("Authentication") || friendlyMessage.Contains("credentials", StringComparison.OrdinalIgnoreCase))
            {
                friendlyMessage = "Google SMTP authentication failed (Code 535). Make sure 2-Step Verification is enabled in your Google Account and you generated a 16-character App Password (not your personal Google account password).";
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = friendlyMessage
            });
        }
    }
}
