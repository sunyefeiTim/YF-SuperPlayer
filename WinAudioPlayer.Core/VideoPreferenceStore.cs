using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinAudioPlayer.Core;

public sealed class ManualVideoSelection
{
    public string Keywords { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
}

/// <summary>
/// 跨平台的视频选择设置。调用方只需传入各平台自己的应用数据目录。
/// JSON 文件名和格式保持与现有 Windows 版本兼容。
/// </summary>
public sealed class VideoPreferenceStore
{
    private static readonly Regex BilibiliIdPattern = new("^BV[0-9A-Za-z]{10}$", RegexOptions.Compiled);
    private readonly string dataDirectory;
    private readonly object syncRoot = new();
    private bool loaded;
    private readonly Dictionary<string, string> selectedVideos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ManualVideoSelection> manualSearches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> offsets = new(StringComparer.OrdinalIgnoreCase);

    public VideoPreferenceStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.dataDirectory = dataDirectory;
    }

    public string? GetSelectedVideo(string trackKey)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            return selectedVideos.GetValueOrDefault(trackKey);
        }
    }

    public ManualVideoSelection? GetManualSelection(string trackKey)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            if (!manualSearches.TryGetValue(trackKey, out ManualVideoSelection? value)) return null;
            return new ManualVideoSelection { Keywords = value.Keywords, VideoId = value.VideoId };
        }
    }

    public void RememberSelectedVideo(string trackKey, string videoId)
    {
        if (string.IsNullOrWhiteSpace(trackKey) || !IsValidVideoId(videoId)) return;
        lock (syncRoot)
        {
            EnsureLoaded();
            selectedVideos[trackKey] = videoId;
            if (manualSearches.TryGetValue(trackKey, out ManualVideoSelection? manual))
            {
                manual.VideoId = videoId;
                SaveManualSearches();
            }
            SaveSelectedVideos();
        }
    }

    public void RememberManualSelection(string trackKey, string? keywords, string? videoId)
    {
        string cleanKeywords = keywords?.Trim() ?? string.Empty;
        string cleanVideoId = videoId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trackKey) || cleanKeywords.Length == 0 || !IsValidVideoId(cleanVideoId)) return;
        lock (syncRoot)
        {
            EnsureLoaded();
            manualSearches[trackKey] = new ManualVideoSelection
            {
                Keywords = cleanKeywords,
                VideoId = cleanVideoId
            };
            selectedVideos[trackKey] = cleanVideoId;
            SaveManualSearches();
            SaveSelectedVideos();
        }
    }

    public double GetOffset(string trackKey, string videoId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            return offsets.TryGetValue(BuildOffsetKey(trackKey, videoId), out double value) && double.IsFinite(value)
                ? value
                : 0;
        }
    }

    public void SetOffset(string trackKey, string videoId, double seconds)
    {
        if (string.IsNullOrWhiteSpace(trackKey) || !IsValidVideoId(videoId) || !double.IsFinite(seconds)) return;
        lock (syncRoot)
        {
            EnsureLoaded();
            offsets[BuildOffsetKey(trackKey, videoId)] = Math.Round(seconds * 10) / 10;
            SaveOffsets();
        }
    }

    public static string BuildOffsetKey(string trackKey, string videoId) => $"{trackKey}\n{videoId}";

    public static bool IsValidVideoId(string? videoId) =>
        !string.IsNullOrWhiteSpace(videoId) && BilibiliIdPattern.IsMatch(videoId);

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        LoadDictionary("video-selections.json", selectedVideos,
            pair => !string.IsNullOrWhiteSpace(pair.Key) && IsValidVideoId(pair.Value));
        LoadDictionary("video-manual-searches.json", manualSearches,
            pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                !string.IsNullOrWhiteSpace(pair.Value?.Keywords) && IsValidVideoId(pair.Value.VideoId));
        LoadDictionary("video-offsets.json", offsets,
            pair => !string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value));
    }

    private void LoadDictionary<T>(string fileName, Dictionary<string, T> destination,
        Func<KeyValuePair<string, T>, bool> predicate)
    {
        try
        {
            string path = Path.Combine(dataDirectory, fileName);
            if (!File.Exists(path)) return;
            Dictionary<string, T>? values = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path));
            if (values == null) return;
            foreach (KeyValuePair<string, T> pair in values.Where(predicate)) destination[pair.Key] = pair.Value;
        }
        catch { }
    }

    private void SaveSelectedVideos() => Save("video-selections.json", selectedVideos);
    private void SaveManualSearches() => Save("video-manual-searches.json", manualSearches);
    private void SaveOffsets() => Save("video-offsets.json", offsets);

    private void Save<T>(string fileName, Dictionary<string, T> values)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            string target = Path.Combine(dataDirectory, fileName);
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            File.Move(temporary, target, true);
        }
        catch { }
    }
}
