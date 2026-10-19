using AnimeApp.Models;
using AnimeApp.Services;
using AnimeApp.Services.Database;
using AnimeApp.Services.Shikimori;
using Xunit;

namespace AnimeApp.Tests;
public sealed class Stage3Tests
{
    private sealed class Store : ICredentialStore
    {
        public OAuthCredentials? Value;
        public Task<OAuthCredentials?> Read(CancellationToken ct = default) => Task.FromResult(Value);
        public Task Write(OAuthCredentials c, CancellationToken ct = default)
        {
            Value = c;
            return Task.CompletedTask;
        }

        public Task Clear(CancellationToken ct = default)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LegacySettingsForBundledClientUseBrowserCallbackAndBroker()
    {
        var old = new OAuthCredentials("bundled-id", "legacy-secret");
        var store = new Store { Value = old };
        var bundled = new OAuthOptions("bundled-id", RedirectUri: "http://127.0.0.1:17842/callback/", TokenBrokerUrl: "https://auth.example/token");
        var manager = new CredentialManager(store, () => bundled);
        await manager.Reload();
        Assert.Same(bundled, manager.Current);
        Assert.Same(old, store.Value);
        var service = new ShikimoriService(new HttpClient(), new(), null!, manager);
        var url = service.AuthorizationUrl("test-state");
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A17842%2Fcallback%2F", url);
        Assert.Contains("state=test-state", url);
        Assert.DoesNotContain("oob", url);
        Assert.Empty(manager.Current.ClientSecret);
    }

    [Fact]
    public async Task BundledBrokerDoesNotOverrideDifferentDeveloperClient()
    {
        var store = new Store { Value = new("custom-id", "custom-secret", "https://custom.example/callback") };
        var manager = new CredentialManager(store, () => new("bundled-id", RedirectUri: "http://127.0.0.1:17842/callback/", TokenBrokerUrl: "https://auth.example/token"));
        await manager.Reload();
        Assert.Equal("custom-id", manager.Current.ClientId);
        Assert.Equal("https://custom.example/callback", manager.Current.RedirectUri);
        Assert.False(manager.Current.IsBrokerConfigured);
    }

    [Fact]
    public async Task CredentialsSwitchAtomicallyWithoutRestart()
    {
        var store = new Store();
        var manager = new CredentialManager(store, () => new("env-id", "env-secret", "https://callback.test"));
        await manager.Reload();
        Assert.Equal("env-id", manager.Current.ClientId);
        await manager.Save(new("saved-id", "saved-secret"));
        Assert.Equal("saved-id", manager.Current.ClientId);
        Assert.Equal("urn:ietf:wg:oauth:2.0:oob", manager.Current.RedirectUri);
        await manager.Delete();
        Assert.Equal("env-id", manager.Current.ClientId);
    }

    [Fact]
    public async Task MissingEnvironmentPairDoesNotBecomeConfigured()
    {
        var manager = new CredentialManager(new Store(), () => new("only-id", ""));
        await manager.Reload();
        Assert.False(manager.Current.Configured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.Save(new("", "secret")));
    }

