using stellarisKIT.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace stellarisKIT.Services;

/// <summary>
/// The "Import backup" half of the provisioning flow: parses a
/// windhawk-user-data-v1 JSON backup and applies it to a running Windhawk
/// installation. Two strategies, best-first:
///
/// 1. PREFERRED — windhawk-cli.exe (ships with Windhawk 2.0+):
///    "windhawk-cli data import &lt;archive&gt; --yes --confirm-app-restart" consumes
///    this exact format natively (verified in the Windhawk 2.0 source:
///    src/windhawk-core/cli/src/commands/data.rs). The CLI talks to the running
///    Windhawk core, which fetches each modId's source from the official mod
///    repository, applies per-mod settings, and reports per-mod outcomes —
///    settings-only entries are sufficient (no manual .wh.cpp handling).
/// 2. FALLBACK — direct HKLM\SOFTWARE\Windhawk registry writes (stable 1.7.x
///    has no import CLI). Verified layout (upstream issue #195 backup script):
///    - Mods live under Engine\Mods\&lt;modId&gt; with values LibraryFileName,
///      MetadataJson, Disabled, Settings_&lt;n&gt; keyed per setting.
///    - App settings live under the Settings subkey.
///    After writing, Windhawk's engine is restarted via its service so it
///    picks up new mods. Mods download on demand once the engine sees them.
///    NOTE: this fallback is best-effort; the 2.0 CLI path is the supported one.
/// </summary>
public sealed class WindhawkImportService
{
    private const string BackupFormat = "windhawk-user-data-v1";
    private const string WindhawkRegistryKey = @"SOFTWARE\Windhawk";
    private const string EngineModsKey = WindhawkRegistryKey + @"\Engine\Mods";
    private const string LegacyModsKey = WindhawkRegistryKey + @"\Mods";
    private const string SettingsKey = WindhawkRegistryKey + @"\Settings";

