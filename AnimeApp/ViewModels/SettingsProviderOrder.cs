using AnimeApp.Models;
namespace AnimeApp.ViewModels;
public sealed partial class SettingsViewModel
{
    private readonly SemaphoreSlim providerSave = new(1, 1);
    public async Task MoveProvider(ProviderViewModel provider, int index)
    {
        var old = Providers.IndexOf(provider);
        if (old < 0 || index < 0 || index >= Providers.Count) return;
        Providers.Move(old, index);
        await PersistProviders();
    }
    public async Task PersistProviders()
    {
        await providerSave.WaitAsync();
        try
        {
            for (var i = 0; i < Providers.Count; i++)
            {
                var p = Providers[i]; p.Priority = i;
                await repository.SaveSetting("provider:" + p.Id, new ProviderSettings(p.Id, p.Enabled, i));
            }
            host.Sources.PreferredProviderId = "";
            PreferredProvider = null;
            await repository.SaveSetting("preferred-provider", "");
            host.Sources.RefreshProviders();
        }
        catch (Exception ex) { host.ReportError(ex); }
        finally { providerSave.Release(); }
    }
}
