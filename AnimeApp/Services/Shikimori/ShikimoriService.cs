using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeApp.Models;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services.Shikimori;
public interface IShikimoriService : IUserLibraryProvider
{
}

public sealed class ShikimoriService(HttpClient http, OAuthOptions initialOptions, ITokenStore tokens, CredentialManager? credentials = null, AnimeApp.Services.Database.LibraryRepository? repository = null) : IShikimoriService, IMetadataProvider, IScheduleProvider
{
    private OAuthOptions options => credentials?.Current ?? initialOptions;
    public CredentialManager? Credentials => credentials;
    public string RedirectUri => options.RedirectUri;
    public bool ShowAdultContent { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> contentRatings = new();
    private bool ratingsLoaded;
    private readonly SemaphoreSlim contentGate = new(1, 1);
    private const string ContentCacheKey = "content-visibility-v2";
    private const string ContentQuery = "&censored=true&rating=!r_plus,!rx";

    public async Task<IReadOnlyList<Anime>> FilterContent(IEnumerable<Anime> anime, CancellationToken ct = default)
    {
        var items = anime.ToArray();
        if (ShowAdultContent || items.Length == 0) return items;
        await contentGate.WaitAsync(ct);
        try
        {
            if (ShowAdultContent) return items;
            await LoadContentCache(ct);
            foreach (var item in items.Where(a => a.ShikimoriId > 0))
                if (SafeRating(item.AgeRating)) contentRatings[item.ShikimoriId] = true;
                else if (item.AgeRating.ToLowerInvariant() is "r_plus" or "rx") contentRatings[item.ShikimoriId] = false;

            foreach (var batch in items.Where(a => a.ShikimoriId > 0 && !contentRatings.ContainsKey(a.ShikimoriId))
                         .Select(a => a.ShikimoriId).Distinct().Chunk(50))
            {
                // REST filtering is the same policy used by search. It does not depend
                // on a second GraphQL endpoint being available on the user's network.
                using var d = await Send(HttpMethod.Get, "/api/animes?ids=" + string.Join(",", batch) + "&limit=50" + ContentQuery, null, false, ct);
                if (d.RootElement.ValueKind != JsonValueKind.Array)
                    throw new IOException("Не удалось проверить фильтр 18+. Повторите загрузку списка.");
                var permitted = d.RootElement.EnumerateArray().Select(row => long.TryParse(row.GetProperty("id").ToString(), out var id) ? id : 0).ToHashSet();
                foreach (var id in batch) contentRatings[id] = permitted.Contains(id);
            }
            if (repository is not null) await repository.SaveSetting(ContentCacheKey, contentRatings.ToDictionary(), ct);
            return ShowAdultContent ? items : items.Where(a => contentRatings.GetValueOrDefault(a.ShikimoriId)).ToArray();
        }
        finally { contentGate.Release(); }
    }

    private async Task LoadContentCache(CancellationToken ct)
    {
        if (!ratingsLoaded && repository is not null)
        {
            foreach (var pair in await repository.Setting<Dictionary<long, bool>>(ContentCacheKey, ct) ?? [])
                contentRatings.TryAdd(pair.Key, pair.Value);
            // Legacy false also meant missing/unknown rating, so it is not a ban.
            foreach (var pair in await repository.Setting<Dictionary<long, bool>>("content-ratings", ct) ?? [])
                if (pair.Value) contentRatings.TryAdd(pair.Key, true);
            ratingsLoaded = true;
        }
    }

    private async Task<List<Anime>> SearchResults(JsonElement rows, bool filtered, CancellationToken ct)
    {
        var items = rows.EnumerateArray().Select(Parse).ToList();
        if (filtered)
        {
            await contentGate.WaitAsync(ct);
            try
            {
                await LoadContentCache(ct);
                foreach (var item in items.Where(a => a.ShikimoriId > 0))
                    contentRatings[item.ShikimoriId] = item.AgeRating.ToLowerInvariant() is not ("r_plus" or "rx");
                if (repository is not null) await repository.SaveSetting(ContentCacheKey, contentRatings.ToDictionary(), ct);
                return items.Where(a => contentRatings.GetValueOrDefault(a.ShikimoriId)).ToList();
            }
            finally { contentGate.Release(); }
        }
        // Respect a toggle changed while the request was in flight.
        return ShowAdultContent ? items : (await FilterContent(items, ct)).ToList();
    }
    public static bool SafeRating(string rating) => rating.ToLowerInvariant() is "g" or "pg" or "pg_13" or "r";

    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextRequest;
    public string AuthorizationUrl(string? state = null)
    {
        if (!options.Configured)
            throw new InvalidOperationException("В этой тестовой сборке вход Shikimori ещё не подключён. Просмотр аниме доступен без входа.");
        return $"{options.BaseUrl}/oauth/authorize?client_id={Uri.EscapeDataString(options.ClientId)}&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}&response_type=code&scope=user_rates" + (state is null ? "" : "&state=" + Uri.EscapeDataString(state));
    }

    public async Task Connect(string code, CancellationToken ct = default)
    {
        if (!options.Configured)
            throw new InvalidOperationException("Сначала сохраните данные OAuth-приложения в настройках Shikimori.");
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Вставьте код, показанный Shikimori после разрешения доступа.");
        await Token(new() { ["grant_type"] = "authorization_code", ["code"] = code.Trim(), ["redirect_uri"] = options.RedirectUri }, ct);
    }

    private async Task Token(Dictionary<string, string> values, CancellationToken ct)
    {
        if (!options.Configured)
            throw new InvalidOperationException("Данные OAuth-приложения отсутствуют. Сохраните их в настройках.");
        using var doc = await ExchangeToken(values, ct);
        var r = doc.RootElement;
        await tokens.Write(new(r.GetProperty("access_token").GetString()!, r.GetProperty("refresh_token").GetString()!, DateTimeOffset.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32())), ct);
    }

