using System.Text.Json;

namespace Driftwall.Core;

/// <summary>
/// Small amount of state that must survive a restart: what is currently on screen, and what has been
/// shown recently so the no-repeat window is not reset every launch.
/// </summary>
public sealed class AppState
{
    /// <summary>Photo keys shown recently, oldest first.</summary>
    public List<string> RecentKeys { get; set; } = new();

    /// <summary>What is on the desktop right now, so Refresh works after a restart.</summary>
    public List<PhotoItem> CurrentPhotos { get; set; } = new();

    public DateTimeOffset? LastChangeUtc { get; set; }

    public static AppState Load()
    {
        try
        {
            if (!File.Exists(Paths.StateFile)) return new AppState();
            var json = File.ReadAllText(Paths.StateFile);
            return JsonSerializer.Deserialize<AppState>(json, DriftwallJson.Options) ?? new AppState();
        }
        catch (Exception ex)
        {
            Log.Warn("State file could not be read; starting fresh.", ex);
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, DriftwallJson.Options);
            var temp = Paths.StateFile + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(Paths.StateFile)) File.Replace(temp, Paths.StateFile, null);
            else File.Move(temp, Paths.StateFile);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save state.", ex);
        }
    }
}
