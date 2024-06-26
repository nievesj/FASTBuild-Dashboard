﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FastBuild.Dashboard.Configuration;

/// <remarks>
///     SettingsBase and any class derived from it must not be accessed before App is loaded,
///     or concretely, before App.Current.ProcessArgs() is called.
/// </remarks>
public abstract class SettingsBase
{
    protected SettingsBase()
    {
        Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
    }

    public static string StorageDirectory { get; private set; }

    [JsonIgnore] public abstract string Domain { get; }

    public string Version { get; set; }

    internal static void Initialize()
    {
        if (App.Current.IsShadowProcess)
        {
            StorageDirectory = App.Current.ShadowContext.StorageDirectory;
        }
        else
        {
            // System.Configuration.ClientConfigPaths is an internal class. Here we retrieve the standard local
            // user setting location with reflection

            var clientConfigPathsType =
                typeof(ConfigurationManager).Assembly.GetType("System.Configuration.ClientConfigPaths");
            var currentProperty =
                clientConfigPathsType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic);
            Debug.Assert(currentProperty != null, "currentProperty != null");
            var currentClientConfigPaths = currentProperty.GetValue(null);
            Debug.Assert(currentClientConfigPaths != null, "currentClientConfigPaths != null");
            var localConfigDirectoryProperty =
                clientConfigPathsType.GetProperty("LocalConfigDirectory",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            Debug.Assert(localConfigDirectoryProperty != null, "localConfigDirectoryField != null");
            StorageDirectory = (string)localConfigDirectoryProperty.GetValue(currentClientConfigPaths);
        }
    }

    internal static string GetSettingsFilePath(string domain)
    {
        return Path.Combine(StorageDirectory, GetSettingsFilename(domain));
    }

    private static string GetSettingsFilename(string domain)
    {
        return $"{domain}.config";
    }

    public static void Save(SettingsBase settings)
    {
        var path = GetSettingsFilePath(settings.Domain);
        var directory = Path.GetDirectoryName(path);
        Debug.Assert(directory != null, "directory != null");
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }

    public static T Load<T>(string domain)
        where T : SettingsBase, new()
    {
#if DEBUG
        if (App.IsInDesignTime) return new T();
#endif

        var path = GetSettingsFilePath(domain);

        if (File.Exists(path))
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                // continue
            }

        var settings = new T();
        var previousVersion = LoadPreviousVersion(domain);
        if (previousVersion != null) settings.Upgrade(previousVersion);

        settings.Save();

        return settings;
    }

    private static JObject LoadPreviousVersion(string domain)
    {
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var parentDirectory = Path.GetDirectoryName(StorageDirectory);
        if (!Directory.Exists(parentDirectory)) return null;

        var previousVersion = new Version(0, 0);
        foreach (var directory in Directory.EnumerateDirectories(parentDirectory))
        {
            var versionString = Path.GetFileName(directory);
            if (versionString == null
                || !System.Version.TryParse(versionString, out var version)
                || version >= currentVersion)
                continue;

            if (version > previousVersion) previousVersion = version;
        }

        var previousVersionFile = Path.Combine(
            parentDirectory,
            previousVersion.ToString(),
            GetSettingsFilename(domain));

        if (!File.Exists(previousVersionFile)) return null;

        try
        {
            return JObject.Parse(File.ReadAllText(previousVersionFile));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected virtual void Upgrade(JObject previousVersion)
    {
        var properties = GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var data = previousVersion.ToObject(GetType());

        foreach (var property in properties)
        {
            if (!property.CanWrite
                || !property.CanRead
                || property.Name == nameof(Version)
                || property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;

            property.SetValue(this, property.GetValue(data));
        }
    }
}