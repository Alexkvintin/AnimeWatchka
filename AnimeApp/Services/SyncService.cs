using AnimeApp.Models;
using AnimeApp.Services.Database;
using AnimeApp.Services.Providers;

namespace AnimeApp.Services;
public sealed record SyncConflict(PendingChange Change, Anime Local, Anime? Remote)
{
    public string Title => Local.Title;
    public string LocalText => $"На компьютере: {Change.Episodes} серий · {StatusLabels.Get(Change.Status)} · оценка {Change.Score ?? Local.Score}/10";
    public string RemoteText => Remote is null ? "Shikimori: запись удалена" : $"Shikimori: {Remote.EpisodesWatched} серий · {StatusLabels.Get(Remote.UserStatus)} · оценка {Remote.Score}/10";
}

public sealed class SyncService(LibraryRepository repository, IUserLibraryProvider api)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public IReadOnlyList<SyncConflict> Conflicts { get; private set; } = [];
    public UserIdentity? Identity { get; private set; }

    private async Task CheckIdentity(long user, CancellationToken ct)
    {
        var identity = await api.CurrentUser(ct);
        if (user <= 0 || identity.Id != user)
            throw new InvalidOperationException("Аккаунт изменился. Подключите Shikimori заново; очередь не отправлена.");
        Identity = identity;
    }

    private static bool Matches(Anime? a, PendingChange p) => a is not null && (!p.EpisodesChanged || a.EpisodesWatched == p.Episodes) && (!p.StatusChanged || a.UserStatus == p.Status) && (p.Score is null || a.Score == p.Score);
    private static bool Changed(Anime? server, PendingChange p) => (server is null && p.ExistedRemotely) || (server is not null && !Matches(server, p with { Episodes = p.BaseEpisodes, Status = p.BaseStatus, Score = p.Score is null ? null : p.BaseScore }));
    public Task<string> Synchronize(long user, CancellationToken ct = default) => SynchronizeCore(user, true, ct);
    public Task<string> SendPending(long user, CancellationToken ct = default) => SynchronizeCore(user, false, ct);
    private async Task<string> SynchronizeCore(long user, bool refreshLibrary, CancellationToken ct)
    {
        if (user <= 0)
            return "Подключите Shikimori, чтобы синхронизировать библиотеку.";
        await gate.WaitAsync(ct);
        try
        {
            await CheckIdentity(user, ct);
            var unlinked = await repository.Unlinked(user, ct);
            var remote = refreshLibrary ? await api.Rates(user, ct) : new List<Anime>();
            var pending = await repository.Pending(user, ct);
            var local = await repository.Library(user, ct);
            var conflicts = new List<SyncConflict>();
            var unconfirmed = 0;
            var rejected = 0;
            var unidentified = unlinked.Count;
            foreach (var p in pending)
            {
                if (p.AnimeId <= 0) { unidentified++; continue; }
                var current = local.Single(x => x.ShikimoriId == p.AnimeId);
                if (!refreshLibrary && await api.ReadRate(user, current, ct) is Anime latest)
                    remote.Add(latest);
                var server = remote.FirstOrDefault(x => x.ShikimoriId == p.AnimeId);
                if (Matches(server, p))
                {
                    await repository.Ack(p, ct);
                    continue;
                }

                if (Changed(server, p))
                {
                    conflicts.Add(new(p, current, server));
                    continue;
                }

                try
                {
                    await api.UpdateFields(user, (server ?? current)with { RateId = server?.RateId }, p, ct);
                }
                catch (Shikimori.ShikimoriApiException ex) when (ex.Status == System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    rejected++;
                    continue;
                }
                // A successful PATCH response alone is not evidence that the list reflects the change.
                var confirmed = await api.ReadRate(user, server ?? current, ct);
                remote.RemoveAll(a => a.ShikimoriId == p.AnimeId);
                if (confirmed is not null)
                    remote.Add(confirmed);
                if (Matches(remote.FirstOrDefault(x => x.ShikimoriId == p.AnimeId), p))
                    await repository.Ack(p, ct);
                else
                    unconfirmed++;
            }

            Conflicts = conflicts;
            if (refreshLibrary)
            {
                await repository.ReplaceRemote(user, remote, ct);
                await repository.SaveSetting("library-sync:" + user, DateTimeOffset.UtcNow, ct);
            }
            else
                await repository.MergeRemote(user, remote, ct);
            if (rejected > 0)
                return $"Shikimori отклонил изменения для {rejected} тайтлов (422). Они сохранены; остальные изменения обработаны.";
            return conflicts.Count > 0 ? $"Конфликтов: {conflicts.Count}. Выберите решение для каждого тайтла в настройках." : unconfirmed > 0 ? $"Сервер пока не подтвердил изменения: {unconfirmed}. Они сохранены в очереди для повторной проверки." : unidentified > 0 ? $"Синхронизация завершена. На устройстве сохранено записей без связи с Shikimori: {unidentified}. Подробности — в настройках." : "Библиотека синхронизирована; изменения подтверждены Shikimori.";
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Resolve(long user, SyncConflict conflict, bool keepLocal, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await CheckIdentity(user, ct);
            var p = (await repository.Pending(user, ct)).FirstOrDefault(x => x.AnimeId == conflict.Change.AnimeId);
            var remote = await api.Rates(user, ct);
            var server = remote.FirstOrDefault(x => x.ShikimoriId == conflict.Change.AnimeId);
            if (p is null || p.UserId != user || p.Revision != conflict.Change.Revision || server?.RateId != conflict.Remote?.RateId || server?.EpisodesWatched != conflict.Remote?.EpisodesWatched || server?.UserStatus != conflict.Remote?.UserStatus || server?.Score != conflict.Remote?.Score)
                throw new InvalidOperationException("Данные изменились после показа конфликта. Синхронизируйте библиотеку и выберите решение заново.");
            if (keepLocal)
            {
                if (server is null)
                    throw new InvalidOperationException("Запись удалена на Shikimori. Примите удаление, затем при необходимости добавьте тайтл заново.");
                await api.UpdateFields(user, server, p, ct);
                var confirmed = await api.ReadRate(user, server, ct);
                remote.RemoveAll(a => a.ShikimoriId == p.AnimeId);
                if (confirmed is not null)
                    remote.Add(confirmed);
                if (!Matches(remote.FirstOrDefault(x => x.ShikimoriId == p.AnimeId), p))
                    throw new IOException("Shikimori пока не подтвердил изменение. Локальная очередь сохранена.");
            }

            await repository.Ack(p, ct);
            await repository.ReplaceRemote(user, remote, ct);
            Conflicts = Conflicts.Where(x => x.Change.AnimeId != p.AnimeId).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    // Compatibility for explicit callers; normal UI resolves one inspected conflict at a time.
    public async Task AcceptRemote(long user, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await CheckIdentity(user, ct);
            var pending = await repository.Pending(user, ct);
            var remote = await api.Rates(user, ct);
            foreach (var p in pending)
                await repository.Ack(p, ct);
            await repository.ReplaceRemote(user, remote, ct);
            Conflicts = [];
        }
        finally
        {
            gate.Release();
        }
    }
}
