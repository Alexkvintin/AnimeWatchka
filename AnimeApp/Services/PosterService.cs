using System.Security.Cryptography;
using System.Text;

namespace AnimeApp.Services;
public interface IPosterLoader
{
    Task<byte[]> Load(Uri uri, CancellationToken ct);
}

public sealed class PosterService(HttpClient http, string directory) : IPosterLoader
{
    public static bool IsUsable(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && !uri.AbsolutePath.Contains("missing_", StringComparison.OrdinalIgnoreCase);
    public const long MaximumBytes = 400L * 1024 * 1024;
    public string DirectoryPath => directory;

    private readonly SemaphoreSlim cacheGate = new(1, 1);
    public async Task Trim(long maximumBytes = MaximumBytes, bool clear = false)
    {
        await cacheGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(directory))
                    return;
                var files = new DirectoryInfo(directory).GetFiles("*.image").OrderBy(f => f.LastWriteTimeUtc).ToList();
                long size = files.Sum(f => f.Length);
                foreach (var file in files)
                {
                    if (!clear && size <= maximumBytes)
                        break;
                    try
                    {
                        var length = file.Length;
                        file.Delete();
                        size -= length;
                    }
                    catch (IOException)
                    {
                        System.Diagnostics.Trace.TraceWarning("Poster cache file is busy");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.TraceWarning("Poster cache file is inaccessible");
                    }
                }
            });
        }
        finally
        {
            cacheGate.Release();
        }
    }

    public static IPosterLoader Shared { get; } = new PosterService(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeApp", "posters"));

    public async Task<byte[]> Load(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != "https")
            throw new InvalidOperationException("Poster URL must use HTTPS.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".image");
        if (File.Exists(path))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(path, ct);
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                return cached;
            }
            catch (FileNotFoundException)
            { /* A concurrent eviction requires a normal fetch. */
            }
        }

        var bytes = await http.GetByteArrayAsync(uri, ct);
        var temporary = path + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        await Trim();
        return bytes;
    }
}
