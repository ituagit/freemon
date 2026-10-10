using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeMon;

/// <summary>Per-user interface state (not part of the shared, password-protected config).</summary>
public sealed class UiState
{
    public bool EventsExpanded { get; set; } = true;
    public int MapMode { get; set; }
    public bool MapFrames { get; set; } = true;
    /// <summary>Everything above the map / tree is collapsed except the state counters (wall display).</summary>
    public bool TopCollapsed { get; set; }
    /// <summary>Interface language (only "uk" during development).</summary>
    public string Language { get; set; } = "";
    /// <summary>Programs for device connections — per user (browsers and SSH clients differ between computers).</summary>
    public SshMode? SshMode { get; set; }
    public string? SshCommand { get; set; }
    /// <summary>0 = default browser, 1 = custom command.</summary>
    public int WebMode { get; set; }
    public string WebCommand { get; set; } = "";
    public string? ReportOrganization { get; set; }
    public string? ReportPreparedBy { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class UiStateStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static UiState? _current;

    public static string FilePath => Paths.Portable
        ? Path.Combine(Paths.DataDir, "ui.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeMon", "ui.json");

    public static UiState Current => _current ??= Load();

    static UiState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(FilePath), Opts) ?? new UiState();
        }
        catch (Exception ex)
        {
            CrashLog.Write("UI state load", ex);
        }
        return new UiState();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts), new UTF8Encoding(false));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("UI state save", ex);
        }
    }
}
