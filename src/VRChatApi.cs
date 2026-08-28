using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCAvatarChanger;

public sealed class VRChatApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>ログインに 2FA が必要なときに投げる。Methods は "totp" / "otp" / "emailOtp" のいずれか。</summary>
public sealed class TwoFactorRequiredException(IReadOnlyList<string> methods) : Exception("2FA required")
{
    public IReadOnlyList<string> Methods { get; } = methods;
}

/// <summary>例外を利用者向けの日本語メッセージにする。</summary>
public static class FriendlyError
{
    public static string Of(Exception ex) => ex switch
    {
        VRChatApiException api => api.Message,
        System.Net.Http.HttpRequestException => "VRChat に接続できませんでした。インターネット接続を確認してください。",
        TaskCanceledException or TimeoutException => "VRChat からの応答がありません。しばらくしてからもう一度お試しください。",
        _ => ex.Message,
    };
}

public sealed class VRChatApi : IDisposable
{
    private const string BaseUrl = "https://api.vrchat.cloud/api/1";

    // VRChat API は「アプリ名/バージョン (連絡先)」形式の User-Agent を要求する。
    // 連絡先(配布者への連絡用)は exe 内に平文で置かないよう XOR で符号化して持ち、実行時に復元する。
    // ※ 強い秘匿ではない(通信は HTTPS 内で平文送信され、逆コンパイルでも判明する)。単純な文字列検索よけ。
    // 連絡先を変えるときは tools/encode-contact.ps1 でバイト列を作り直して差し替える。
    private static readonly byte[] ContactKey = [0x5A, 0xC3, 0x2F, 0x91, 0x7E, 0x08, 0xB4, 0x6D];
    private static readonly byte[] ContactEnc =
    [
        0x32, 0xB7, 0x5B, 0xE1, 0x0D, 0x32, 0x9B, 0x42, 0x3D, 0xAA, 0x5B, 0xF9, 0x0B, 0x6A,
        0x9A, 0x0E, 0x35, 0xAE, 0x00, 0xC3, 0x0B, 0x7D, 0xE7, 0x19, 0x3B, 0xB1, 0x4D, 0xF5,
        0x4E, 0x3B, 0x9B, 0x3B, 0x08, 0x80, 0x6E, 0xE7, 0x1F, 0x7C, 0xD5, 0x1F, 0x19, 0xAB,
        0x4E, 0xFF, 0x19, 0x6D, 0xC6,
    ];

