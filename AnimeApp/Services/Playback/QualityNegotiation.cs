using System.Text.RegularExpressions;

namespace AnimeApp.Services.Playback;
public static class QualityNegotiation
{
    public static MediaVariant? Select(IReadOnlyList<MediaVariant>? variants, string preference = "Auto", int? maximumHeight = null)
    {
        if (variants is null || variants.Count == 0)
            return null;
        var ordered = variants.Where(v => maximumHeight is null || v.Height <= maximumHeight).OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToArray();
        if (ordered.Length == 0)
            throw new InvalidOperationException("Нет потока в пределах выбранного ограничения качества.");
        return ordered.FirstOrDefault(v => v.Quality == preference) ?? ordered.First();
    }

    public static IReadOnlyList<MediaVariant> ParseMaster(string manifest, Uri origin)
    {
        var result = new List<MediaVariant>();
        var lines = manifest.Split('\n').Select(l => l.Trim()).ToArray();
        for (int i = 0; i < lines.Length - 1; i++)
            if (lines[i].StartsWith("#EXT-X-STREAM-INF:"))
            {
                var resolution = Regex.Match(lines[i], @"RESOLUTION=(\d+)x(\d+)");
                var bw = Regex.Match(lines[i], @"(?:^|[:,])BANDWIDTH=(\d+)");
                var next = lines.Skip(i + 1).FirstOrDefault(l => l != "" && !l.StartsWith('#'));
                if (resolution.Success && Uri.TryCreate(origin, next, out var uri) && uri.Scheme == "https")
                    result.Add(new(resolution.Groups[2].Value + "p", uri.ToString(), int.Parse(resolution.Groups[1].Value), bw.Success ? long.Parse(bw.Groups[1].Value) : null));
            }

        return result.DistinctBy(v => v.Location).OrderByDescending(v => v.Height).ToArray();
    }

    public static async Task<IReadOnlyList<MediaVariant>> ExpandHls(HttpClient http, IReadOnlyList<MediaVariant> variants, CancellationToken ct)
    {
        var result = new List<MediaVariant>();
        foreach (var v in variants)
        {
            if (Uri.TryCreate(v.Location, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var manifest = await http.GetStringAsync(uri, ct);
                // Leave masters with separate audio intact: the player must preserve rendition groups.
                var children = manifest.Contains("#EXT-X-MEDIA:") ? [] : ParseMaster(manifest, uri);
                if (children.Count > 0)
                {
                    result.AddRange(children);
                    continue;
                }
            }

            result.Add(v);
        }

        return result.DistinctBy(v => v.Location).OrderByDescending(v => v.Height).ToArray();
    }
}
