using System.Net.Http;
using System.IO;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Edith.Core;

namespace Edith.App;

/// <summary>
/// Fetches a small, text-only research context.  It deliberately never starts
/// a process or opens a window: a knowledge request must not steal focus.
/// </summary>
public sealed class BackgroundWebResearchService
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset SavedAt, BackgroundResearchResult Result)> PriceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PriceCacheTtl = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient _directHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<BackgroundResearchResult> ResearchAsync(string query, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (TryGetPriceModel(query, out string cachedProduct) &&
            PriceCache.TryGetValue(cachedProduct, out var cached) &&
            now - cached.SavedAt <= PriceCacheTtl)
        {
            WriteDiagnostic($"backend=price-cache{Environment.NewLine}age-ms={(now - cached.SavedAt).TotalMilliseconds:F0}{Environment.NewLine}" + cached.Result.FormatForBrain(cachedProduct));
            return cached.Result;
        }
        string freshQuery = IntentRouter.BuildFreshSearchQuery(query, now);
        if (string.IsNullOrWhiteSpace(freshQuery))
            return new BackgroundResearchResult("", []);

        try
        {
            IReadOnlyList<string> queries = BuildQueries(query, freshQuery, now);
            var hits = new List<SearchHit>(queries.Count);
            foreach (string targetedQuery in queries)
            {
                hits.Add(await FetchAsync(targetedQuery, cancellationToken));
                await Task.Delay(120, cancellationToken);
            }
            if (IsBestOverallGpu(query))
            {
                string[] trustedPages =
                [
                    "https://www.nvidia.com/en-us/geforce/graphics-cards/50-series/",
                    "https://www.tomshardware.com/reviews/gpu-hierarchy,4388.html",
                    "https://www.techpowerup.com/gpu-specs/geforce-rtx-5090.c4216"
                ];
                SearchHit[] direct = await Task.WhenAll(trustedPages.Select(x => FetchDirectAsync(x, cancellationToken)));
                hits.AddRange(direct);
            }
            else if (TryGetPriceModel(query, out string product))
            {
                string encoded = Uri.EscapeDataString(product);
                string[] retailerPages =
                [
                    "https://www.dns-shop.ru/search/?q=" + encoded,
                    "https://www.citilink.ru/search/?text=" + encoded,
                    "https://www.regard.ru/catalog?search=" + encoded,
                    "https://www.ozon.ru/search/?text=" + encoded,
                    "https://market.yandex.ru/search?text=" + encoded
                ];
                SearchHit[] direct = await Task.WhenAll(retailerPages.Select(x => FetchDirectAsync(x, cancellationToken, ResearchEvidenceType.CurrentPrice, 80)));
                hits.AddRange(direct);
            }
            IReadOnlyList<ResearchSource> evidence = hits.SelectMany(x => x.Evidence)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.Url) ? x.Title : x.Url, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).OrderByDescending(x => x.TrustScore).Take(12).ToList();
            var result = new BackgroundResearchResult(
                string.Join(" | ", queries),
                evidence.Select(x => x.ToString()).ToList(),
                evidence);
            if (TryGetPriceModel(query, out string pricedProduct) &&
                result.RankedEvidence.Any(x => x.EvidenceType == ResearchEvidenceType.CurrentPrice &&
                                               x.CanonicalProduct?.Equals(pricedProduct, StringComparison.OrdinalIgnoreCase) == true &&
                                               x.PriceText is not null))
            {
                PriceCache[pricedProduct] = (now, result);
            }
            WriteDiagnostic("backend=duckduckgo-html" + Environment.NewLine +
                            "sources=" + result.Sources.Count + Environment.NewLine +
                            "queries=" + queries.Count + Environment.NewLine +
                            result.FormatForBrain());
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            WriteDiagnostic("backend=duckduckgo-html" + Environment.NewLine +
                            "error=network-or-parser" + Environment.NewLine +
                            "query=" + freshQuery);
            return new BackgroundResearchResult(freshQuery, []);
        }
    }

    private async Task<SearchHit> FetchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            string url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            string html = await _http.GetStringAsync(url, cancellationToken);
            DateTimeOffset fetchedAt = DateTimeOffset.Now;
            return new SearchHit(query, BackgroundResearchExtractor.ExtractEvidence(html).Select(x => x with { FetchedAt = fetchedAt }).ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch { return new SearchHit(query, []); }
    }

    private async Task<SearchHit> FetchDirectAsync(string url, CancellationToken cancellationToken, ResearchEvidenceType? forcedType = null, int? forcedTrust = null)
    {
        try
        {
            string html = await _directHttp.GetStringAsync(url, cancellationToken);
            Uri uri = new(url);
            (int trust, ResearchEvidenceType type) = forcedType is not null
                ? (forcedTrust ?? 20, forcedType.Value)
                : uri.Host.Contains("nvidia.com", StringComparison.OrdinalIgnoreCase)
                ? (100, ResearchEvidenceType.ProductExistence)
                : (65, ResearchEvidenceType.PerformanceRanking);
            return new SearchHit(url, BackgroundResearchExtractor.ExtractDirectEvidence(html, url, trust, type));
        }
        catch (OperationCanceledException) { throw; }
        catch { return new SearchHit(url, []); }
    }

    private static IReadOnlyList<string> BuildQueries(string original, string freshQuery, DateTimeOffset now)
    {
        if (IsBestOverallGpu(original))
        {
            string month = now.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return [
                $"site:nvidia.com/geforce current flagship gaming GPU {now:yyyy}",
                $"site:amd.com current flagship gaming GPU {now:yyyy}",
                $"site:intel.com Arc flagship gaming GPU {now:yyyy}",
                $"best gaming GPU {month} benchmarks",
                $"fastest gaming GPU {month} independent review"
            ];
        }

        Match model = System.Text.RegularExpressions.Regex.Match(original, @"\b(?:NVIDIA\s+)?(?:GeForce\s+)?RTX\s+\d{4}(?:\s*Ti)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (model.Success && Regex.IsMatch(original, "цена|стоит|стоимость|купить", RegexOptions.IgnoreCase))
        {
            string product = model.Value;
            return [
                $"\"{product}\" цена купить Россия {now:yyyy-MM-dd}",
                $"\"{product}\" site:regard.ru",
                $"\"{product}\" site:dns-shop.ru",
                $"\"{product}\" site:citilink.ru",
                $"\"{product}\" site:ozon.ru",
                $"\"{product}\" site:market.yandex.ru"
            ];
        }

        return [freshQuery];
    }

    private static bool IsBestOverallGpu(string query)
    {
        string normalized = IntentRouter.Normalize(query);
        return normalized.Contains("лучш") && (normalized.Contains("видеокарт") || normalized.Contains("gpu"));
    }

    private static bool TryGetPriceModel(string query, out string product)
    {
        Match match = Regex.Match(query, @"\b(?:NVIDIA\s+)?(?:GeForce\s+)?RTX\s+\d{4}(?:\s*Ti)?\b", RegexOptions.IgnoreCase);
        product = match.Success ? match.Value : "";
        return match.Success && Regex.IsMatch(query, "цена|стоит|стоимость|купить", RegexOptions.IgnoreCase);
    }

    private static void WriteDiagnostic(string text)
    {
        try
        {
            Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
            using var writer = new StreamWriter(@"C:\EDITH\data\diagnostics\background-research.txt", false, new System.Text.UTF8Encoding(true));
            writer.WriteLine(DateTimeOffset.Now.ToString("O"));
            writer.WriteLine(text);
        }
        catch
        {
        }
    }

    private sealed record SearchHit(string Query, IReadOnlyList<ResearchSource> Evidence);
}
