using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace stellarisKIT.Services;

/// <summary>
/// The authoritative list of mod IDs that the bundled default backup contains.
/// Used by WindhawkImportService to skip any mod not in this catalog, so the
/// import stays deterministic and a stray/unsigned modId can never be written
/// into the engine's registry.
///
/// Keep this list in sync with the bundled asset
/// Assets/Windhawk/mods-bundled.json whenever the default set changes.
/// </summary>
public static class WindhawkModCatalog
{
    /// <summary>
    /// Reads the bundled default backup JSON from the app's content location and
    /// returns the ordered list of mod IDs it declares.
    ///
    /// For packaged (MSIX) runs this resolves via the package content; for
    /// unpackaged runs it resolves relative to the executable directory.
    /// </summary>
    public static IReadOnlyList<string> AllowedModIds
    {
        get
        {
            var path = BundledBackupPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Array.Empty<string>();

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var mods = doc.RootElement.GetProperty("mods");
                var ids = new List<string>(mods.GetArrayLength());
                foreach (var mod in mods.EnumerateArray())
                    if (mod.TryGetProperty("modId", out var id) &&
                        id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
                return ids;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Absolute path to the bundled KaliteOS Windhawk backup JSON.
    /// Primary: Assets/Windhawk/KaliteOS.json (C:\Users\AutoOS\Downloads\KaliteOS.json copy).
    /// Fallback: Assets/Windhawk/mods-bundled.json (kept in sync with KaliteOS).
    /// Fallback 2: Assets/KaliteOS.json
    /// </summary>
    public static string? BundledBackupPath
    {
        get
        {
            var assemblyDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(assemblyDir)) return null;

            foreach (var candidate in new[]
            {
                Path.Combine(assemblyDir, "Assets", "Windhawk", "KaliteOS.json"),
                Path.Combine(assemblyDir, "Assets", "Windhawk", "mods-bundled.json"),
                Path.Combine(assemblyDir, "Assets", "KaliteOS.json"),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>
    /// Ensures the bundled backup asset is copied to a writable location next to
    /// the executable on first use, so the import path always has a real file to
    /// hand to Windhawk (windhawk-cli and the registry path both want a file on
    /// disk, not a package URI).
    ///
    /// Idempotent and safe to call on every run.
    /// </summary>
    public static string EnsureBundledBackupOnDisk()
    {
        var source = BundledBackupPath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
            throw new FileNotFoundException(
                "The bundled Windhawk mod list (Assets/Windhawk/mods-bundled.json) is missing from the installation.");

        var assemblyDir = AppContext.BaseDirectory;

        var targetDir = Path.Combine(assemblyDir, "Assets", "Windhawk");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "mods-bundled.json");

        // Only copy when the target is missing or older than the source (e.g. after
        // an app update that bumped the bundled mod list).
        if (!File.Exists(target) ||
            File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
        {
            File.Copy(source, target, overwrite: true);
        }

        return target;
    }
}
