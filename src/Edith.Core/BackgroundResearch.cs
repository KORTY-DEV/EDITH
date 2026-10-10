using System.Net;
using System.Text.RegularExpressions;

namespace Edith.Core;

public enum ResearchEvidenceType
{
    Unknown,
    ProductExistence,
    Specification,
    PerformanceRanking,
    CurrentPrice,
    Availability
}

public sealed record ResearchSource(string Title, string Domain, string Snippet, string Url, int TrustScore)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Snippet) ? $"{Title} ({Domain})" : $"{Title} ({Domain}): {Snippet}";
    public DateTimeOffset? FreshnessDate => Regex.Match(Snippet + " " + Title, @"\b20\d{2}-\d{2}-\d{2}\b") is Match m && m.Success && DateTimeOffset.TryParse(m.Value, out DateTimeOffset date) ? date : null;
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset? EffectiveDate => FreshnessDate ?? FetchedAt;
    public string? PriceText => Regex.Match(Snippet + " " + Title, @"(?:(?<!\d)(?:\d{1,3}(?:[\s\u00A0]\d{3})+|\d{4,})\s*(?:₽|руб(?:\.|лей|ля)?\b)|""price""\s*:\s*\d+)", RegexOptions.IgnoreCase) is Match p && p.Success ? (p.Value.Contains(":", StringComparison.Ordinal) ? Regex.Match(p.Value, @"\d+").Value + " RUB" : Regex.Replace(p.Value, @"\s+", " ").Trim()) : null;
    public string Currency => PriceText is null ? "" : "RUB";
    public string Availability => Regex.IsMatch(Snippet + " " + Title, "в наличии|есть в продаже|InStock|in stock", RegexOptions.IgnoreCase) ? "available" : "unknown";
    public ResearchEvidenceType EvidenceType { get; init; }
    public string? CanonicalProduct
    {
        get
        {
            Match match = Regex.Match(Title + " " + Snippet, @"\b(?:NVIDIA\s+)?(?:GeForce\s+)?RTX\s+(?:5090|5080|5070\s*Ti|5070|5060(?:\s*Ti)?)\b", RegexOptions.IgnoreCase);
            return match.Success ? "NVIDIA GeForce RTX " + Regex.Replace(Regex.Replace(match.Value, @".*RTX\s+", "", RegexOptions.IgnoreCase), @"\s+", " ").ToUpperInvariant() : null;
        }
    }
    public bool IsAuthoritativeProductSource => TrustScore >= 65 && (EvidenceType is ResearchEvidenceType.ProductExistence or ResearchEvidenceType.Specification or ResearchEvidenceType.PerformanceRanking);
}

public sealed record BackgroundResearchResult(string Query, IReadOnlyList<string> Sources, IReadOnlyList<ResearchSource>? Evidence = null)
{
    public IReadOnlyList<ResearchSource> RankedEvidence =>
        (Evidence ?? Sources.Select(x => new ResearchSource(x, "unknown", x, "", 0)).ToList())
        .OrderByDescending(x => x.TrustScore).ThenBy(x => x.Domain).Take(8).ToList();

    public string FormatForBrain(string? canonicalProduct = null) =>
        Sources.Count == 0
            ? $"Фоновый поиск по запросу «{Query}» не вернул проверяемых источников. Не выдумывай текущие факты и цены."
            : $"Фоновый поиск, запрос: {Query}{Environment.NewLine}" +
              (Regex.IsMatch(Query, "цена|стоит|стоимость", RegexOptions.IgnoreCase) ? "Интент: current price; используй только canonical product из запроса и evidence, не смешивай модели." : "Интент: best overall consumer/gaming, если пользователь сказал «лучшая» без бюджета. Не подменяй флагман самой дешёвой найденной картой.") + Environment.NewLine +
              $"Runtime date: {DateTimeOffset.Now:yyyy-MM-dd}; product existence requires authoritative evidence; unconfirmed products must not be named.{Environment.NewLine}" +
              string.Join(Environment.NewLine, RankedEvidence.Where(x => canonicalProduct is null || x.CanonicalProduct is null || x.CanonicalProduct.Equals(canonicalProduct, StringComparison.OrdinalIgnoreCase)).Select((x, i) => $"[{i + 1}] canonical={x.CanonicalProduct ?? "unresolved"}; type={x.EvidenceType}; title={x.Title}; domain={x.Domain}; trust={x.TrustScore}; price={x.PriceText ?? "unknown"}; currency={x.Currency}; availability={x.Availability}; date={x.EffectiveDate:yyyy-MM-dd}; url={x.Url}; fact={x.Snippet}"));

