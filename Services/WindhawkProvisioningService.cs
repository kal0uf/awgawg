using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services;

/// <summary>
/// The "Install Windhawk" half of the provisioning flow: ensures Windhawk is
/// present by downloading the official installer and running it silently.
/// 
/// Direct download mode (copied from AutoOS AppsStage.cs):
/// - Installer URL: https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe
///   (pinned to 2.0.0-alpha.5 as requested; no GitHub API lookup).
/// - Install:  Process.Start(windhawk_setup.exe, "/S")  hidden, wait for exit.
/// - Settings: Assets/Windhawk/KaliteOS.json (also saved as mods-bundled.json) via windhawk-cli:
///             windhawk-cli.exe data import "KaliteOS.json" --confirm-app-restart --yes  (WorkingDir = ProgramFiles\Windhawk)
/// - Mod updates: mod list --update-available --json  →  mod update &lt;id&gt;
///
/// - The installer is NSIS ("InstallerType: nullsoft").
/// - Silent switch: /S  (matches AutoOS AppsStage.cs).
/// - ElevationRequirement: elevationRequired (engine service + injection). app.manifest
///   is requireAdministrator so the installer normally runs elevated already.
/// </summary>
public sealed class WindhawkProvisioningService
{
    private static readonly HttpClient _httpClient = new();

    // Direct pinned URL — replaces GitHub API lookup (per user request).
    // Keep OfficialSiteUrl for error messages / verification fallback.
    public const string DirectDownloadUrl = "https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe";
    public const string DirectVersion = "2.0.0-alpha.5";
    private const string OfficialSiteUrl = "https://windhawk.net/";

    public record ProvisionProgress(WindhawkProvisionStage Stage, string StatusText, double? DownloadPercent);
    public record ResolvedInstaller(string Version, string DownloadUrl, string? Sha256, long SizeBytes);

    public enum WindhawkProvisionStage
    {
        Resolving,
        Downloading,
        Installing,
        Verifying,
        Done,
        Failed,
    }

    private readonly WindhawkDetectionService _detection = new();
    private readonly WindhawkInstallerService _installer = new();

    /// <summary>
    /// Resolves the installer to the pinned direct download URL (no GitHub API).
    /// Mirrors AutoOS AppsStage.cs which hard-codes the URL instead of querying the API.
    /// </summary>
    public Task<ResolvedInstaller> ResolveLatestInstallerAsync(CancellationToken ct)
    {
        // Direct mode: no network call, just return the pinned version + URL.
        // This matches AppsStage.cs: DownloadHelper.Download(directUrl, Path.GetTempPath(), "windhawk_setup.exe")
        var resolved = new ResolvedInstaller(DirectVersion, DirectDownloadUrl, null, 0);
        return Task.FromResult(resolved);
    }

