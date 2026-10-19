using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string musicHome = "https://flac.music.hi.cn/";

var builder = WebApplication.CreateBuilder(args);
// 桌面普通用户运行时不写 Windows Event Log，避免无管理员权限导致宿主启动失败。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseStaticWebAssets();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("https://sunyefeitim.github.io")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Content-Disposition")));
builder.Services.AddHttpClient("music-source", client =>
{
    client.BaseAddress = new Uri(musicHome);
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 YF-Super-Player-Mobile/1.0");
    client.DefaultRequestHeaders.Referrer = new Uri(musicHome);
});

var app = builder.Build();
app.UseCors();

string accessPassword = app.Configuration["APP_ACCESS_PASSWORD"]?.Trim() ?? string.Empty;
string signingKey = app.Configuration["TOKEN_SIGNING_KEY"]?.Trim() ?? string.Empty;
bool accessLockEnabled = accessPassword.Length > 0 && signingKey.Length >= 32;

app.Use(async (context, next) =>
{
    if (!accessLockEnabled || context.Request.Path == "/health" || context.Request.Path == "/api/auth/login")
    {
        await next();
        return;
    }
    if (context.Request.Cookies.TryGetValue("yf_access", out string? token)
        && IsValidAccessToken(token, signingKey))
    {
        await next();
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "请先输入播放器访问密码" });
        return;
    }
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsync(GetLoginPage());
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/auth/login", (AccessLoginRequest request, HttpContext context) =>
{
    if (!accessLockEnabled) return Results.Ok(new { unlocked = true });
    byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(request.Password ?? string.Empty));
    byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(accessPassword));
    if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
        return Results.Json(new { message = "密码不正确" }, statusCode: StatusCodes.Status401Unauthorized);
    string token = CreateAccessToken(signingKey);
    context.Response.Cookies.Append("yf_access", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/"
    });
    return Results.Ok(new { unlocked = true });
});

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
    JsonElement resolvedData = root.GetProperty("data");
    string? url = resolvedData.GetProperty("url").GetString();
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { message = "没有获得下载地址" });
    string actualFormat = Text(resolvedData, "format", format).ToLowerInvariant();
    if (actualFormat == "aac") actualFormat = "m4a";

    using HttpResponseMessage media = await client.GetAsync(url);
    media.EnsureSuccessStatusCode();
    string mediaType = media.Content.Headers.ContentType?.MediaType ?? "";
    if (!mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        && !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "音乐平台没有开放这首歌曲的下载" });
    byte[] bytes = await media.Content.ReadAsByteArrayAsync();
    if (!HasExpectedAudioHeader(bytes, actualFormat))
        return Results.BadRequest(new { message = "下载内容不是有效的音频文件，请尝试其他音质或歌曲" });
    string stem = string.IsNullOrWhiteSpace(request.Artist) ? request.Name : $"{request.Name}-{request.Artist}";
    string fileName = SanitizeFileName(stem) + "." + actualFormat;
    string verifiedMediaType = actualFormat switch
    {
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        _ => "audio/mpeg"
    };
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
    if (format is "m4a" or "aac")
        return bytes.Length >= 12 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t'
            && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';
    if (bytes.Length < 2) return false;
    bool hasId3 = bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3';
    bool hasFrameSync = bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0;
    return hasId3 || hasFrameSync;
}

static string CreateAccessToken(string signingKey)
{
    long expires = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
    string payload = expires.ToString();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
    string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return $"{payload}.{signature}";
}

static bool IsValidAccessToken(string token, string signingKey)
{
    string[] parts = token.Split('.', 2);
    if (parts.Length != 2 || !long.TryParse(parts[0], out long expires)
        || expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
    string expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0])))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(expected));
}

static string GetLoginPage() => """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><meta name="theme-color" content="#101113"><title>解锁 YF-Super Player</title><style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#101315;color:#f4f6f5;font-family:system-ui,-apple-system,"Segoe UI",sans-serif;padding:24px}.card{width:min(390px,100%);padding:30px;border:1px solid #ffffff16;border-radius:24px;background:#191d20;box-shadow:0 24px 70px #0008}.mark{width:72px;height:72px;margin-bottom:20px;border-radius:18px;display:grid;place-items:center;background:#0756f9;color:white;font-size:38px;box-shadow:0 10px 35px #0756f955}h1{margin:0 0 8px;font-size:25px}p{margin:0 0 22px;color:#919b96;font-size:14px;line-height:1.6}input,button{width:100%;height:48px;border-radius:12px;font:inherit}input{border:1px solid #ffffff24;background:#111416;color:white;padding:0 14px;font-size:18px;letter-spacing:3px;outline:none}input:focus{border-color:#62d9a7}button{margin-top:12px;border:0;background:#68dca9;color:#102019;font-weight:800;cursor:pointer}.error{min-height:22px;margin-top:10px;color:#ff9b9b;font-size:13px}
</style></head><body><main class="card"><div class="mark">♫</div><h1>YF-Super Player</h1><p>请输入共享密码后进入播放器</p><form id="form"><input id="password" type="password" inputmode="numeric" autocomplete="current-password" autofocus aria-label="访问密码" placeholder="访问密码"><button id="submit" type="submit">进入播放器</button><div id="error" class="error"></div></form></main><script>
const form=document.getElementById('form'),password=document.getElementById('password'),button=document.getElementById('submit'),error=document.getElementById('error');form.addEventListener('submit',async event=>{event.preventDefault();error.textContent='';button.disabled=true;button.textContent='正在验证…';try{const response=await fetch('/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:password.value})});if(!response.ok){let message='密码不正确';try{message=(await response.json()).message||message}catch{}throw new Error(message)}location.replace('/')}catch(ex){error.textContent=ex.message||'暂时无法验证，请稍后重试';password.select()}finally{button.disabled=false;button.textContent='进入播放器'}});
</script></body></html>
""";

record AccessLoginRequest(string? Password);
record MusicSearchRequest(string? Platform, string? Keyword);
record MusicQuality(string Format, string Bitrate, string Size);
record MusicSearchResult(string Id, string Name, string Artist, string Album, int Duration, long Time,
    string Sign, List<MusicQuality> Qualities);
record MusicDownloadRequest(string Platform, string Id, string Name, string Artist, long Time, string? Sign,
    string Format, string? Bitrate);
