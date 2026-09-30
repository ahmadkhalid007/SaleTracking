using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace SaleTracking.Services;

// Node remains an internal worker; ASP.NET owns its lifecycle and all browser access.
public sealed class WhatsAppBridgeWorker(IOptions<WhatsAppWebOptions> options,
    ILogger<WhatsAppBridgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var settings = options.Value;
        if (!settings.Enabled || !settings.AutoStart || !settings.HasValidAddress) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                // Never terminate or start a second copy over a separately managed service.
                if (await ExistingBridgeAsync(settings, stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    continue;
                }
                if (!File.Exists(Path.Combine(settings.BridgeDirectory, "server.js"))
                    || !Directory.Exists(Path.Combine(settings.BridgeDirectory, "node_modules", "whatsapp-web.js")))
                {
                    logger.LogError("WhatsApp dependencies are missing. Run WhatsAppBridge/start.ps1 -InstallOnly once, then restart SaleTrack.");
                    return;
                }
                var start = new ProcessStartInfo
                {
                    FileName = FindNode(settings.NodeExecutable), WorkingDirectory = settings.BridgeDirectory,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (File.Exists(Path.Combine(settings.BridgeDirectory, ".env")))
                    start.ArgumentList.Add("--env-file=.env");
                start.ArgumentList.Add("server.js");
                start.Environment["WHATSAPP_BRIDGE_PORT"] = new Uri(settings.BridgeUrl).Port.ToString();
                start.Environment["WHATSAPP_BRIDGE_TOKEN_FILE"] = settings.TokenFile;
                start.Environment["WHATSAPP_BRIDGE_DATA_DIR"] = settings.DataDirectory;
                start.Environment["PUPPETEER_CACHE_DIR"] = Path.Combine(settings.DataDirectory, "browser");
                start.Environment["WHATSAPP_PARENT_PID"] = Environment.ProcessId.ToString();
                // An empty override deliberately selects the shared token file over an old .env key.
                start.Environment["WHATSAPP_BRIDGE_API_KEY"] = settings.ApiKey;
                process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogDebug("WhatsApp: {Output}", e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogWarning("WhatsApp: {Output}", e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                logger.LogInformation("WhatsApp sender started in the background. Connect it in Settings.");
                await process.WaitForExitAsync(stoppingToken);
                logger.LogWarning("WhatsApp sender stopped with exit code {ExitCode}; retrying shortly.", process.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                logger.LogError(ex, "WhatsApp could not start. Install Node.js 22+ or configure WhatsApp:Web:NodeExecutable.");
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    process.Dispose();
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private static async Task<bool> ExistingBridgeAsync(WhatsAppWebOptions settings, CancellationToken cancellationToken)
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(settings.ApiKey) ? await File.ReadAllTextAsync(settings.TokenFile, cancellationToken) : settings.ApiKey;
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.BridgeUrl.TrimEnd('/') + "/api/connection");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException) { return false; }
    }

    private static string FindNode(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cuaNodeBase = Path.Combine(localAppData, "OpenAI", "Codex", "runtimes", "cua_node");
            if (Directory.Exists(cuaNodeBase))
            {
                foreach (var dir in Directory.GetDirectories(cuaNodeBase))
                {
                    var cand = Path.Combine(dir, "bin", "node.exe");
                    if (File.Exists(cand)) return cand;
                }
            }

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe")
            })
                if (File.Exists(candidate)) return candidate;
        }
        return "node";
    }
}
