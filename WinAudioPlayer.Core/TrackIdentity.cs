namespace WinAudioPlayer.Core;

/// <summary>
/// 与界面和文件路径无关的歌曲身份。重新下载同一首歌后仍可匹配原有设置。
/// </summary>
public readonly record struct TrackIdentity(string Title, string Artist)
{
    public static TrackIdentity Create(string? title, string? artist)
    {
        string cleanTitle = (title ?? string.Empty).Trim();
        string cleanArtist = string.Equals(artist?.Trim(), "-", StringComparison.Ordinal)
            ? string.Empty
            : (artist ?? string.Empty).Trim();
        return new TrackIdentity(cleanTitle, cleanArtist);
    }

    public string Key => $"{Title}\n{Artist}";

    public string DefaultVideoSearchKeywords => $"{Title} {Artist} MV".Trim();
}