    [Fact]
    public async Task ProtectedCredentialsSurviveRestartAndHideSecret()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var p = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "credentials");
        var store = new WindowsCredentialStore(p);
        await store.Write(new("test-id", "test-secret-never-plaintext"));
        Assert.DoesNotContain("test-secret-never-plaintext", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(p)));
        Assert.Equal("test-id", (await new WindowsCredentialStore(p).Read())!.ClientId);
        await store.Clear();
        Assert.Null(await store.Read());
    }

    [Theory]
    [InlineData("invalid_client", "authorization_code", "Client ID")]
    [InlineData("invalid_grant", "authorization_code", "истёк")]
    [InlineData("invalid_grant", "refresh_token", "отозван")]
    [InlineData("access_denied", "authorization_code", "отказали")]
    public void OAuthFailuresAreSpecificAndDoNotEchoResponse(string error, string grant, string expected)
    {
        var text = ShikimoriService.OAuthErrorMessage(error, "secret-payload", grant);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("secret-payload", text);
    }

    private static Anime Title(long id = 1, int episodes = 2) => new()
    {
        ShikimoriId = id,
        Name = "Title",
        RateId = id + 100,
        UserStatus = "watching",
        EpisodesWatched = episodes,
        EpisodesTotal = 12
    };
    private static async Task<LibraryRepository> Repository()
    {
        var db = new DatabaseService(Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString(), "db"));
        await db.Initialize();
        return new(db);
    }

    private sealed class Api(params Anime[] values) : IShikimoriService
    {
        public List<Anime> Values = values.ToList();
        public int Writes;
        public bool IgnoreWrites;
        public bool FailReads;
        public Task<UserIdentity> CurrentUser(CancellationToken ct = default) => Task.FromResult(new UserIdentity(7, "test"));
        public Task<List<Anime>> Rates(long user, CancellationToken ct = default) => FailReads ? throw new IOException("offline") : Task.FromResult(Values.ToList());
        public Task Update(long user, Anime a, CancellationToken ct = default)
        {
            Writes++;
            if (!IgnoreWrites)
                Values[Values.FindIndex(x => x.ShikimoriId == a.ShikimoriId)] = a;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SuccessfulPatchWithoutReadbackKeepsPending()
    {
        var r = await Repository();
        var a = Title();
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        var api = new Api(a)
        {
            IgnoreWrites = true
        };
        await new SyncService(r, api).Synchronize(7);
        Assert.Single(await r.Pending(7));
        Assert.Equal(3, Assert.Single(await r.Library(7)).EpisodesWatched);
    }

    [Fact]
    public async Task ConflictDoesNotBlockAnotherTitleAndResolutionIsScoped()
    {
        var r = await Repository();
        var a = Title();
        var b = Title(2);
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        await r.Change(7, b, b with { EpisodesWatched = 3 });
        var api = new Api(a with { EpisodesWatched = 5 }, b);
        var sync = new SyncService(r, api);
        await sync.Synchronize(7);
        Assert.Equal(1, api.Writes);
        Assert.Single(await r.Pending(7));
        var conflict = Assert.Single(sync.Conflicts);
        await sync.Resolve(7, conflict, false);
        Assert.Empty(await r.Pending(7));
        Assert.Equal(5, (await r.Library(7)).Single(x => x.ShikimoriId == 1).EpisodesWatched);
        Assert.Equal(3, (await r.Library(7)).Single(x => x.ShikimoriId == 2).EpisodesWatched);
    }

    [Fact]
    public async Task KeepLocalRechecksRemoteAndRejectsStaleDecision()
    {
        var r = await Repository();
        var a = Title();
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        var api = new Api(a with { EpisodesWatched = 5 });
        var sync = new SyncService(r, api);
        await sync.Synchronize(7);
        var conflict = Assert.Single(sync.Conflicts);
        api.Values[0] = a with
        {
            EpisodesWatched = 6
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.Resolve(7, conflict, true));
        Assert.Equal(0, api.Writes);
        await sync.Synchronize(7);
        await sync.Resolve(7, Assert.Single(sync.Conflicts), true);
        Assert.Equal(3, api.Values[0].EpisodesWatched);
        Assert.Empty(await r.Pending(7));
    }

    [Fact]
    public async Task OfflineQueueRetriesOnceAndDoesNotIncrementTwice()
    {
        var r = await Repository();
        var a = Title();
        await r.Change(7, a, a with { EpisodesWatched = 3 });
        var api = new Api(a)
        {
            FailReads = true
        };
        var sync = new SyncService(r, api);
        await Assert.ThrowsAsync<IOException>(() => sync.Synchronize(7));
        Assert.Single(await r.Pending(7));
        api.FailReads = false;
        await sync.Synchronize(7);
        await sync.Synchronize(7);
        Assert.Equal(1, api.Writes);
        Assert.Empty(await r.Pending(7));
    }

    [Fact]
    public async Task CacheEvictsOldestAndClearLeavesOtherFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnimeAppTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "old.image"), new byte[20]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "old.image"), DateTime.UtcNow.AddDays(-1));
        await File.WriteAllBytesAsync(Path.Combine(dir, "new.image"), new byte[20]);
        await File.WriteAllTextAsync(Path.Combine(dir, "unrelated"), "keep");
        using var http = new HttpClient();
        var service = new PosterService(http, dir);
        await service.Trim(20);
        Assert.False(File.Exists(Path.Combine(dir, "old.image")));
        Assert.True(File.Exists(Path.Combine(dir, "new.image")));
        await service.Trim(clear: true);
        Assert.True(File.Exists(Path.Combine(dir, "unrelated")));
        Assert.Empty(Directory.GetFiles(dir, "*.image"));
    }

    [Theory]
    [InlineData("watching", "Смотрю")]
    [InlineData("on_hold", "Отложено")]
    [InlineData("released", "Вышло")]
    public void StatusIsRussian(string code, string label) => Assert.Equal(label, StatusLabels.Get(code));
}
