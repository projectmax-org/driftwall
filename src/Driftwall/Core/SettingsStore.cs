using System.Text.Json;

namespace Driftwall.Core;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>.
/// <para>
/// Saves are debounced and written through a temporary file. Debouncing matters because settings
/// controls are bound live: dragging the interval slider would otherwise write the file on every
/// tick. The temporary file matters because a power cut mid-write must not leave an unparsable
/// settings file behind.
/// </para>
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly Lock _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;

    public SettingsStore()
    {
        Settings = Load();
        _saveTimer = new Timer(_ => FlushIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Raised after settings are changed through <see cref="Update"/>.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Mutates settings and schedules a save. The callback runs under the store's lock.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Settings);
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, Settings);
    }

    /// <summary>Marks settings dirty after they were mutated directly by a bound control.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, Settings);
    }

    public string? GetApiKey(string providerId) =>
        Settings.ApiKeys.TryGetValue(providerId, out var stored) && !string.IsNullOrEmpty(stored)
            ? Dpapi.Unprotect(stored)
            : null;

    public void SetApiKey(string providerId, string? key)
    {
        Update(settings =>
        {
            if (string.IsNullOrWhiteSpace(key)) settings.ApiKeys.Remove(providerId);
            else settings.ApiKeys[providerId] = Dpapi.Protect(key.Trim());
        });
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile))
            {
                Log.Info("No settings file; starting from defaults.");
                return AppSettings.CreateDefault();
            }

            var json = File.ReadAllText(Paths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, DriftwallJson.Options);
            if (settings is null) return AppSettings.CreateDefault();

            Migrate(settings);
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error("Settings file could not be read; it has been backed up and reset.", ex);
            TryBackupCorruptFile();
            return AppSettings.CreateDefault();
        }
    }

    /// <summary>Brings older settings files up to the current schema.</summary>
    private static void Migrate(AppSettings settings)
    {
        settings.IntervalMinutes = Math.Max(0, settings.IntervalMinutes);
        settings.PrefetchCount = Math.Clamp(settings.PrefetchCount, 0, 10);
        settings.OutputQuality = Math.Clamp(settings.OutputQuality, 60, 100);
        settings.CacheSizeMegabytes = Math.Clamp(settings.CacheSizeMegabytes, 64, 65536);
        settings.MinResolutionRatio = Math.Clamp(settings.MinResolutionRatio, 0.1, 2.0);
        settings.NoRepeatWindow = Math.Clamp(settings.NoRepeatWindow, 0, 5000);

        foreach (var source in settings.Sources)
        {
            source.Weight = Math.Clamp(source.Weight, 1, 10);
            source.Options ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        settings.SchemaVersion = 1;
    }

    private static void TryBackupCorruptFile()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return;
            var backup = Paths.SettingsFile + ".corrupt";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(Paths.SettingsFile, backup);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not back up the unreadable settings file.", ex);
        }
    }

    private void FlushIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
        }

        Save();
    }

    /// <summary>Writes settings immediately. Called on shutdown and after important changes.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, DriftwallJson.Options);
                var temp = Paths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(Paths.SettingsFile)) File.Replace(temp, Paths.SettingsFile, null);
                else File.Move(temp, Paths.SettingsFile);

                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save settings.", ex);
            }
        }
    }

    public void Dispose()
    {
        FlushIfDirty();
        _saveTimer.Dispose();
    }
}