    /// <summary>連絡先(復号済み)。publish.ps1 の既定値チェックにも使う。</summary>
    public static string Contact
    {
        get
        {
            var b = new byte[ContactEnc.Length];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)(ContactEnc[i] ^ ContactKey[i % ContactKey.Length]);
            return Encoding.UTF8.GetString(b);
        }
    }

    private static readonly string UserAgent = BuildUserAgent();

    private static string BuildUserAgent()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var ver = v is null ? "1.0.0" : v.ToString(3);
        return "VRCAvatarChanger/" + ver + " (" + Contact + ")";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly string _cookiePath;

    public VRChatApi()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        _cookiePath = AppPaths.In("session.json");

        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        LoadCookies();
    }

    // ---------------- セッション保存 ----------------

    private sealed record SavedSession(string? Auth, string? TwoFactorAuth);

    // DPAPI (CurrentUser) で暗号化して保存する。同じ Windows ユーザーでしか復号できない。
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("VRCAvatarChanger.session.v1");

    private void LoadCookies()
    {
        try
        {
            if (!File.Exists(_cookiePath)) return;
            var encrypted = File.ReadAllBytes(_cookiePath);
            var plain = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            var s = JsonSerializer.Deserialize<SavedSession>(plain);
            if (s is null) return;
            var uri = new Uri(BaseUrl);
            if (!string.IsNullOrEmpty(s.Auth)) _cookies.Add(uri, new Cookie("auth", s.Auth) { Domain = uri.Host, Path = "/" });
            if (!string.IsNullOrEmpty(s.TwoFactorAuth)) _cookies.Add(uri, new Cookie("twoFactorAuth", s.TwoFactorAuth) { Domain = uri.Host, Path = "/" });
        }
        catch { /* 壊れていたら無視して再ログインしてもらう */ }
    }

    public void SaveCookies()
    {
        var c = _cookies.GetCookies(new Uri(BaseUrl));
        var s = new SavedSession(c["auth"]?.Value, c["twoFactorAuth"]?.Value);
        var plain = JsonSerializer.SerializeToUtf8Bytes(s);
        AtomicFile.WriteAllBytes(_cookiePath, ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser));
    }

    public bool HasSavedSession => _cookies.GetCookies(new Uri(BaseUrl))["auth"] is not null;

    /// <summary>ブラウザログイン(Discord/Google 等)で得たクッキーをセッションとして採用する。</summary>
    public void SetSessionCookies(string auth, string? twoFactorAuth)
    {
        var uri = new Uri(BaseUrl);
        foreach (Cookie c in _cookies.GetCookies(uri)) c.Expired = true;
        _cookies.Add(uri, new Cookie("auth", auth) { Domain = uri.Host, Path = "/" });
        if (!string.IsNullOrEmpty(twoFactorAuth))
            _cookies.Add(uri, new Cookie("twoFactorAuth", twoFactorAuth) { Domain = uri.Host, Path = "/" });
        SaveCookies();
    }

    // ---------------- 認証 ----------------

    /// <summary>保存済みクッキーで現在のユーザーを取得。未ログインなら null。</summary>
    public async Task<CurrentUser?> TryGetCurrentUserAsync(CancellationToken ct = default)
    {
        if (!HasSavedSession) return null;
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/auth/user"), ct);
        if (res.Status == HttpStatusCode.Unauthorized) return null;
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);
        if (res.Body.Contains("requiresTwoFactorAuth")) return null;
        return JsonSerializer.Deserialize<CurrentUser>(res.Body, JsonOptions);
    }

    /// <summary>ユーザー名/パスワードでログイン。2FA が必要なら TwoFactorRequiredException。</summary>
    public async Task<CurrentUser> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}"));
        var res = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/auth/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            return req;
        }, ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);

        using var doc = JsonDocument.Parse(res.Body);
        if (doc.RootElement.TryGetProperty("requiresTwoFactorAuth", out var methods))
        {
            SaveCookies();
            throw new TwoFactorRequiredException(methods.EnumerateArray().Select(m => m.GetString() ?? "").ToList());
        }
        SaveCookies();
        return JsonSerializer.Deserialize<CurrentUser>(res.Body, JsonOptions)!;
    }

    /// <summary>2FA コードを検証。method は "totp" / "otp" / "emailOtp"。</summary>
    public async Task<CurrentUser> VerifyTwoFactorAsync(string method, string code, CancellationToken ct = default)
    {
        var path = method switch
        {
            "totp" => "totp",
            "otp" => "otp",
            "emailOtp" => "emailotp",
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
        var payload = JsonSerializer.Serialize(new { code = code.Trim() });
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/twofactorauth/{path}/verify")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        }, ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);

        using var doc = JsonDocument.Parse(res.Body);
        if (!(doc.RootElement.TryGetProperty("verified", out var v) && v.GetBoolean()))
            throw new VRChatApiException("認証コードが正しくありません。");

        SaveCookies();
        return (await TryGetCurrentUserAsync(ct)) ?? throw new VRChatApiException("2FA 後のユーザー取得に失敗しました。");
    }

    public async Task LogoutAsync()
    {
        try { using var _ = await _http.PutAsync($"{BaseUrl}/logout", null); } catch { }
        foreach (Cookie c in _cookies.GetCookies(new Uri(BaseUrl))) c.Expired = true;
        if (File.Exists(_cookiePath)) File.Delete(_cookiePath);
    }

    // ---------------- アバター ----------------

    /// <summary>自分がアップロードしたアバター一覧。</summary>
    public async Task<List<Avatar>> GetOwnAvatarsAsync(CancellationToken ct = default)
        => await GetAllPagesAsync("/avatars?user=me&releaseStatus=all&sort=updated&order=descending", ct);

    /// <summary>お気に入り登録したアバター一覧。</summary>
    public async Task<List<Avatar>> GetFavoriteAvatarsAsync(CancellationToken ct = default)
    {
        // /avatars/favorites は tag(グループ名 avatars1, avatars2...)を付けないと最初のグループしか返さない。
        // グループ一覧を取ってから、グループごとに全ページ取得して結合する。
        var groups = await GetFavoriteGroupsAsync(ct);
        if (groups.Count == 0)
            return await GetAllPagesAsync("/avatars/favorites?sort=updated&order=descending", ct);

        var result = new List<Avatar>();
        var seen = new HashSet<string>();
        foreach (var g in groups)
        {
            var page = await GetAllPagesAsync($"/avatars/favorites?tag={Uri.EscapeDataString(g.Name)}&sort=updated&order=descending", ct);
            foreach (var a in page)
            {
                if (!seen.Add(a.Id)) continue;
                a.FavoriteGroup = FriendlyGroupName(g);
                result.Add(a);
            }
        }
        return result;
    }

    /// <summary>表示用のグループ名。ユーザーが名前を付けていなければ「お気に入り 1」のように番号で。</summary>
    public static string FriendlyGroupName(FavoriteGroup g)
    {
        var d = g.DisplayName?.Trim() ?? "";
        if (d.Length > 0 && !string.Equals(d, g.Name, StringComparison.OrdinalIgnoreCase)) return d;
        var m = Regex.Match(g.Name, @"^avatars(\d+)$", RegexOptions.IgnoreCase);
        return m.Success ? $"お気に入り {m.Groups[1].Value}" : g.Name;
    }

    /// <summary>アバターのお気に入りグループ一覧(avatars1 = "Favorite Avatars 1" など)。</summary>
    public async Task<List<FavoriteGroup>> GetFavoriteGroupsAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await GetJsonAsync<List<FavoriteGroup>>("/favorite/groups?type=avatar&n=100", ct);
            return list.Where(g => g.Type == "avatar" && !string.IsNullOrEmpty(g.Name)).OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
        }
        catch (VRChatApiException) { return []; }
    }

    /// <summary>
    /// お気に入り登録レコード(アバター)。アバター ID → 登録 ID (fvrt_...) と所属グループの対応に使う。
    /// お気に入りから外すには、アバター ID ではなくこの登録 ID が要る。
    /// </summary>
    public async Task<List<Favorite>> GetFavoriteRecordsAsync(CancellationToken ct = default)
    {
        const int pageSize = 100;
        var all = new List<Favorite>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await GetJsonAsync<List<Favorite>>($"/favorites?type=avatar&n={pageSize}&offset={offset}", ct);
            all.AddRange(page);
            if (page.Count < pageSize) break;
        }
        return all;
    }

    /// <summary>アバターをお気に入りに登録する。group は FavoriteGroup.Name ("avatars1" など)。</summary>
    public async Task<Favorite> AddFavoriteAsync(string avatarId, string group, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "avatar",
            favoriteId = RequireAvatarId(avatarId),
            tags = new[] { RequireToken(group) },
        });
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/favorites")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        }, ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);
        return JsonSerializer.Deserialize<Favorite>(res.Body, JsonOptions)
               ?? throw new VRChatApiException("API の応答を解釈できませんでした。");
    }

    /// <summary>お気に入りから外す。favoriteId は GetFavoriteRecordsAsync が返す登録 ID (fvrt_...)。</summary>
    public async Task RemoveFavoriteAsync(string favoriteId, CancellationToken ct = default)
    {
        var id = RequireToken(favoriteId);
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/favorites/{id}"), ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);
    }

    private static readonly Regex AvatarIdPattern = new(
        @"^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidAvatarId(string? id) => id is not null && AvatarIdPattern.IsMatch(id);

    // URL パスに埋め込む前に必ず形式を検証し、パス操作を防ぐ
    private static string RequireAvatarId(string id)
        => IsValidAvatarId(id) ? id : throw new VRChatApiException("アバター ID の形式が不正です。");

    // お気に入り ID (fvrt_...) やグループ名 (avatars1) 用。記号を含まないことだけを確かめる
    private static readonly Regex SafeTokenPattern = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    private static string RequireToken(string value)
        => SafeTokenPattern.IsMatch(value) ? value : throw new VRChatApiException("ID の形式が不正です。");

    public async Task<Avatar> GetAvatarAsync(string avatarId, CancellationToken ct = default)
        => await GetJsonAsync<Avatar>($"/avatars/{RequireAvatarId(avatarId)}", ct);

    /// <summary>アバターを切り替える。VRChat 起動中ならゲーム内にも即反映される。</summary>
    public async Task<CurrentUser> SelectAvatarAsync(string avatarId, CancellationToken ct = default)
    {
        var id = RequireAvatarId(avatarId);
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl}/avatars/{id}/select"), ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);
        return JsonSerializer.Deserialize<CurrentUser>(res.Body, JsonOptions)!;
    }

    // 画像 URL は API 応答由来。VRChat のホスト以外・https 以外には一切リクエストを出さない
    private static bool IsAllowedImageUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps
           && (u.Host == "api.vrchat.cloud" || u.Host == "vrchat.com" || u.Host.EndsWith(".vrchat.cloud", StringComparison.Ordinal));

    private const int MaxImageBytes = 10 * 1024 * 1024;

    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        if (!IsAllowedImageUrl(url)) return null;
        try
        {
            await WaitCooldownAsync(ct);
            // ヘッダだけ先に読み、本文はサイズ上限を確かめながら受信する
            // (Content-Length を返さない応答でも 10MB で打ち切る)
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // 画像も同じレート制限の対象。次の API 呼び出しまで間を空ける
                _cooldownUntil = DateTimeOffset.UtcNow + RetryWait(res, 0);
                return null;
            }
            if (!res.IsSuccessStatusCode) return null;
            if (res.Content.Headers.ContentLength > MaxImageBytes) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buf, ct)) > 0)
            {
                if (ms.Length + read > MaxImageBytes) return null;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ---------------- 送信 (レート制限のリトライつき) ----------------

    /// <summary>1 回の送信結果。本文まで読み終えているので HttpResponseMessage は持ち回らない。</summary>
    private readonly record struct ApiResponse(HttpStatusCode Status, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
    }

    private const int MaxRetries = 2;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30);

    /// <summary>429 を受けたら、この時刻まで次のリクエストを送らない (並行している他のリクエストもここで待つ)。</summary>
    private DateTimeOffset _cooldownUntil;

    private async Task WaitCooldownAsync(CancellationToken ct)
    {
        var wait = _cooldownUntil - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait > MaxRetryWait ? MaxRetryWait : wait, ct);
    }

    /// <summary>
    /// リクエストを送り、本文まで読んで返す。レート制限 (429) と GET の一時的なサーバーエラーは、
    /// Retry-After (無ければ 2 秒 → 4 秒) だけ待って数回まで再試行する。
    /// HttpRequestMessage は再送できないので、呼び出し側は「作る関数」を渡す。
    /// </summary>
    private async Task<ApiResponse> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitCooldownAsync(ct);
            using var req = makeRequest();
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var limited = res.StatusCode == HttpStatusCode.TooManyRequests;
            if (limited || ShouldRetry(res.StatusCode, req.Method))
            {
                var wait = RetryWait(res, attempt);
                // レート制限は接続全体にかかるので、他のリクエストにも待ってもらう
                if (limited) _cooldownUntil = DateTimeOffset.UtcNow + wait;
                if (attempt < MaxRetries)
                {
                    await Task.Delay(wait, ct);
                    continue;
                }
            }
            return new ApiResponse(res.StatusCode, body);
        }
    }

    /// <summary>再試行してよい失敗か。取得 (GET) 以外は、二重に実行されないよう再試行しない。</summary>
    private static bool ShouldRetry(HttpStatusCode status, HttpMethod method)
        => method == HttpMethod.Get
           && status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>次の試行までの待ち時間。サーバーが Retry-After を返していればそれに従う。</summary>
    private static TimeSpan RetryWait(HttpResponseMessage res, int attempt)
    {
        var ra = res.Headers.RetryAfter;
        var hinted = ra?.Delta ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var wait = hinted is { } h && h > TimeSpan.Zero ? h : TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
        return wait > MaxRetryWait ? MaxRetryWait : wait;
    }

    // ---------------- 内部 ----------------

    private async Task<List<Avatar>> GetAllPagesAsync(string pathAndQuery, CancellationToken ct)
    {
        const int pageSize = 100;
        var all = new List<Avatar>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await GetJsonAsync<List<Avatar>>($"{pathAndQuery}&n={pageSize}&offset={offset}", ct);
            all.AddRange(page);
            if (page.Count < pageSize) break;
        }
        return all;
    }

    private async Task<T> GetJsonAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery), ct);
        if (!res.IsSuccess) throw new VRChatApiException(ExtractError(res.Body, res.Status), res.Status);
        return JsonSerializer.Deserialize<T>(res.Body, JsonOptions)
               ?? throw new VRChatApiException("API の応答を解釈できませんでした。");
    }

    /// <summary>VRChat のエラー応答を、利用者向けの日本語メッセージにする。英語の生メッセージはそのまま見せない。</summary>
    private static string ExtractError(string body, HttpStatusCode status)
    {
        string? server = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                server = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) ? m.GetString() : err.ToString();
        }
        catch { }
        server = server?.Trim().Trim('"');

        // よく返ってくるメッセージは個別に日本語化
        if (!string.IsNullOrEmpty(server))
        {
            var s = server.ToLowerInvariant();
            if (s.Contains("user-agent") || s.Contains("identify yourself"))
                return "VRChat がこのアプリからの通信を受け付けませんでした(アプリの識別情報が未設定)。配布された最新版を使うか、配布者に連絡してください。";
            if (s.Contains("invalid username") || s.Contains("invalid password") || s.Contains("email or password"))
                return "ユーザー名(メールアドレス)またはパスワードが違います。";
            if (s.Contains("two-factor") || s.Contains("2fa") || s.Contains("verification code") || s.Contains("invalid code"))
                return "認証コードが正しくありません。もう一度入力してください。";
            if (s.Contains("avatar not found"))
                return "そのアバターは見つかりませんでした(削除されたか、ID が違う可能性があります)。";
            if (s.Contains("not public") || s.Contains("private"))
                return "このアバターは非公開のため使用できません。";
            if (s.Contains("too many favorites") || s.Contains("maximum number of favorites"))
                return "そのお気に入りグループは上限に達しています。別のグループを選ぶか、いらないものを外してください。";
            if (s.Contains("already") && s.Contains("favorite"))
                return "そのアバターはすでにお気に入りに登録されています。";
            if (s.Contains("too many") || s.Contains("rate limit"))
                return "VRChat へのアクセスが多すぎます。1 分ほど待ってからもう一度お試しください。";
            if (s.Contains("banned") || s.Contains("suspended"))
                return "このアカウントは現在 VRChat で制限されています。";
        }

        var jp = status switch
        {
            HttpStatusCode.Unauthorized => "ログイン情報が正しくないか、セッションの期限が切れています。もう一度ログインしてください。",
            HttpStatusCode.Forbidden => "VRChat に操作を拒否されました(非公開アバター、または権限がない可能性があります)。",
            HttpStatusCode.NotFound => "見つかりませんでした(削除されたか、ID が違う可能性があります)。",
            HttpStatusCode.TooManyRequests => "VRChat へのアクセスが多すぎます。1 分ほど待ってからもう一度お試しください。",
            >= HttpStatusCode.InternalServerError => "VRChat 側で問題が起きているようです。しばらくしてからもう一度お試しください。",
            _ => "VRChat からエラーが返されました。",
        };
        // 問い合わせ対応のため、原文は小さく後ろに添える
        return string.IsNullOrEmpty(server) ? $"{jp} (HTTP {(int)status})" : $"{jp} (HTTP {(int)status}: {server})";
    }

    public void Dispose() => _http.Dispose();
}

// ---------------- モデル ----------------

public sealed class CurrentUser
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CurrentAvatar { get; set; } = "";
    public string? CurrentAvatarThumbnailImageUrl { get; set; }
}

public sealed class Avatar
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string AuthorId { get; set; } = "";
    public string? Description { get; set; }
    public string? ThumbnailImageUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string ReleaseStatus { get; set; } = "";
    public string? FavoriteGroup { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>お気に入りの登録レコード。Id が登録そのものの ID、FavoriteId が対象 (アバター) の ID。</summary>
public sealed class Favorite
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string FavoriteId { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}

public sealed class FavoriteGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "";
    public string? OwnerId { get; set; }
}
