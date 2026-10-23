using AnimeApp.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeApp.Services.Providers;
public record ReleaseSuggestion(ProviderRelease Release, double Score, string Explanation)
{
    public string Confidence => Score >= 0.85 ? "высокое" : Score >= 0.6 ? "среднее" : "низкое";
    public string Label => $"{Release.Title} · Совпадение: {Confidence} · {Release.EpisodesAvailable?.ToString() ?? Release.EpisodesTotal?.ToString() ?? "?"} серий";
}

public sealed class ReleaseMatcher
{
    public static ReleaseSuggestion? Unambiguous(IReadOnlyList<ReleaseSuggestion> suggestions)
    {
        var best = suggestions.OrderByDescending(s => s.Score).FirstOrDefault();
        return best is { Score: >= 0.85 } && suggestions.All(s => s == best || s.Score < 0.6) ? best : null;
    }
    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant().Normalize(NormalizationForm.FormKC), @"[^\p{L}\p{Nd}]+", " ").Trim();
    public ReleaseSuggestion Rank(Anime anime, ProviderRelease release)
    {
        var names = new[]
        {
            anime.Name,
            anime.RussianName,
            anime.EnglishName,
            anime.JapaneseName
        }.Concat(anime.AlternativeNames).Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalize).Where(n => n.Length > 0);
        var titles = release.Title.Split('/').Select(Normalize).Where(n => n.Length > 0).ToArray();
        double score = 0;
        foreach (var name in names)
            foreach (var title in titles)
            {
                var a = name.Split(' ').ToHashSet();
                var b = title.Split(' ').ToHashSet();
                var similarity = name == title ? 1 : (double)a.Intersect(b).Count() / Math.Max(1, a.Union(b).Count());
                // A long shared title must not auto-link a different numbered season.
                var aNumbers = a.Where(t => t.All(char.IsDigit)).ToHashSet();
                var bNumbers = b.Where(t => t.All(char.IsDigit)).ToHashSet();
                if (!aNumbers.SetEquals(bNumbers)) similarity = Math.Min(similarity, 0.59);
                score = Math.Max(score, similarity);
            }

        var notes = new List<string>();
        if (anime.Year.HasValue && release.Year.HasValue)
        {
            if (anime.Year != release.Year)
            {
                score -= 0.25;
                notes.Add("год отличается");
            }
            else
                notes.Add("год совпадает");
        }

        if (!string.IsNullOrEmpty(release.Kind) && !string.IsNullOrEmpty(anime.Kind) && release.Kind != anime.Kind)
        {
            score -= 0.2;
            notes.Add("тип отличается");
        }

        if (release.EpisodesTotal > 0 && anime.EpisodesTotal > 0 && release.EpisodesTotal != anime.EpisodesTotal)
        {
            score -= 0.1;
            notes.Add("число серий отличается");
        }

        return new(release, Math.Clamp(score, 0, 1), string.Join(", ", notes));
    }

    public async Task<IReadOnlyList<ReleaseSuggestion>> Suggest(IAnimeProvider provider, Anime anime, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ProviderRelease>();
        foreach (var q in new[]
        {
            anime.RussianName,
            anime.Name,
            anime.EnglishName
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3))
        {
            foreach (var r in await provider.Search(q, ct))
                found[r.Id] = r;
            var exact = Unambiguous(found.Values.Select(r => Rank(anime, r)).ToArray());
            if (exact is { Score: >= 0.99, Release.Year: not null } && !string.IsNullOrEmpty(exact.Release.Kind))
                break;
        }

        var best = found.Values.Select(r => Rank(anime, r)).OrderByDescending(r => r.Score).Take(5).ToList();
        for (int i = 0; i < best.Count; i++)
        {
            if (best[i].Score < 0.6) continue;
            try
            {
                best[i] = Rank(anime, await provider.GetRelease(best[i].Release.Id, ct));
            }
            catch (ProviderException)
            {
                best[i] = best[i] with
                {
                    Explanation = "Метаданные недоступны; проверьте релиз перед привязкой",
                    Score = Math.Min(best[i].Score, 0.59)
                };
            }
        }

        return best.OrderByDescending(r => r.Score).ToList();
    }
}