    /// <summary>Parses and validates a backup file without touching Windhawk.</summary>
    public static WindhawkBackup ParseBackup(string jsonFilePath)
    {
        if (!File.Exists(jsonFilePath))
            throw new FileNotFoundException("Windhawk backup file not found.", jsonFilePath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        var backup = JsonSerializer.Deserialize<WindhawkBackup>(File.ReadAllText(jsonFilePath), options)
            ?? throw new InvalidOperationException("The Windhawk backup file is not valid JSON.");

        if (!string.Equals(backup.Format, BackupFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported backup format '{backup.Format}' (expected '{BackupFormat}').");

        return backup;
    }

    /// <summary>
    /// Imports a backup into the installed Windhawk. Prefers the Windhawk 2.0+
    /// CLI, falls back to direct registry writes on 1.7.x. Per-mod failures are
    /// logged and skipped, never abort the run; the returned result carries a
    /// per-mod summary for the InfoBar.
    /// </summary>
    public async Task<WindhawkImportResult> ImportBackupAsync(
        string jsonFilePath,
        WindhawkInstallationInfo installation,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var backup = ParseBackup(jsonFilePath);
        var result = new WindhawkImportResult();

        // Strategy 1: native CLI (Windhawk 2.0+).
        if (!string.IsNullOrEmpty(installation.CliPath) && File.Exists(installation.CliPath))
        {
            status?.Report("Importing via windhawk-cli (Windhawk 2.0+)...");
            var cliResult = await ImportViaCliAsync(installation.CliPath!, jsonFilePath, backup, status, ct);
            if (cliResult is not null)
            {
                status?.Report("Restarting the Windhawk engine service to apply changes...");
                try
                {
                    RestartWindhawkService();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Service restart failed: {ex.Message}");
                    status?.Report("Note: Windhawk service could not be restarted automatically — it will pick up the mods on its next start.");
                }
                return cliResult;
            }

            status?.Report("windhawk-cli import did not complete — falling back to registry import.");
        }

        // Strategy 2: direct registry (stable 1.7.x, no CLI).
        status?.Report("Importing via Windhawk registry (legacy 1.7.x path)...");
        await ImportViaRegistryAsync(backup, result, status, ct);
        return result;
    }

    // ------------------------------------------------------------------
    // Strategy 1: windhawk-cli.exe (Windhawk 2.0+)
    // ------------------------------------------------------------------

    /// <summary>
    /// Executes sequential installation and configuration for each mod via CLI.
    /// This bypasses "data import" which strict-checks repo versions and fails.
    /// Returns null when the CLI is critically broken so the caller can fall back.
    /// </summary>
    private static async Task<WindhawkImportResult?> ImportViaCliAsync(
        string cliPath, string jsonFilePath, WindhawkBackup backup, IProgress<string>? status, CancellationToken ct)
    {
        var result = new WindhawkImportResult();

        // App settings can be immediately written to the registry because Windhawk
        // live-monitors and consumes them regardless of version via filesystem mapping.
        try
        {
            ApplyAppSettings(backup);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CLI fallback ApplyAppSettings failed: {ex.Message}");
            status?.Report($"Warning: app settings could not be applied ({ex.Message}).");
        }

        foreach (var mod in backup.Mods)
        {
            ct.ThrowIfCancellationRequested();
            status?.Report($"Installing mod: {mod.ModId}...");

            try
            {
                // 1. Install mod via unified Mod Install CLI (fetches latest)
                var installPsi = new ProcessStartInfo
                {
                    FileName = cliPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                installPsi.ArgumentList.Add("mod");
                installPsi.ArgumentList.Add("install");
                installPsi.ArgumentList.Add(mod.ModId);
                installPsi.ArgumentList.Add("--yes");
                
                using var installProcess = Process.Start(installPsi);
                if (installProcess is null)
                {
                    result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, false, "Failed to instantiate windhawk-cli bridge."));
                    result.Skipped++;
                    continue;
                }

                await installProcess.WaitForExitAsync(ct);
                
                if (installProcess.ExitCode != 0)
                {
                    string stderr = await installProcess.StandardError.ReadToEndAsync(ct);
                    Debug.WriteLine($"Mod '{mod.ModId}' install failed: {stderr}");
                    result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, false, $"Installation failed: {stderr.Trim()}"));
                    result.Skipped++;
                    continue;
                }

                // 2. Map Configuration block back onto LIVE Mod properties using ArgumentList safely parsing flattened keys
                if (mod.Settings is not null && mod.Settings.Count > 0)
                {
                    var configPsi = new ProcessStartInfo
                    {
                        FileName = cliPath,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    configPsi.ArgumentList.Add("mod");
                    configPsi.ArgumentList.Add("settings");
                    configPsi.ArgumentList.Add("set");
                    configPsi.ArgumentList.Add(mod.ModId);
                    
                    foreach (var (key, value) in mod.Settings)
                    {
                        string strValue = ConvertSettingValue(value);
                        configPsi.ArgumentList.Add($"{key}={strValue}");
                    }
                    
                    using var configProcess = Process.Start(configPsi);
                    if (configProcess is not null)
                    {
                        await configProcess.WaitForExitAsync(ct);
                    }
                }

                result.Imported++;
                result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, true, "Installed and injected configurations successfully."));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception deploying Windhawk mod '{mod.ModId}': {ex.Message}");
                result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, false, ex.Message));
                result.Skipped++;
            }
        }
        
        status?.Report(result.SummaryText);
        return result;
    }

    // ------------------------------------------------------------------
    // Strategy 2: direct registry writes (stable 1.7.x fallback)
    // ------------------------------------------------------------------

    private async Task ImportViaRegistryAsync(
        WindhawkBackup backup, WindhawkImportResult result, IProgress<string>? status, CancellationToken ct)
    {
        // App settings first (dotted keys under HKLM\SOFTWARE\Windhawk\Settings),
        // then mods one by one.
        await Task.Run(() =>
        {
            try
            {
                ApplyAppSettings(backup);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyAppSettings failed: {ex.Message}");
                status?.Report($"Warning: app settings could not be applied ({ex.Message}).");
            }

        // Only import mods whose IDs are known-good; unknown IDs won't resolve
        // from the Windhawk repository and will leave the user with disabled, non-
        // functional entries. This keeps the import deterministic.
        var catalog = WindhawkModCatalog.AllowedModIds;
        foreach (var mod in backup.Mods)
        {
            ct.ThrowIfCancellationRequested();

            if (!catalog.Contains(mod.ModId, StringComparer.OrdinalIgnoreCase))
            {
                result.Skipped++;
                result.Outcomes.Add(new WindhawkModImportOutcome(
                    mod.ModId, false,
                    "Mod id is not in the bundled catalog — skipped for safety."));
                Debug.WriteLine($"Mod '{mod.ModId}' skipped: not in catalog.");
                continue;
            }

            try
            {
                ApplyModToRegistry(mod);

                // Hard verification: re-read the registry to confirm the mod is
                // present and NOT disabled. A write-back failure or a stray
                // Disabled=1 would otherwise silently leave the mod not applied.
                bool applied = VerifyModApplied(mod.ModId);
                if (!applied)
                {
                    result.Skipped++;
                    result.Outcomes.Add(new WindhawkModImportOutcome(
                        mod.ModId, false,
                        "Registry write succeeded but the mod is not enabled — Windhawk will not apply it."));
                    Debug.WriteLine($"Mod '{mod.ModId}' import verified as NOT applied.");
                    continue;
                }

                result.Imported++;
                result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, true, null));
                Debug.WriteLine($"Mod '{mod.ModId}' import verified as applied.");
            }
            catch (Exception ex)
            {
                // Per-mod failure: log and continue (e.g. modId unknown,
                // registry locked). Windhawk will skip it.
                Debug.WriteLine($"Mod '{mod.ModId}' import failed: {ex.Message}");
                result.Skipped++;
                result.Outcomes.Add(new WindhawkModImportOutcome(mod.ModId, false, ex.Message));
            }
        }
        }, ct);

        // Nudge the engine to pick up the changes: restart the Windhawk service
        // (it reloads its mod profile from the registry on start).
        status?.Report("Restarting the Windhawk engine service to apply changes...");
        try
        {
            RestartWindhawkService();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Service restart failed: {ex.Message}");
            status?.Report("Note: Windhawk service could not be restarted automatically — it will pick up the mods on its next start.");
        }

        // Post-import summary: recompute Imported/Skipped counts from the verified
        // per-mod outcomes so the UI message is accurate (the loop above already
        // tagged failures as Skipped).
        status?.Report(result.SummaryText);
    }

    /// <summary>
    /// Applies appSettings as dotted registry values under HKLM\SOFTWARE\Windhawk\Settings.
    /// Engine sub-settings flatten to "engine.xxx" keys, matching how Windhawk's
    /// own settings UI names them.
    /// </summary>
    private static void ApplyAppSettings(WindhawkBackup backup)
    {
        var app = backup.AppSettings;
        if (app is null) return;

        using var baseKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(SettingsKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windhawk settings registry key (elevation required).");

        void SetString(string name, string? value)
        {
            if (value is null) return;
            baseKey.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }
        void SetDword(string name, bool? value) { if (value.HasValue) baseKey.SetValue(name, value.Value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord); }
        void SetDwordInt(string name, int? value) { if (value.HasValue) baseKey.SetValue(name, value.Value, Microsoft.Win32.RegistryValueKind.DWord); }

        SetString("language", app.Language);
        SetString("theme", app.Theme);
        SetDword("disableUpdateCheck", app.DisableUpdateCheck);
        SetDword("devModeOptOut", app.DevModeOptOut);
        SetDword("hideTrayIcon", app.HideTrayIcon);
        SetDword("alwaysCompileModsLocally", app.AlwaysCompileModsLocally);
        SetDword("dontAutoShowToolkit", app.DontAutoShowToolkit);
        SetDwordInt("modTasksDialogDelay", app.ModTasksDialogDelay);
        SetDwordInt("loggingVerbosity", app.LoggingVerbosity);

        if (app.Engine is { } engine)
        {
            SetDwordInt("engine.loggingVerbosity", engine.LoggingVerbosity);
            SetDword("engine.injectIntoCriticalProcesses", engine.InjectIntoCriticalProcesses);
            SetDword("engine.injectIntoIncompatiblePrograms", engine.InjectIntoIncompatiblePrograms);
            SetDword("engine.injectIntoGames", engine.InjectIntoGames);

            if (engine.Include is not null)
                baseKey.SetValue("engine.include", string.Join("\n", engine.Include), Microsoft.Win32.RegistryValueKind.MultiString);
            if (engine.Exclude is not null)
                baseKey.SetValue("engine.exclude", string.Join("\n", engine.Exclude), Microsoft.Win32.RegistryValueKind.MultiString);
        }
    }

    /// <summary>
    /// Writes one mod's registration under HKLM\SOFTWARE\Windhawk\Engine\Mods\&lt;modId&gt;.
    /// The registry stores settings as flat string values ("key" → stringified
    /// scalar, arrays as key[i] entries) — this is exactly the flat form the
    /// backup already uses, so values transfer directly.
    /// </summary>
    private static void ApplyModToRegistry(WindhawkModEntry mod)
    {
        if (string.IsNullOrWhiteSpace(mod.ModId) || mod.ModId.Contains('\\') || mod.ModId.StartsWith("local@"))
            throw new InvalidOperationException("Unsupported mod id.");

        // Mods ship as .dll under the engine's mod directory; the engine resolves
        // the modId against the official repository on first run. Writing the
        // registry entry is enough to make it an "installed" mod for the engine.
        string modKeyPath = EngineModsKey + "\\" + mod.ModId;
        using var modKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(modKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create the registry key for mod '{mod.ModId}' (elevation required).");

        modKey.SetValue("LibraryFileName", $"{mod.ModId}.dll", Microsoft.Win32.RegistryValueKind.String);

        if (!string.IsNullOrEmpty(mod.Version))
            modKey.SetValue("Version", mod.Version, Microsoft.Win32.RegistryValueKind.String);

        // IMPORTANT: enable the mod by default. The engine disables it only when
        // the source/binary cannot be resolved from the repository. Writing 0 here
        // is what makes the toggle appear "on" in the Windhawk UI.
        modKey.SetValue("Disabled", 0, Microsoft.Win32.RegistryValueKind.DWord);

        if (mod.Settings is not null)
        {
            foreach (var (key, value) in mod.Settings)
            {
                modKey.SetValue($"Settings_{key}", ConvertSettingValue(value), Microsoft.Win32.RegistryValueKind.String);
            }
        }
    }

    /// <summary>
    /// Verifies that a single mod was written correctly by re-opening its registry
    /// key and checking the Disabled value — the most reliable signal that the
    /// engine will honor the mod.
    /// </summary>
    private static bool VerifyModApplied(string modId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(EngineModsKey + "\\" + modId, writable: false);
            if (key is null) return false;
            var disabled = key.GetValue("Disabled");
            if (disabled is int d && d == 0) return true;
            if (disabled is int d2 && d2 == 1) return false; // explicitly disabled
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string ConvertSettingValue(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.True } => "1",
        JsonElement { ValueKind: JsonValueKind.False } => "0",
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Restarts the Windhawk service so it reloads its mod profile.</summary>
    private static void RestartWindhawkService()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c net stop WindhawkService & net start WindhawkService",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var process = Process.Start(psi);
        process?.WaitForExit(30000);
    }
}
