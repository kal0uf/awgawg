using stellarisKIT.Models;
using stellarisKIT.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services;

/// <summary>
/// Windhawk provisioning flow — direct-download variant copied from AutoOS AppsStage.cs:
/// 1. Download windhawk_setup.exe from pinned URL https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe
///    to %TEMP%\windhawk_setup.exe (AutoOS: DownloadHelper.Download(directUrl, Path.GetTempPath(), "windhawk_setup.exe"))
/// 2. Run NSIS installer with /S (silent), hidden window, wait for exit (AutoOS: Process.Start(... "/S", Hidden))
/// 3. Import KaliteOS.json from Assets/Windhawk/KaliteOS.json (also mods-bundled.json) via:
///    windhawk-cli.exe data import "json" --confirm-app-restart --yes   WorkingDirectory = %ProgramFiles%\Windhawk
/// 4. Update mods:  mod list --update-available --json  →  mod update &lt;id&gt;  (AutoOS ProcessActions.UpdateWindhawkMods)
///
/// No GitHub API is used — the installer URL is pinned per user request. No AutoOS remote is used.
/// </summary>
public sealed class WindhawkInstallerService
{
    private static readonly HttpClient _http = new();

    // Direct pinned URL — replaces GitHub API + offline asset lookup (AutoOS pattern).
    public const string DirectDownloadUrl = "https://github.com/ramensoftware/windhawk/releases/download/2.0.0-alpha.5/windhawk_setup.exe";
    public const string DirectVersion = "2.0.0-alpha.5";

    private readonly WindhawkDetectionService _detection = new();

    // Temp files created during a run. Cleaned up in Dispose when the service is
    // short-lived, and also aggressively in CleanupTempFiles.
    private readonly List<string> _tempFilePaths = new();

    /// <summary>
    /// Currently tracked Windhawk install info after the last successful install.
    /// </summary>
    public WindhawkInstallationInfo? LastInstalledInfo { get; private set; }

    // ------------------------------------------------------------------
    // Step 1: resolve latest release + offline installer asset
    // ------------------------------------------------------------------