    // Safe degraded answer: it reports an actual source rather than asking a
    // local model to invent current facts when its lease is unavailable.
    public string SourceBackedReply() =>
        Sources.Count == 0
            ? "Актуальные данные сейчас получить не удалось."
            : Evidence is null
                ? "По свежим источникам: " + Sources[0]
            : RankedEvidence.Count < 2
                ? "Не удалось надёжно подтвердить актуальные данные несколькими источниками."
                : "По свежим источникам удалось подтвердить данные несколькими независимыми источниками, но точная цена зависит от модели и магазина.";

    public string? SinglePriceOfferReply(string? canonicalProduct = null)
    {
        if (!Regex.IsMatch(Query, "цена|стоит|стоимость|купить", RegexOptions.IgnoreCase)) return null;
        List<ResearchSource> offers = RankedEvidence
            .Where(x => x.EvidenceType == ResearchEvidenceType.CurrentPrice && x.PriceText is not null)
            .Where(x => canonicalProduct is null || x.CanonicalProduct?.Equals(canonicalProduct, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (offers.Count != 1) return null;

        ResearchSource offer = offers[0];
        Match digits = Regex.Match(offer.PriceText!, @"\d[\d\s]*");
        if (!digits.Success || !long.TryParse(Regex.Replace(digits.Value, @"\s", ""), out long amount)) return null;
        string store = offer.Domain switch
        {
            "regard.ru" => "Регард",
            "dns-shop.ru" => "DNS",
            "citilink.ru" => "Ситилинк",
            "ozon.ru" => "Ozon",
            "market.yandex.ru" => "Яндекс Маркет",
            _ => offer.Domain
        };
        string formatted = amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');
        return $"Нашла актуальное предложение за {formatted} рублей в магазине {store}.";
    }

    public string? PriceRangeReply(string? canonicalProduct = null)
    {
        if (!Regex.IsMatch(Query, "цена|стоит|стоимость|купить", RegexOptions.IgnoreCase)) return null;
        var amounts = RankedEvidence
            .Where(x => x.EvidenceType == ResearchEvidenceType.CurrentPrice && x.PriceText is not null)
            .Where(x => canonicalProduct is null || x.CanonicalProduct?.Equals(canonicalProduct, StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => Regex.Match(x.PriceText!, @"\d[\d\s]*"))
            .Where(x => x.Success)
            .Select(x => long.TryParse(Regex.Replace(x.Value, @"\s", ""), out long value) ? value : 0)
            .Where(x => x > 0)
            .Distinct()
            .Order()
            .ToList();
        if (amounts.Count < 2) return null;
        string Format(long value) => value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');
        return $"Нашла несколько актуальных предложений: от {Format(amounts.First())} до {Format(amounts.Last())} рублей. Цена зависит от версии карты и магазина.";
    }
}

public static class BackgroundResearchExtractor
{
    public static IReadOnlyList<string> ExtractDuckDuckGo(string html, int limit = 4)
    {
        if (string.IsNullOrWhiteSpace(html) || limit < 1)
            return [];

        MatchCollection titles = Regex.Matches(html,
            @"result__a[^>]*>(?<value>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        MatchCollection snippets = Regex.Matches(html,
            @"result__snippet[^>]*>(?<value>.*?)</(?:a|td|div)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var results = new List<string>();
        for (int i = 0; i < titles.Count && results.Count < limit; i++)
        {
            string title = Clean(titles[i].Groups["value"].Value);
            string snippet = i < snippets.Count
                ? Clean(snippets[i].Groups["value"].Value)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(title))
                results.Add(string.IsNullOrWhiteSpace(snippet) ? title : title + ": " + snippet);
        }

        return results;
    }

    public static IReadOnlyList<ResearchSource> ExtractEvidence(string html, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(html) || limit < 1) return [];
        MatchCollection titles = Regex.Matches(html,
            @"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        MatchCollection snippets = Regex.Matches(html,
            @"result__snippet[^>]*>(?<value>.*?)</(?:a|td|div)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var result = new List<ResearchSource>();
        for (int i = 0; i < titles.Count && result.Count < limit; i++)
        {
            string title = Clean(titles[i].Groups["title"].Value);
            string rawUrl = WebUtility.HtmlDecode(titles[i].Groups["url"].Value);
            string url = rawUrl;
            int uddg = rawUrl.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
            if (uddg >= 0) url = Uri.UnescapeDataString(rawUrl[(uddg + 5)..].Split('&')[0]);
            string domain = "unknown";
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) domain = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            string snippet = i < snippets.Count ? Clean(snippets[i].Groups["value"].Value) : "";
            if (title.Length > 0)
            {
                int trust = Trust(domain);
                result.Add(new ResearchSource(title, domain, snippet, url, trust) { EvidenceType = Classify(domain, title + " " + snippet) });
            }
        }
        return result;
    }

    public static IReadOnlyList<ResearchSource> ExtractDirectEvidence(string html, string url, int trust, ResearchEvidenceType type, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return [];
        string text = WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        string domain = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        MatchCollection models = Regex.Matches(text, @"\b(?:NVIDIA\s+)?(?:GeForce\s+)?RTX\s+(?:5090|5080|5070\s*Ti|5070|5060(?:\s*Ti)?)\b", RegexOptions.IgnoreCase);
        if (type == ResearchEvidenceType.CurrentPrice)
        {
            MatchCollection offers = Regex.Matches(text, @"(?:name|title)""?\s*:\s*""?(?<name>[^""}]*RTX\s+\d{4}[^""}]*)[^\n]{0,1500}?(?:price""?\s*:\s*|price[=:])(?<price>\d{4,})[^}]{0,150}", RegexOptions.IgnoreCase);
            var parsed = offers.Cast<Match>().Take(limit).Select(x => new ResearchSource(x.Groups["name"].Value.Trim(), domain, x.Value, url, trust) { EvidenceType = type, FetchedAt = DateTimeOffset.Now }).ToList();
            if (parsed.Count > 0) return parsed;
        }
        return models.Cast<Match>().Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit)
            .Select(model =>
            {
                int start = Math.Max(0, text.IndexOf(model, StringComparison.OrdinalIgnoreCase) - 120);
                string snippet = text.Substring(start, Math.Min(900, text.Length - start));
                return new ResearchSource($"{type}: {model}", domain, snippet, url, trust) { EvidenceType = type, FetchedAt = DateTimeOffset.Now };
            }).ToList();
    }

    private static int Trust(string domain) =>
        domain switch
        {
            "nvidia.com" or "amd.com" or "intel.com" => 100,
            _ when domain.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".amd.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".intel.com", StringComparison.OrdinalIgnoreCase) => 100,
            "dns-shop.ru" or "citilink.ru" or "regard.ru" or "ozon.ru" or "market.yandex.ru" => 80,
            _ when domain.Contains("tomshardware") || domain.Contains("techpowerup") || domain.Contains("computerbase") => 65,
            _ when domain.Contains("hardwareunboxed") || domain.Contains("anandtech") || domain.Contains("videocardz") => 60,
            _ => 20
        };

    private static ResearchEvidenceType Classify(string domain, string text) =>
        domain switch
        {
            "nvidia.com" or "amd.com" or "intel.com" => ResearchEvidenceType.ProductExistence,
            _ when domain.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".amd.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".intel.com", StringComparison.OrdinalIgnoreCase) => ResearchEvidenceType.ProductExistence,
            "dns-shop.ru" or "citilink.ru" or "regard.ru" or "ozon.ru" or "market.yandex.ru" => ResearchEvidenceType.CurrentPrice,
            _ when domain.Contains("tomshardware") || domain.Contains("techpowerup") || domain.Contains("computerbase") || domain.Contains("hardwareunboxed") || domain.Contains("anandtech") || domain.Contains("videocardz") => ResearchEvidenceType.PerformanceRanking,
            _ => ResearchEvidenceType.Unknown
        };

    private static string Clean(string value)
    {
        value = WebUtility.HtmlDecode(Regex.Replace(value, "<.*?>", " "));
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length <= 700 ? value : value[..700];
    }
}
