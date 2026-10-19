using System.Text.Json;

const string musicHome = "https://flac.music.hi.cn/";

var builder = WebApplication.CreateBuilder(args);
// 桌面普通用户运行时不写 Windows Event Log，避免无管理员权限导致宿主启动失败。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseStaticWebAssets();
builder.Services.AddHttpClient("music-source", client =>
{
    client.BaseAddress = new Uri(musicHome);
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 YF-Super-Player-Mobile/1.0");
    client.DefaultRequestHeaders.Referrer = new Uri(musicHome);
});

var app = builder.Build();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapPost("/api/music/search", async (MusicSearchRequest request, IHttpClientFactory clients) =>
{
    string keyword = request.Keyword?.Trim() ?? "";
    if (keyword.Length == 0) return Results.BadRequest(new { message = "请输入搜索关键词" });
    string platform = request.Platform == "wyy" ? "wyy" : "kuwo";
    HttpClient client = clients.CreateClient("music-source");
    using HttpResponseMessage response = await PostFormAsync(client, "ajax.php?act=search", new()
    {
        ["platform"] = platform,
        ["keyword"] = keyword,
        ["page"] = "1",
        ["size"] = "30"
    });
    using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    JsonElement root = json.RootElement;
    if (!root.TryGetProperty("code", out JsonElement code) || code.GetInt32() != 0)
        return Results.BadRequest(new { message = Text(root, "msg", "音乐服务返回异常") });

    var songs = new List<MusicSearchResult>();
    if (root.GetProperty("data").TryGetProperty("list", out JsonElement list))
    {
        foreach (JsonElement item in list.EnumerateArray())
        {
            var qualities = new List<MusicQuality>();
            if (item.TryGetProperty("minfo", out JsonElement qualityList))
            {
                foreach (JsonElement quality in qualityList.EnumerateArray())
                    qualities.Add(new(Text(quality, "format"), Text(quality, "bitrate"), Text(quality, "size")));
            }
            songs.Add(new(
                Text(item, "id"), Text(item, "name"), Text(item, "artist"), Text(item, "album_name"),
                Number(item, "duration"), LongNumber(item, "time"), Text(item, "sign"), qualities));
        }
    }
    return Results.Ok(songs);
});

app.MapPost("/api/music/download", async (MusicDownloadRequest request, IHttpClientFactory clients) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { message = "歌曲信息不完整" });
    string platform = request.Platform == "wyy" ? "wyy" : "kuwo";
    string format = request.Format.Equals("flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "mp3";
    HttpClient client = clients.CreateClient("music-source");
    using HttpResponseMessage resolved = await PostFormAsync(client, "ajax.php?act=getUrl", new()
    {
        ["platform"] = platform,
        ["songid"] = request.Id,
        ["format"] = format,
        ["bitrate"] = request.Bitrate ?? "",
        ["time"] = request.Time.ToString(),
        ["sign"] = request.Sign ?? ""
    });
    using JsonDocument json = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
    JsonElement root = json.RootElement;
    if (!root.TryGetProperty("code", out JsonElement code) || code.GetInt32() != 0)
        return Results.BadRequest(new { message = Text(root, "msg", "没有获得下载地址") });
    string? url = root.GetProperty("data").GetProperty("url").GetString();
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { message = "没有获得下载地址" });

    using HttpResponseMessage media = await client.GetAsync(url);
    media.EnsureSuccessStatusCode();
    string mediaType = media.Content.Headers.ContentType?.MediaType ?? "";
    if (!mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "音乐平台没有开放这首歌曲的下载" });
    byte[] bytes = await media.Content.ReadAsByteArrayAsync();
    if (!HasExpectedAudioHeader(bytes, format))
        return Results.BadRequest(new { message = "下载内容不是有效的音频文件，请尝试其他音质或歌曲" });
    string stem = string.IsNullOrWhiteSpace(request.Artist) ? request.Name : $"{request.Name}-{request.Artist}";
    string fileName = SanitizeFileName(stem) + "." + format;
    string verifiedMediaType = format == "flac" ? "audio/flac" : "audio/mpeg";
    return Results.File(bytes, verifiedMediaType, fileName);
});

app.MapFallbackToFile("index.html");
app.Run();

static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields)
{
    HttpRequestException? lastError = null;
    for (int attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            HttpResponseMessage response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            lastError = ex;
            if (attempt < 2) await Task.Delay(500 * (attempt + 1));
        }
    }
    throw new HttpRequestException("音乐服务连接失败，请稍后重试", lastError);
}

static string Text(JsonElement element, string name, string fallback = "") =>
    element.TryGetProperty(name, out JsonElement value) ? value.ToString() : fallback;
static int Number(JsonElement element, string name) => int.TryParse(Text(element, name), out int value) ? value : 0;
static long LongNumber(JsonElement element, string name) => long.TryParse(Text(element, name), out long value) ? value : 0;
static string SanitizeFileName(string value)
{
    foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
    return string.IsNullOrWhiteSpace(value) ? "song" : value.Trim();
}

static bool HasExpectedAudioHeader(byte[] bytes, string format)
{
    if (format == "flac")
        return bytes.Length >= 4 && bytes[0] == (byte)'f' && bytes[1] == (byte)'L'
            && bytes[2] == (byte)'a' && bytes[3] == (byte)'C';
    if (bytes.Length < 2) return false;
    bool hasId3 = bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3';
    bool hasFrameSync = bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
    return hasId3 || hasFrameSync;
}

record MusicSearchRequest(string? Platform, string? Keyword);
record MusicQuality(string Format, string Bitrate, string Size);
record MusicSearchResult(string Id, string Name, string Artist, string Album, int Duration, long Time,
    string Sign, List<MusicQuality> Qualities);
record MusicDownloadRequest(string Platform, string Id, string Name, string Artist, long Time, string? Sign,
    string Format, string? Bitrate);