    /// <summary>
    /// Direct-download replacement for the GitHub API lookup.
    /// Returns the pinned URL + version (mirrors AutoOS AppsStage.cs hard-coded download).
    /// </summary>
    public Task<(string Version, string DownloadUrl)> ResolveLatestOfflineInstallerAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult((DirectVersion, DirectDownloadUrl));
    }

    // ------------------------------------------------------------------
    // Step 2: download to temp path
    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads the installer to a temp file (AutoOS: DownloadHelper.Download(directUrl, Path.GetTempPath(), "windhawk_setup.exe")).
    /// The returned path is tracked for cleanup.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string downloadUrl, IProgress<string>? status = null, CancellationToken ct = default)
    {
        // Use windhawk_setup.exe (pinned direct URL) — matches requested link.
        string tempPath = Path.Combine(Path.GetTempPath(), "windhawk_setup.exe");

        // Avoid reusing a stale/locked temp file from a previous failed run (AutoOS kills locking processes).
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        status?.Report($"Downloading Windhawk {DirectVersion} from GitHub (direct)...");

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.UserAgent.ParseAdd("stellarisKIT-WindhawkInstaller/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[1 << 16];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
            await file.WriteAsync(buffer.AsMemory(0, read), ct);

        _tempFilePaths.Add(tempPath);
        return tempPath;
    }

    // ------------------------------------------------------------------
    // Step 3: run installer silently
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the downloaded NSIS installer with /S (silent) and hidden window style,
    /// and awaits exit. Requires elevation for the engine service install.
    /// Mirrors AutoOS AppsStage.cs: Process.Start(new ProcessStartInfo { FileName = windhawk_setup.exe, Arguments = "/S", Hidden }).WaitForExitAsync()
    /// </summary>
    public async Task InstallSilentlyAsync(
        string installerPath, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!WindhawkDetectionService.IsRunningElevated())
        {
            throw new UnauthorizedAccessException(
                "Administrator rights are required to install Windhawk (it installs a system service). " +
                "Relaunch stellarisKIT elevated and retry.");
        }

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Windhawk installer not found at the expected temp path.", installerPath);

        status?.Report("Installing Windhawk silently...");

        // Direct invocation (AutoOS) — not cmd /c start /wait. Hidden, no window.
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/S",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Windhawk offline installer.");

        // NSIS installers can take a while (service install, file copy). 10-minute ceiling.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("The Windhawk installer timed out after 10 minutes and was terminated.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The Windhawk installer exited with code {process.ExitCode}. " +
                "Installation may have failed; check the official site: https://windhawk.net/");
        }

        // Give the install a moment to settle before we probe for cli.exe.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    // ------------------------------------------------------------------
    // Step 4: locate cli.exe and run data import
    // ------------------------------------------------------------------

    /// <summary>
    /// Locates windhawk-cli.exe under %ProgramFiles%\Windhawk and runs the data
    /// import command. WorkingDirectory is set to the Windhawk install folder.
    /// </summary>
    public async Task<WindhawkInstallerResult> ImportSettingsAsync(
        string jsonPath,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var info = _detection.Detect();
        if (!info.IsInstalled || string.IsNullOrEmpty(info.InstallDirectory))
            throw new InvalidOperationException(
                "Windhawk does not appear to be installed. Install it first before importing settings.");

        string cliPath = info.CliPath
            ?? Path.Combine(info.InstallDirectory, "windhawk-cli.exe");

        if (!File.Exists(cliPath))
        {
            // Fall back to the explicit Program Files path requested in the spec.
            string pfCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk", "windhawk-cli.exe");
            if (File.Exists(pfCli))
                cliPath = pfCli;
        }

        if (!File.Exists(cliPath))
            throw new InvalidOperationException(
                "windhawk-cli.exe not found in the Windhawk install directory. " +
                "The CLI is required to import settings; ensure Windhawk 2.0+ is installed.");

        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Settings JSON file not found.", jsonPath);

        status?.Report("Importing Windhawk settings via windhawk-cli...");

        var importResult = await RunCliCommandAsync(
            cliPath,
            $"data import \"{jsonPath}\" --confirm-app-restart --yes",
            info.InstallDirectory,
            status,
            ct);

        // Parse the machine-readable summary if the CLI emitted one.
        var parsed = ParseImportSummary(importResult.Stdout);
        if (parsed is not null)
        {
            status?.Report(parsed);
        }
        else if (!string.IsNullOrWhiteSpace(importResult.Stderr))
        {
            status?.Report($"Import completed with stderr: {importResult.Stderr.Trim()}");
        }
        else
        {
            status?.Report("Settings imported via windhawk-cli.");
        }

        LastInstalledInfo = _detection.Detect();

        // Optional step 5: update any mods that have updates available.
        var updateResult = await UpdateModsIfAvailableAsync(cliPath, info.InstallDirectory, status, ct);
        if (updateResult.HasUpdates)
        {
            status?.Report($"Updated {updateResult.UpdatedIds.Count} mod(s) to the latest version.");
        }
        else if (updateResult.CheckedCount > 0)
        {
            status?.Report("All installed mods are up to date.");
        }

        return new WindhawkInstallerResult(
            Success: importResult.ExitCode == 0 || importResult.ExitCode == 7,
            ExitCode: importResult.ExitCode,
            Stdout: importResult.Stdout,
            Stderr: importResult.Stderr,
            ModsUpdated: updateResult.UpdatedIds.ToList());
    }

    // ------------------------------------------------------------------
    // Step 5: mod update (optional)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs "mod list --update-available --json", and for each returned modId runs
    /// "mod update <modId>". Returns the list of updated ids.
    /// </summary>
    public async Task<ModUpdateResult> UpdateModsIfAvailableAsync(
        string cliPath,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var result = new ModUpdateResult();

        status?.Report("Checking for mod updates...");

        var listResult = await RunCliCommandAsync(
            cliPath,
            "mod list --update-available --json",
            workingDirectory,
            status,
            ct);

        if (listResult.ExitCode != 0)
        {
            status?.Report($"Mod update check failed (exit {listResult.ExitCode}): {listResult.Stderr.Trim()}");
            return result;
        }

        var updateable = ParseUpdateAvailableList(listResult.Stdout);
        result.CheckedCount = updateable.Count;

        if (updateable.Count == 0)
            return result;

        foreach (var modId in updateable)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"Updating mod {modId}...");

            var updateResult = await RunCliCommandAsync(
                cliPath,
                $"mod update {modId}",
                workingDirectory,
                status,
                ct);

            if (updateResult.ExitCode == 0)
                result.UpdatedIds.Add(modId);
            else
                status?.Report($"Update failed for {modId} (exit {updateResult.ExitCode}): {updateResult.Stderr.Trim()}");
        }

        result.HasUpdates = result.UpdatedIds.Count > 0;
        return result;
    }

    // ------------------------------------------------------------------
    // Process invocation helper (same pattern as the rest of the service)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a windhawk-cli subcommand and captures stdout/stderr. WorkingDirectory
    /// is set to the Windhawk install folder so the CLI can find its engine/data
    /// paths.
    /// </summary>
    private static async Task<CliRunResult> RunCliCommandAsync(
        string cliPath,
        string arguments,
        string workingDirectory,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start windhawk-cli: {cliPath}");

        // Read both streams concurrently so a bloated stdout doesn't deadlock the stderr pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            throw new InvalidOperationException("windhawk-cli command timed out after 5 minutes.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        Debug.WriteLine($"windhawk-cli [{arguments}] exit {process.ExitCode}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine($"windhawk-cli stderr: {stderr}");

        return new CliRunResult(process.ExitCode, stdout, stderr);
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses the --json output of "data import". Expected shape:
    /// { "summary": { "mods": [ { "modId", "status", "message" } ... ] } }
    /// Returns a human-readable summary string when parsing succeeds.
    /// </summary>
    private static string? ParseImportSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement? modsElement = null;
            // Support both { "summary": { "mods": [...] } } and { "data": { "summary": { "mods": [...] } } }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("summary", out var s) && s.TryGetProperty("mods", out var m))
                {
                    modsElement = m;
                }
                else if (root.TryGetProperty("data", out var data) && data.TryGetProperty("summary", out var s2) && s2.TryGetProperty("mods", out var m2))
                {
                    modsElement = m2;
                }
                else
                {
                    // Fallback: try to find a nested "mods" property anywhere at top level
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("mods", out var m3))
                        {
                            modsElement = m3;
                            break;
                        }
                    }
                }
            }

            if (modsElement is null || modsElement.Value.ValueKind != JsonValueKind.Array)
                return null;

            int imported = 0, failed = 0;
            var failures = new List<string>();

            foreach (var mod in modsElement.Value.EnumerateArray())
            {
                string modId = mod.TryGetProperty("modId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
                string status = mod.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
                bool ok = status.Equals("installed", StringComparison.OrdinalIgnoreCase);
                if (ok) imported++;
                else
                {
                    failed++;
                    string msg = mod.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? status
                        : status;
                    if (!string.IsNullOrEmpty(modId))
                        failures.Add($"{modId}: {msg}");
                }
            }

            var parts = new List<string> { $"CLI import: {imported} mod(s) installed" };
            if (failed > 0)
                parts.Add($"{failed} failed: " + string.Join("; ", failures));

            return string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParseImportSummary failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the stdout of "mod list --update-available --json". Expected shape is
    /// a JSON array of mod ids, or an object with an ids/advisories field. This is
    /// lenient: it collects any string entries that look like mod ids.
    /// </summary>
    private static List<string> ParseUpdateAvailableList(string json)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return ids;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // If it's a bare array of strings, use it directly.
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String)
                        ids.Add(el.GetString()!);
                return ids;
            }

            // Otherwise look for common shapes: { "ids": [...] }, { "updateAvailable": [...] },
            // or flatten every string property value that looks like a mod id.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in prop.Value.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                            ids.Add(el.GetString()!);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ParseUpdateAvailableList failed: {ex.Message}");
        }

        return ids;
    }

    // ------------------------------------------------------------------
    // Cleanup
    // ------------------------------------------------------------------

    /// <summary>
    /// Removes temp files created during install/import. If a file is still locked,
    /// attempts to kill the locking process first, then retries deletion.
    /// </summary>
    public void CleanupTempFiles()
    {
        foreach (var path in _tempFilePaths.ToList())
        {
            TryDeleteWithForce(path);
        }

        _tempFilePaths.Clear();
    }

    /// <summary>
    /// Explicitly clean up a specific temp file (installer .exe or imported .json).
    /// </summary>
    public static void CleanupFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        TryDeleteWithForce(path);
    }

    private static void TryDeleteWithForce(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                break;
            }
            catch (IOException)
            {
                // File locked — try to find and kill the locking process, then retry.
                var locker = FindLockedProcess(path);
                if (locker is not null && locker.Id != Environment.ProcessId)
                {
                    try
                    {
                        locker.Kill(entireProcessTree: true);
                        Task.Delay(500).Wait();
                    }
                    catch { }
                }

                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    break;
                }
                catch
                {
                    // Last attempt failed; leave the file for the next cleanup sweep.
                }
            }
        }
    }

    private static Process? FindLockedProcess(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fileName)))
            {
                try
                {
                    if (proc.MainModule?.FileName.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
                        return proc;
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    // ------------------------------------------------------------------
    // Records
    // ------------------------------------------------------------------

    public record CliRunResult(int ExitCode, string Stdout, string Stderr);

    public record WindhawkInstallerResult(
        bool Success,
        int ExitCode,
        string Stdout,
        string Stderr,
        List<string> ModsUpdated);

    public sealed class ModUpdateResult
    {
        public bool HasUpdates { get; set; }
        public int CheckedCount { get; set; }
        public List<string> UpdatedIds { get; } = new();
    }

    public void Dispose()
    {
        CleanupTempFiles();
    }
}
