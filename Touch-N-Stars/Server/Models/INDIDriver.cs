using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NINA.Core.Utility;

namespace TouchNStars.Server.Models;

public class INDIDriver
{
    public string Name { get; set; }
    public string Label { get; set; }
    public string Type { get; set; }
}

public static class INDIDriverRegistry
{
    private static readonly string[] BuiltInDriverTypes =
    [
        "camera",
        "dome",
        "filterwheel",
        "flatpanel",
        "focuser",
        "rotator",
        "safetymonitor",
        "switches",
        "telescope",
        "weather"
    ];

    // Base directory: ~/Documents/INDI/ (or My Documents\INDI\ on Windows)
    private static readonly string DriverDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "INDI");
    private static readonly string ThirdPartyFilePath = Path.Combine(DriverDirectory, "3rdparty.json");
    private static readonly object PrepareLock = new();
    private static bool prepared;

    private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();
    // Embedded resource name format: TouchNStars.Server.Models.indi_drivers.<type>.json
    private static string ResourceName(string driverType) =>
        $"TouchNStars.Server.Models.indi_drivers.{driverType}.json";

    public static void PrepareDriverFiles(bool force = false)
    {
        lock (PrepareLock)
        {
            if (prepared && !force)
            {
                return;
            }

            EnsureDriverDirectory();
            Logger.Info($"Preparing INDI driver files in '{DriverDirectory}' (force={force})");

            foreach (string builtInType in BuiltInDriverTypes)
            {
                try
                {
                    SyncBuiltInDriverFile(builtInType);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to prepare INDI file for type '{builtInType}': {ex.Message}");
                }
            }

            EnsureThirdPartyFile();
            prepared = true;
        }
    }

    public static List<INDIDriver> GetDrivers(string driverType)
    {
        if (string.IsNullOrWhiteSpace(driverType))
        {
            Logger.Warning("INDI driver type was empty, returning empty list");
            return new List<INDIDriver>();
        }

        PrepareDriverFiles();

        var filePath = Path.Combine(DriverDirectory, $"{driverType}.json");
        var drivers = ReadDriverListFromFile(filePath);
        var thirdPartyDrivers = ReadThirdPartyDrivers(driverType);

        if (thirdPartyDrivers.Count == 0)
        {
            return drivers;
        }

        var merged = new Dictionary<string, INDIDriver>(StringComparer.OrdinalIgnoreCase);

        foreach (var driver in drivers)
        {
            if (driver == null)
            {
                continue;
            }

            string key = string.IsNullOrWhiteSpace(driver.Name)
                ? $"__label__:{driver.Label ?? string.Empty}"
                : driver.Name;
            merged[key] = NormalizeDriver(driver, driverType);
        }

        foreach (var driver in thirdPartyDrivers)
        {
            if (driver == null)
            {
                continue;
            }

            string key = string.IsNullOrWhiteSpace(driver.Name)
                ? $"__label__:{driver.Label ?? string.Empty}"
                : driver.Name;
            // 3rdparty entries intentionally override defaults when names collide.
            merged[key] = NormalizeDriver(driver, driverType);
        }

        return new List<INDIDriver>(merged.Values);
    }

    private static void EnsureDriverDirectory()
    {
        try
        {
            Directory.CreateDirectory(DriverDirectory);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create INDI driver directory '{DriverDirectory}': {ex.Message}");
        }
    }

    // Overwrite the local default file with the embedded one so plugin updates always win.
    private static void SyncBuiltInDriverFile(string driverType)
    {
        if (Array.IndexOf(BuiltInDriverTypes, driverType) < 0)
        {
            Logger.Warning($"Unknown INDI driver type '{driverType}', no embedded sync performed");
            return;
        }

        var dest = Path.Combine(DriverDirectory, $"{driverType}.json");
        var resourceName = ResourceName(driverType);
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Logger.Warning($"No embedded default found for INDI driver type '{driverType}' (resource '{resourceName}')");
            return;
        }

        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] embeddedBytes = ms.ToArray();

            bool shouldWrite = true;
            if (File.Exists(dest))
            {
                byte[] existingBytes = File.ReadAllBytes(dest);
                shouldWrite = !AreEqual(existingBytes, embeddedBytes);
            }

            if (!shouldWrite)
            {
                return;
            }

            using var fs = File.Create(dest);
            fs.Write(embeddedBytes, 0, embeddedBytes.Length);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to sync INDI driver file '{dest}': {ex.Message}");
        }
    }

    // Seed 3rdparty.json exactly once; do not overwrite user edits.
    private static void EnsureThirdPartyFile()
    {
        if (File.Exists(ThirdPartyFilePath))
        {
            return;
        }

        var resourceName = ResourceName("3rdparty");
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        try
        {
            using var fs = File.Create(ThirdPartyFilePath);

            if (stream == null)
            {
                Logger.Warning($"No embedded default found for INDI third-party file (resource '{resourceName}'). Writing inline default template.");
                WriteDefaultThirdPartyTemplate(fs);
                return;
            }

            stream.CopyTo(fs);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to seed INDI third-party file '{ThirdPartyFilePath}': {ex.Message}");
        }
    }

    private static void WriteDefaultThirdPartyTemplate(Stream targetStream)
    {
        var template = new Dictionary<string, List<INDIDriver>>(StringComparer.OrdinalIgnoreCase);
        foreach (string type in BuiltInDriverTypes)
        {
            template[type] = new List<INDIDriver>();
        }

        using var writer = new StreamWriter(targetStream);
        string json = JsonConvert.SerializeObject(template, Formatting.Indented);
        writer.Write(json);
    }

    private static List<INDIDriver> ReadDriverListFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Logger.Warning($"INDI driver file not found at '{filePath}', returning empty list");
            return new List<INDIDriver>();
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var drivers = JsonConvert.DeserializeObject<List<INDIDriver>>(json);
            if (drivers == null)
            {
                Logger.Warning($"INDI driver file '{filePath}' deserialised to null (expected a JSON array)");
                return new List<INDIDriver>();
            }
            return drivers;
        }
        catch (IOException ex)
        {
            Logger.Error($"Failed to read INDI driver file '{filePath}': {ex.Message}");
            return new List<INDIDriver>();
        }
        catch (JsonException ex)
        {
            Logger.Error($"Failed to parse INDI driver file '{filePath}': {ex.Message}");
            return new List<INDIDriver>();
        }
    }

    private static List<INDIDriver> ReadThirdPartyDrivers(string driverType)
    {
        if (!File.Exists(ThirdPartyFilePath))
        {
            return new List<INDIDriver>();
        }

        string json;
        try
        {
            json = File.ReadAllText(ThirdPartyFilePath);
        }
        catch (IOException ex)
        {
            Logger.Error($"Failed to read INDI third-party file '{ThirdPartyFilePath}': {ex.Message}");
            return new List<INDIDriver>();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<INDIDriver>();
        }

        try
        {
            // Preferred format: { "focuser": [{...}], "telescope": [{...}] }
            var map = JsonConvert.DeserializeObject<Dictionary<string, List<INDIDriver>>>(json);
            if (map != null)
            {
                if (!map.TryGetValue(driverType, out var typedDrivers) || typedDrivers == null)
                {
                    return new List<INDIDriver>();
                }

                return typedDrivers;
            }
        }
        catch (JsonException)
        {
            // Fall back to flat-array format below.
        }

        try
        {
            // Backward-compatible format: [ { "Name": "...", "Label": "...", "Type": "focuser" } ]
            var allDrivers = JsonConvert.DeserializeObject<List<INDIDriver>>(json);
            if (allDrivers == null)
            {
                return new List<INDIDriver>();
            }

            return allDrivers.FindAll(d =>
                d != null &&
                !string.IsNullOrWhiteSpace(d.Type) &&
                d.Type.Equals(driverType, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            Logger.Error($"Failed to parse INDI third-party file '{ThirdPartyFilePath}': {ex.Message}");
            return new List<INDIDriver>();
        }
    }

    private static INDIDriver NormalizeDriver(INDIDriver driver, string fallbackType)
    {
        return new INDIDriver
        {
            Name = driver.Name,
            Label = driver.Label,
            Type = string.IsNullOrWhiteSpace(driver.Type) ? fallbackType : driver.Type
        };
    }

    private static bool AreEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
