using AnimeApp.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AnimeApp.Services.Database;
// SQLite work is explicitly scheduled off the UI thread: SQLite's async API executes synchronously.
public sealed class DatabaseService(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<T> Execute<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
                c.Open();
                return work(c);
            }, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task Initialize(CancellationToken ct = default) => Execute(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
 PRAGMA journal_mode=WAL;
 CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, json TEXT NOT NULL);
 CREATE TABLE IF NOT EXISTS anime(user_id INTEGER NOT NULL, id INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(user_id,id));
 CREATE TABLE IF NOT EXISTS mappings(anime_id INTEGER NOT NULL, provider TEXT NOT NULL, release_id TEXT NOT NULL, PRIMARY KEY(anime_id,provider));
 CREATE TABLE IF NOT EXISTS pending(user_id INTEGER NOT NULL, anime_id INTEGER NOT NULL, revision TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(user_id,anime_id));
 CREATE TABLE IF NOT EXISTS local_progress(user_id INTEGER NOT NULL, revision TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(user_id,revision));
 CREATE TABLE IF NOT EXISTS history(user_id INTEGER NOT NULL, anime_id INTEGER NOT NULL, provider TEXT NOT NULL, episode TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(user_id,anime_id,provider,episode));
 PRAGMA user_version=1;
 """;
        cmd.ExecuteNonQuery();
        return true;
    }, ct);
    internal static void Run(SqliteConnection c, string sql, params (string, object? )[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var(k, v)in args)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal static List<T> Read<T>(SqliteConnection c, string sql, params (string, object? )[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var(k, v)in args)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
            list.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!);
        return list;
    }
}

public sealed class LibraryRepository(DatabaseService db)
{
    public Task RestoreUnlinked(long user, UnlinkedProgress entry, Anime target, CancellationToken ct = default) => db.Execute(c =>
    {
        if (entry.Change.UserId != user || target.ShikimoriId <= 0) throw new InvalidOperationException("Неверный аккаунт или тайтл.");
        if (target.EpisodesTotal > 0 && entry.Change.Episodes > target.EpisodesTotal) throw new InvalidOperationException("У выбранного тайтла меньше серий. Проверьте сезон.");
        using var tx = c.BeginTransaction();
        var stored = DatabaseService.Read<UnlinkedProgress>(c, "SELECT json FROM local_progress WHERE user_id=$u AND revision=$r", ("$u", user), ("$r", entry.Change.Revision)).FirstOrDefault();
        if (stored is null) return false;
        var local = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u AND id=$a", ("$u", user), ("$a", target.ShikimoriId)).FirstOrDefault();
        var before = local ?? target;
        var after = before with { EpisodesWatched = Math.Max(before.EpisodesWatched, entry.Change.Episodes), Score = entry.Change.Score ?? before.Score, UserStatus = before.UserStatus == "completed" ? "completed" : entry.Change.Status };
        WriteChange(c, user, before, after);
        foreach (var history in DatabaseService.Read<WatchEntry>(c, "SELECT json FROM history WHERE user_id=$u AND anime_id=$a", ("$u", user), ("$a", entry.Change.AnimeId)).Where(h => h.Title == entry.Title))
        {
            var updated = history with { AnimeId = target.ShikimoriId };
            DatabaseService.Run(c, "INSERT OR IGNORE INTO history VALUES($u,$a,$p,$e,$j)", ("$u", user), ("$a", target.ShikimoriId), ("$p", updated.ProviderId), ("$e", updated.EpisodeId), ("$j", JsonSerializer.Serialize(updated)));
        }
        DatabaseService.Run(c, "DELETE FROM local_progress WHERE user_id=$u AND revision=$r", ("$u", user), ("$r", entry.Change.Revision));
        tx.Commit(); return true;
    }, ct);
    // Legacy builds queued ID=0 even though Shikimori cannot accept it. Preserve it
    // separately, rather than retry forever or erase the user's progress.
    public Task<List<UnlinkedProgress>> Unlinked(long user, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        foreach (var change in DatabaseService.Read<PendingChange>(c, "SELECT json FROM pending WHERE user_id=$u AND anime_id<=0", ("$u", user)))
        {
            var anime = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u AND id=$a", ("$u", user), ("$a", change.AnimeId)).FirstOrDefault();
            var saved = new UnlinkedProgress(change, anime?.Title ?? "Тайтл без названия");
            DatabaseService.Run(c, "INSERT OR IGNORE INTO local_progress VALUES($u,$r,$j)", ("$u", user), ("$r", change.Revision), ("$j", JsonSerializer.Serialize(saved)));
            DatabaseService.Run(c, "DELETE FROM pending WHERE user_id=$u AND anime_id=$a AND revision=$r", ("$u", user), ("$a", change.AnimeId), ("$r", change.Revision));
        }
        tx.Commit();
        return DatabaseService.Read<UnlinkedProgress>(c, "SELECT json FROM local_progress WHERE user_id=$u", ("$u", user));
    }, ct);
    public Task<List<Anime>> Library(long user, CancellationToken ct = default) => db.Execute(c => DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u", ("$u", user)), ct);
    public Task<T?> Setting<T>(string key, CancellationToken ct = default) => db.Execute(c => DatabaseService.Read<T>(c, "SELECT json FROM settings WHERE key=$k", ("$k", key)).FirstOrDefault(), ct);
    public Task SaveSetting<T>(string key, T value, CancellationToken ct = default) => db.Execute(c =>
    {
        DatabaseService.Run(c, "INSERT OR REPLACE INTO settings VALUES($k,$j)", ("$k", key), ("$j", JsonSerializer.Serialize(value)));
        return true;
    }, ct);
    public Task Cache(long user, IEnumerable<Anime> anime, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        foreach (var a in anime)
            DatabaseService.Run(c, "INSERT OR REPLACE INTO anime VALUES($u,$id,$j)", ("$u", user), ("$id", a.ShikimoriId), ("$j", JsonSerializer.Serialize(a)));
        tx.Commit();
        return true;
    }, ct);
    public Task ReplaceRemote(long user, IEnumerable<Anime> anime, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        var existing = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u", ("$u", user)).ToDictionary(a => a.ShikimoriId);
        DatabaseService.Run(c, "DELETE FROM anime WHERE user_id=$u AND id>0 AND id NOT IN (SELECT anime_id FROM pending WHERE user_id=$u)", ("$u", user));
        foreach (var incoming in anime)
        {
            var previous = existing.GetValueOrDefault(incoming.ShikimoriId);
            var a = !PosterService.IsUsable(incoming.Poster) && PosterService.IsUsable(previous?.Poster) ? incoming with { Poster = previous!.Poster } : incoming;
            DatabaseService.Run(c, "INSERT INTO anime SELECT $u,$id,$j WHERE NOT EXISTS (SELECT 1 FROM pending WHERE user_id=$u AND anime_id=$id) ON CONFLICT(user_id,id) DO UPDATE SET json=excluded.json", ("$u", user), ("$id", a.ShikimoriId), ("$j", JsonSerializer.Serialize(a)));
        }
        tx.Commit();
        return true;
    }, ct);
    public Task MergeRemote(long user, IEnumerable<Anime> anime, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        foreach (var incoming in anime)
        {
            var previous = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u AND id=$id", ("$u", user), ("$id", incoming.ShikimoriId)).FirstOrDefault();
            var a = !PosterService.IsUsable(incoming.Poster) && PosterService.IsUsable(previous?.Poster) ? incoming with { Poster = previous!.Poster } : incoming;
            DatabaseService.Run(c, "INSERT INTO anime SELECT $u,$id,$j WHERE NOT EXISTS (SELECT 1 FROM pending WHERE user_id=$u AND anime_id=$id) ON CONFLICT(user_id,id) DO UPDATE SET json=excluded.json", ("$u", user), ("$id", a.ShikimoriId), ("$j", JsonSerializer.Serialize(a)));
        }
        tx.Commit();
        return true;
    }, ct);
    public Task SavePoster(long user, long id, string url, CancellationToken ct = default) => db.Execute(c =>
    {
        if (!PosterService.IsUsable(url)) return false;
        var current = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u AND id=$id", ("$u", user), ("$id", id)).FirstOrDefault();
        if (current is not null)
            DatabaseService.Run(c, "UPDATE anime SET json=$j WHERE user_id=$u AND id=$id", ("$j", JsonSerializer.Serialize(current with { Poster = url })), ("$u", user), ("$id", id));
        return true;
    }, ct);
    public Task Change(long user, Anime before, Anime after, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        WriteChange(c, user, before, after);
        tx.Commit();
        return true;
    }, ct);
    public Task<List<PendingChange>> Pending(long user, CancellationToken ct = default) => db.Execute(c => DatabaseService.Read<PendingChange>(c, "SELECT json FROM pending WHERE user_id=$u", ("$u", user)), ct);
    public Task Ack(PendingChange p, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        var newer = DatabaseService.Read<PendingChange>(c, "SELECT json FROM pending WHERE user_id=$u AND anime_id=$a", ("$u", p.UserId), ("$a", p.AnimeId)).FirstOrDefault();
        if (newer is not null && newer.Revision != p.Revision)
        {
            newer = newer with
            {
                BaseEpisodes = p.EpisodesChanged ? p.Episodes : newer.BaseEpisodes,
                BaseStatus = p.StatusChanged ? p.Status : newer.BaseStatus,
                BaseScore = p.Score ?? newer.BaseScore,
                ExistedRemotely = true
            };
            DatabaseService.Run(c, "UPDATE pending SET json=$j WHERE user_id=$u AND anime_id=$a", ("$j", JsonSerializer.Serialize(newer)), ("$u", p.UserId), ("$a", p.AnimeId));
        }

        DatabaseService.Run(c, "DELETE FROM pending WHERE user_id=$u AND anime_id=$a AND revision=$r", ("$u", p.UserId), ("$a", p.AnimeId), ("$r", p.Revision));
        tx.Commit();
        return true;
    }, ct);
    public Task<AnimeApp.Services.Providers.AnimeIdentity?> MappedIdentity(string provider, string release, CancellationToken ct = default) => db.Execute(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT anime_id FROM mappings WHERE provider=$p AND release_id=$r LIMIT 2";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$r", release);
        using var rows = cmd.ExecuteReader();
        if (!rows.Read())
            return null;
        var id = rows.GetInt64(0);
        return rows.Read() ? null : new AnimeApp.Services.Providers.AnimeIdentity("shikimori", id.ToString());
    }, ct);
    public Task SaveMapping(AnimeSourceMapping m, CancellationToken ct = default) => db.Execute(c =>
    {
        DatabaseService.Run(c, "INSERT OR REPLACE INTO mappings VALUES($a,$p,$r)", ("$a", m.ShikimoriId), ("$p", m.ProviderId), ("$r", m.ProviderAnimeId));
        return true;
    }, ct);
    public Task<string?> Mapping(long anime, string provider, CancellationToken ct = default) => db.Execute(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT release_id FROM mappings WHERE anime_id=$a AND provider=$p";
        cmd.Parameters.AddWithValue("$a", anime);
        cmd.Parameters.AddWithValue("$p", provider);
        return cmd.ExecuteScalar() as string;
    }, ct);
    public Task History(long user, WatchEntry e, CancellationToken ct = default) => db.Execute(c =>
    {
        DatabaseService.Run(c, "INSERT OR REPLACE INTO history VALUES($u,$a,$p,$e,$j)", ("$u", user), ("$a", e.AnimeId), ("$p", e.ProviderId), ("$e", e.EpisodeId), ("$j", JsonSerializer.Serialize(e)));
        return true;
    }, ct);
    public Task<List<WatchEntry>> History(long user, CancellationToken ct = default) => db.Execute(c => DatabaseService.Read<WatchEntry>(c, "SELECT json FROM history WHERE user_id=$u", ("$u", user)).OrderByDescending(x => x.UpdatedAt).ToList(), ct);
    private static void WriteChange(SqliteConnection c, long user, Anime before, Anime after)
    {
        if (after.ShikimoriId <= 0) throw new InvalidOperationException("Тайтл ещё не сопоставлен с Shikimori.");
        var old = DatabaseService.Read<PendingChange>(c, "SELECT json FROM pending WHERE user_id=$u AND anime_id=$a", ("$u", user), ("$a", after.ShikimoriId)).FirstOrDefault();
        var change = new PendingChange(user, after.ShikimoriId, Guid.NewGuid().ToString("N"), after.EpisodesWatched, after.UserStatus, old?.BaseEpisodes ?? before.EpisodesWatched, old?.BaseStatus ?? before.UserStatus, old?.ExistedRemotely ?? before.RateId.HasValue, (old?.EpisodesChanged ?? false) || before.EpisodesWatched != after.EpisodesWatched, (old?.StatusChanged ?? false) || before.UserStatus != after.UserStatus, before.Score != after.Score ? after.Score : old?.Score, old?.BaseScore ?? before.Score);
        DatabaseService.Run(c, "INSERT OR REPLACE INTO anime VALUES($u,$a,$j)", ("$u", user), ("$a", after.ShikimoriId), ("$j", JsonSerializer.Serialize(after)));
        DatabaseService.Run(c, "INSERT OR REPLACE INTO pending VALUES($u,$a,$r,$j)", ("$u", user), ("$a", after.ShikimoriId), ("$r", change.Revision), ("$j", JsonSerializer.Serialize(change)));
    }

    public Task DeleteMapping(long anime, string provider, CancellationToken ct = default) => db.Execute(c =>
    {
        DatabaseService.Run(c, "DELETE FROM mappings WHERE anime_id=$a AND provider=$p", ("$a", anime), ("$p", provider));
        return true;
    }, ct);
    public Task<bool> PersistPlayback(long user, WatchEntry entry, bool mark, CancellationToken ct = default) => db.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        var old = DatabaseService.Read<WatchEntry>(c, "SELECT json FROM history WHERE user_id=$u AND anime_id=$a AND provider=$p AND episode=$e", ("$u", user), ("$a", entry.AnimeId), ("$p", entry.ProviderId), ("$e", entry.EpisodeId)).FirstOrDefault();
        if (old?.ReleaseId != entry.ReleaseId)
            old = null;
        entry = entry with
        {
            Completed = mark || old?.Completed == true,
            WatchedRanges = entry.WatchedRanges ?? old?.WatchedRanges,
            Duration = entry.Duration > 0 ? entry.Duration : old?.Duration ?? 0
        };
        DatabaseService.Run(c, "INSERT OR REPLACE INTO history VALUES($u,$a,$p,$e,$j)", ("$u", user), ("$a", entry.AnimeId), ("$p", entry.ProviderId), ("$e", entry.EpisodeId), ("$j", JsonSerializer.Serialize(entry)));
        var anime = DatabaseService.Read<Anime>(c, "SELECT json FROM anime WHERE user_id=$u AND id=$a", ("$u", user), ("$a", entry.AnimeId)).FirstOrDefault();
        var changed = mark && anime is not null && entry.Episode > anime.EpisodesWatched && (anime.EpisodesTotal <= 0 || entry.Episode <= anime.EpisodesTotal);
        if (changed)
            WriteChange(c, user, anime!, anime!with { EpisodesWatched = entry.Episode, UserStatus = anime!.Status == "released" && anime.EpisodesTotal > 0 && entry.Episode == anime.EpisodesTotal ? "completed" : anime.UserStatus is "planned" or "on_hold" or "dropped" ? "watching" : anime.UserStatus });
        tx.Commit();
        return changed;
    }, ct);
}