    private async Task<JsonDocument> ExchangeToken(Dictionary<string, string> values, CancellationToken ct)
    {
        if (options.IsBrokerConfigured)
        {
            // Only the authorization code/refresh token goes to the configured broker.
            // The application secret never belongs in the desktop distribution.
            using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenBrokerUrl) { Content = new FormUrlEncodedContent(values) };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new IOException("Не удалось подключиться к Shikimori. Повторите вход немного позже.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        values["client_id"] = options.ClientId;
        values["client_secret"] = options.ClientSecret;
        return await Send(HttpMethod.Post, "/oauth/token", () => new FormUrlEncodedContent(values), false, ct, values["grant_type"]);
    }

    public async Task Refresh(CancellationToken ct = default)
    {
        await refreshGate.WaitAsync(ct);
        try
        {
            var t = await tokens.Read(ct) ?? throw new InvalidOperationException("Подключите Shikimori в настройках.");
            await Token(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = t.RefreshToken }, ct);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<JsonDocument> Send(HttpMethod method, string path, Func<HttpContent>? body, bool auth, CancellationToken ct, string? grant = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await requestGate.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                var delay = nextRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
                using var req = new HttpRequestMessage(method, options.BaseUrl.TrimEnd('/') + path);
                req.Headers.UserAgent.ParseAdd(options.ApplicationName);
                req.Content = body?.Invoke();
                if (auth)
                {
                    var token = await tokens.Read(ct) ?? throw new InvalidOperationException("Подключите аккаунт Shikimori.");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                }

                response = await http.SendAsync(req, ct);
                nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(1100);
            }
            catch (TaskCanceledException)when (!ct.IsCancellationRequested)
            {
                throw new IOException("Shikimori не ответил за 25 секунд. Локальные изменения сохранены.");
            }
            catch (HttpRequestException)
            {
                throw new IOException("Нет соединения с Shikimori. Проверьте интернет. Изменения ожидают синхронизации.");
            }
            finally
            {
                requestGate.Release();
            }

            using (response)
            {
                if (grant is not null && !response.IsSuccessStatusCode && (int)response.StatusCode < 500 && (int)response.StatusCode != 429)
                {
                    var error = "";
                    var description = "";
                    try
                    {
                        using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        error = Text(failure.RootElement, "error");
                        description = Text(failure.RootElement, "error_description");
                    }
                    catch (JsonException)
                    {
                        error = "invalid_response";
                    }

                    throw new InvalidOperationException(OAuthErrorMessage(error, description, grant));
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && auth && attempt == 0)
                {
                    await Refresh(ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Shikimori не принял обновлённый токен. Доступ мог быть отозван — подключите аккаунт заново.");
                if ((int)response.StatusCode == 429)
                    throw new IOException("Лимит запросов Shikimori. Повторите синхронизацию примерно через минуту.");
                if (!response.IsSuccessStatusCode)
                    throw new ShikimoriApiException(response.StatusCode, method == HttpMethod.Get ? "чтение библиотеки или каталога" : "сохранение изменений");
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
        }

        throw new IOException("Не удалось обновить авторизацию.");
    }

    public static string OAuthErrorMessage(string error, string description, string grant)
    {
        // Never expose a remote payload; providers may echo submitted credentials.
        if (error == "access_denied")
            return "Вы отказали приложению в доступе. Подключение можно начать заново.";
        if (description.Contains("redirect", StringComparison.OrdinalIgnoreCase))
            return "Redirect URI не совпадает с настройками OAuth-приложения Shikimori.";
        if (error == "invalid_client")
            return "Shikimori не принял Client ID или Client Secret. Проверьте оба значения: сервер не уточняет, какое из них неверно.";
        if (error == "invalid_grant")
            return grant == "refresh_token" ? "Не удалось обновить доступ: refresh token недействителен или доступ отозван. Подключите Shikimori заново." : "Код авторизации уже недействителен: истёк или был использован. Получите новый код через Shikimori.";
        return "Не удалось подключить Shikimori. Проверьте данные OAuth-приложения и получите новый код авторизации.";
    }

    public async Task<UserIdentity> CurrentUser(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/api/users/whoami", null, true, ct);
        var root = d.RootElement;
        var avatar = root.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "x80") : "";
        if (string.IsNullOrEmpty(avatar)) avatar = Text(root, "avatar");
        if (avatar.StartsWith("//")) avatar = "https:" + avatar;
        else if (avatar.StartsWith('/')) avatar = "https://shikimori.io" + avatar;
        if (!Uri.TryCreate(avatar, UriKind.Absolute, out var uri) || uri.Scheme != "https") avatar = "";
        return new(root.GetProperty("id").GetInt64(), Text(root, "nickname"), avatar);
    }

    public async Task<List<Anime>> Rates(long user, CancellationToken ct = default)
    {
        var result = new List<Anime>();
        for (int page = 1;; page++)
        {
            using var d = await Send(HttpMethod.Get, $"/api/users/{user}/anime_rates?page={page}&limit=100", null, true, ct);
            foreach (var r in d.RootElement.EnumerateArray())
            {
                var a = Parse(r.GetProperty("anime"));
                result.Add(a with { AddedAt = DateTimeOffset.TryParse(Text(r, "created_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var added) ? added : null, RateId = r.GetProperty("id").GetInt64(), EpisodesWatched = Number(r, "episodes"), UserStatus = Text(r, "status"), Score = Number(r, "score"), Rewatches = Number(r, "rewatches") });
            }

            if (d.RootElement.GetArrayLength() < 100)
                break;
        }

        return result;
    }

    public async Task<List<Anime>> Search(string query, CancellationToken ct = default)
    {
        var filtered = !ShowAdultContent;
        using var d = await Send(HttpMethod.Get, $"/api/animes?search={Uri.EscapeDataString(query)}&limit=30" + (filtered ? ContentQuery : ""), null, false, ct);
        return await SearchResults(d.RootElement, filtered, ct);
    }

    public bool SupportsSearchFilters => true;

    public async Task<IReadOnlyDictionary<long, string>> Posters(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<long, string>();
        foreach (var batch in ids.Where(id => id > 0).Distinct().Chunk(50))
        {
            var query = "{ animes(ids: \"" + string.Join(",", batch) + "\", limit: 50) { id poster { originalUrl } } }";
            using var d = await Send(HttpMethod.Post, "/api/graphql", () => new StringContent(JsonSerializer.Serialize(new { query }), System.Text.Encoding.UTF8, "application/json"), false, ct);
            if (!d.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("animes", out var rows))
                throw new IOException("Не удалось обновить обложки Shikimori.");
            foreach (var row in rows.EnumerateArray())
                if (long.TryParse(row.GetProperty("id").ToString(), out var id) && batch.Contains(id) && row.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Object)
                {
                    var url = Text(poster, "originalUrl");
                    if (PosterService.IsUsable(url)) result[id] = url;
                }
        }
        return result;
    }

    public async Task<List<Anime>> Search(AnimeSearch query, CancellationToken ct = default)
    {
        var filtered = !ShowAdultContent;
        using var d = await Send(HttpMethod.Get, "/api/animes?" + query.ToShikimoriQuery() + (filtered ? ContentQuery : ""), null, false, ct);
        return await SearchResults(d.RootElement, filtered, ct);
    }

    public async Task<Anime> Details(long id, CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, $"/api/animes/{id}", null, false, ct);
        return Parse(d.RootElement);
    }

    public string MetadataId => "shikimori";
    public string MetadataName => "Shikimori";

    public async Task<IReadOnlyList<AnimeRelation>> Relations(long id, CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, $"/api/animes/{id}/related", null, false, ct);
        return d.RootElement.EnumerateArray().Where(e => e.TryGetProperty("anime", out var a) && a.ValueKind == JsonValueKind.Object).Select(e => new AnimeRelation(Parse(e.GetProperty("anime")), Text(e, "relation").ToLowerInvariant().Replace(' ', '_'))).ToArray();
    }

    public async Task<IReadOnlyList<ScheduleItem>> Schedule(CancellationToken ct = default)
    {
        using var d = await Send(HttpMethod.Get, "/api/calendar", null, false, ct);
        return d.RootElement.EnumerateArray().Select(e => new ScheduleItem(Parse(e.GetProperty("anime")), Number(e, "next_episode"), DateTimeOffset.TryParse(Text(e, "next_episode_at"), out var at) ? at : null)).ToArray();
    }

    public async Task Update(long user, Anime a, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>
        {
            ["user_rate[user_id]"] = user.ToString(),
            ["user_rate[target_id]"] = a.ShikimoriId.ToString(),
            ["user_rate[target_type]"] = "Anime",
            ["user_rate[episodes]"] = a.EpisodesWatched.ToString(),
            ["user_rate[status]"] = a.UserStatus
        };
        using var d = await Send(a.RateId.HasValue ? HttpMethod.Patch : HttpMethod.Post, a.RateId.HasValue ? $"/api/v2/user_rates/{a.RateId}" : "/api/v2/user_rates", () => new FormUrlEncodedContent(values), true, ct);
    }

    public async Task UpdateFields(long user, Anime a, PendingChange p, CancellationToken ct = default)
    {
        if (a.ShikimoriId <= 0 || p.AnimeId != a.ShikimoriId)
            throw new InvalidOperationException("Тайтл ещё не сопоставлен с Shikimori. Прогресс сохранён на компьютере.");
        if (p.Score is < 0 or > 10)
            throw new InvalidOperationException("Оценка должна быть от 0 до 10.");
        var values = new Dictionary<string, string>();
        if (!a.RateId.HasValue)
        {
            values["user_rate[user_id]"] = user.ToString();
            values["user_rate[target_id]"] = a.ShikimoriId.ToString();
            values["user_rate[target_type]"] = "Anime";
        }

        if (p.EpisodesChanged)
            values["user_rate[episodes]"] = p.Episodes.ToString();
        if (p.StatusChanged)
            values["user_rate[status]"] = p.Status;
        if (p.Score is int score)
            values["user_rate[score]"] = score.ToString();
        if (values.Count == 0)
            return;
        using var d = await Send(a.RateId.HasValue ? HttpMethod.Patch : HttpMethod.Post, a.RateId.HasValue ? $"/api/v2/user_rates/{a.RateId}" : "/api/v2/user_rates", () => new FormUrlEncodedContent(values), true, ct);
    }

    public async Task<Anime?> ReadRate(long user, Anime anime, CancellationToken ct = default)
    {
        try
        {
            var path = anime.RateId is long id ? $"/api/v2/user_rates/{id}" : $"/api/v2/user_rates?user_id={user}&target_id={anime.ShikimoriId}&target_type=Anime";
            using var d = await Send(HttpMethod.Get, path, null, true, ct);
            var r = d.RootElement;
            if (r.ValueKind == JsonValueKind.Array)
            {
                if (r.GetArrayLength() == 0)
                    return null;
                r = r[0];
            }

            if (r.GetProperty("user_id").GetInt64() != user || r.GetProperty("target_id").GetInt64() != anime.ShikimoriId || Text(r, "target_type") != "Anime")
                throw new InvalidOperationException("Shikimori вернул запись другого тайтла или аккаунта.");
            return anime with
            {
                AddedAt = DateTimeOffset.TryParse(Text(r, "created_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var added) ? added : null, RateId = r.GetProperty("id").GetInt64(),
                EpisodesWatched = Number(r, "episodes"),
                UserStatus = Text(r, "status"),
                Score = Number(r, "score"),
                Rewatches = Number(r, "rewatches")
            };
        }
        catch (ShikimoriApiException ex)when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return (await Rates(user, ct)).FirstOrDefault(a => a.ShikimoriId == anime.ShikimoriId);
        }
    }

    private Anime Parse(JsonElement e)
    {
        var poster = e.TryGetProperty("image", out var im) ? Text(im, "original") : "";
        return new()
        {
            ShikimoriId = e.GetProperty("id").GetInt64(),
            Name = Text(e, "name"),
            RussianName = Text(e, "russian"),
            EnglishName = First(e, "english"),
            JapaneseName = First(e, "japanese"),
            AlternativeNames = e.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array ? synonyms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [],
            Poster = string.IsNullOrEmpty(poster) ? "" : new Uri(new Uri(options.BaseUrl), poster).ToString(),
            Description = Regex.Replace(Text(e, "description_html"), "<[^>]+>", " ").Trim()is { Length: > 0 } description ? WebUtility.HtmlDecode(description) : "Описание пока отсутствует.",
            Year = DateTime.TryParse(Text(e, "aired_on"), out var date) ? date.Year : null,
            AirMonth = DateTime.TryParse(Text(e, "aired_on"), out var aired) ? aired.Month : null,
            Kind = Text(e, "kind"),
            Status = Text(e, "status"),
            AgeRating = Text(e, "rating"),
            CommunityScore = e.TryGetProperty("score", out var rating) && double.TryParse(rating.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var score) ? score : 0,
            EpisodesTotal = Number(e, "episodes")
        };
    }

    private static string Text(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Number(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
    private static string First(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.String).ToString() : "";
}

public sealed class ShikimoriApiException(HttpStatusCode status, string operation = "запрос") : IOException("Shikimori: " + operation + ", ошибка " + (int)status + ". Локальные данные сохранены.")
{
    public HttpStatusCode Status => status;
}