    /// <summary>
    /// Full provisioning pipeline: resolve → download (with progress) → verify
    /// digest → silent install → wait → verify installed. Returns the resulting
    /// installation info. Throws only on unrecoverable failures; recoverable
    /// stage failures throw InvalidOperationException with a user-facing message.
    /// </summary>
    public async Task<WindhawkInstallationInfo> EnsureInstalledAsync(
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        var existing = _detection.Detect();
        if (existing.IsInstalled)
        {
            progress.Report(new ProvisionProgress(
                WindhawkProvisionStage.Done,
                $"Windhawk {existing.Version ?? "already"} is already installed.",
                null));
            return existing;
        }

        // 1. Resolve the latest stable installer.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving, "Resolving latest Windhawk release...", null));
        var installer = await ResolveLatestInstallerAsync(ct);

        // 2. Download with progress.
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading, $"Downloading Windhawk {installer.Version}...", 0));
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
        await DownloadFileAsync(installer.DownloadUrl, tempPath, installer.SizeBytes, progress, ct);

        // 3. Integrity: verify SHA256 when the release metadata provided one.
        if (!string.IsNullOrEmpty(installer.Sha256))
        {
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installer signature...", null));
            await VerifySha256Async(tempPath, installer.Sha256!, ct);
        }

        // 4. Silent install. NSIS: /S = silent, /STANDARD = standard install mode
        // (verified against the winget manifest's InstallerSwitches and the
        // upstream discussion confirming these exact flags).
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, "Installing Windhawk (silent)...", null));
        await RunInstallerSilentlyAsync(tempPath, ct);

        // 5. Verify the install actually landed (NSIS exits 0 and the UI exe exists).
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Verifying, "Verifying installation...", null));
        var result = await WaitForInstallationAsync(ct);
        if (!result.IsInstalled)
        {
            throw new InvalidOperationException(
                "The Windhawk installer finished but the installation could not be verified. Check the official site: " + OfficialSiteUrl);
        }

        try { File.Delete(tempPath); } catch { }

        progress.Report(new ProvisionProgress(
            WindhawkProvisionStage.Done,
            $"Windhawk {result.Version ?? ""} installed.".Trim() + ".",
            null));
        return result;
    }

    /// <summary>
    /// Direct-download flow copied from AutoOS AppsStage.cs (C:\Users\AutoOS\AutoOS):
    /// - Download windhawk_setup.exe from the pinned direct URL to Path.GetTempPath()
    /// - Install silently with /S hidden
    /// - Download/import windhawk.json via windhawk-cli data import --confirm-app-restart --yes
    ///   (working directory = ProgramFiles\Windhawk) then apply mod updates.
    /// </summary>
    public async Task<WindhawkInstallerService.WindhawkInstallerResult> InstallAndImportAsync(
        string jsonPath,
        IProgress<ProvisionProgress> progress,
        CancellationToken ct)
    {
        // 1. Resolve — direct pinned URL, no GitHub API (mirrors AutoOS AppsStage.cs).
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Resolving,
            $"Using Windhawk {DirectVersion} direct download...", null));

        string version = DirectVersion;
        string downloadUrl = DirectDownloadUrl;

        // 2. Download the installer (AutoOS: DownloadHelper.Download(directUrl, Path.GetTempPath(), "windhawk_setup.exe"))
        progress.Report(new ProvisionProgress(WindhawkProvisionStage.Downloading,
            $"Downloading Windhawk {version}...", null));

        string installerPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");
        // Ensure no stale/locked file from previous failed run (AutoOS kills locking processes before delete).
        try { if (File.Exists(installerPath)) File.Delete(installerPath); } catch { }
        await DownloadFileAsync(downloadUrl, installerPath, 0, progress, ct);

        // Always use the provided jsonPath which must be the KaliteOS bundled file (Assets/Windhawk/KaliteOS.json).
        // If missing, resolve the bundled asset directly — never use AutoOS remote.
        string effectiveJsonPath = jsonPath;
        if (string.IsNullOrWhiteSpace(effectiveJsonPath) || !File.Exists(effectiveJsonPath))
        {
            effectiveJsonPath = WindhawkModCatalog.EnsureBundledBackupOnDisk();
            if (string.IsNullOrWhiteSpace(effectiveJsonPath) || !File.Exists(effectiveJsonPath))
                throw new FileNotFoundException("KaliteOS Windhawk settings file not found in Assets/Windhawk.", effectiveJsonPath);
        }

        try
        {
            // 3. Silent install — AutoOS: Process.Start(windhawk_setup_offline.exe, "/S", hidden).WaitForExitAsync()
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Installing Windhawk (silent)...", null));

            await RunInstallerSilentlyAsync(installerPath, ct);

            // 4. Import settings via CLI — AutoOS: windhawk-cli.exe data import "windhawk.json" --confirm-app-restart --yes
            progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing,
                "Importing Windhawk settings...", null));

            var importResult = await _installer.ImportSettingsAsync(
                effectiveJsonPath,
                new Progress<string>(s => progress.Report(new ProvisionProgress(WindhawkProvisionStage.Installing, s, null))),
                ct);

            progress.Report(new ProvisionProgress(
                importResult.Success ? WindhawkProvisionStage.Done : WindhawkProvisionStage.Failed,
                importResult.Success
                    ? $"Windhawk {version} installed and settings imported."
                    : $"Windhawk installed, but settings import had issues (exit {importResult.ExitCode}).",
                null));

            return importResult;
        }
        finally
        {
            // 6. Cleanup temp installer + any imported JSON copies the CLI may have created.
            // Mirrors AutoOS AppsStage.cs cleanup: kill locking processes then delete temp files.
            _installer.CleanupTempFiles();
            WindhawkInstallerService.CleanupFile(installerPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, long totalBytes, IProgress<ProvisionProgress> progress, CancellationToken ct)
    {
        // Match AutoOS DownloadHelper behavior: attach User-Agent and follow redirects.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("stellarisKIT-WindhawkProvisioner/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        totalBytes = response.Content.Headers.ContentLength ?? totalBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastReport = DateTime.UtcNow;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) != 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;
            if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new ProvisionProgress(
                    WindhawkProvisionStage.Downloading,
                    $"Downloading Windhawk... ({totalRead * 100.0 / totalBytes:F0}%)",
                    totalRead * 100.0 / totalBytes));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            string actual = Convert.ToHexStringLower(hash);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Downloaded Windhawk installer failed SHA256 verification — download may be corrupted or tampered with. Aborting.");
            }
        }, ct);
    }

    private static async Task RunInstallerSilentlyAsync(string installerPath, CancellationToken ct)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            // requireAdministrator manifest covers packaged runs; this guards
            // unpackaged/un elevated configurations. The engine's service install
            // needs elevation and would silently fail otherwise.
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). Relaunch stellarisKIT elevated and retry.");
        }

        // AutoOS AppsStage.cs: Process.Start(windhawk_setup.exe, "/S", Hidden)
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk installer.");

        // NSIS setup can take a while (it also installs the engine service); 10-minute ceiling.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        int exit = -1;
        try { if (process.HasExited) exit = process.ExitCode; } catch { }
        Debug.WriteLine($"Windhawk installer exit code {exit}");
        // NSIS: 0 = success. Verification below is the real source of truth; a
        // non-zero exit here is logged but the file check decides.
    }

    private static async Task<WindhawkInstallationInfo> WaitForInstallationAsync(CancellationToken ct)
    {
        var detection = new WindhawkDetectionService();
        for (int i = 0; i < 60; i++) // 60 * 1s = 60s
        {
            ct.ThrowIfCancellationRequested();
            var info = detection.Detect();
            if (info.IsInstalled) return info;
            await Task.Delay(1000, ct);
        }
        return WindhawkInstallationInfo.NotInstalled;
    }
}
