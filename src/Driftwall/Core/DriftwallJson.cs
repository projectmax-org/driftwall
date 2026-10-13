using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Driftwall.Core;

/// <summary>Shared JSON settings. One instance so the metadata cache is reused everywhere.</summary>
public static class DriftwallJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Compact variant for network payload parsing (no indentation cost).</summary>
    public static readonly JsonSerializerOptions Network = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Writes a file and makes sure it is on disk before returning. The stores write to a temporary
    /// file and then swap it in; without the flush NTFS can record the swap before the data, and a
    /// power cut then leaves a file of the right name that is empty or full of zeros.
    /// </summary>
    public static void WriteDurably(string path, string json)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(json));   // UTF-8 without a byte order mark, as File.WriteAllText wrote
        stream.Flush(flushToDisk: true);
    }
}
