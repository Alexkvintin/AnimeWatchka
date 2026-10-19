namespace AnimeApp.ViewModels;
public sealed partial class SettingsViewModel
{
    internal async Task RefreshIdentity()
    {
        var user = host.UserId;
        try
        {
            var identity = await api.CurrentUser();
            if (host.UserId != user || identity.Id != user) return;
            host.AccountAvatar = identity.Avatar;
            await repository.SaveSetting("account", identity);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        { /* Offline: keep the cached avatar; account and library remain usable. */ }
    }
}
