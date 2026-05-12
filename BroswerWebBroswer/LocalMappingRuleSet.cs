using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WebBrowserApp
{
    internal sealed class LocalMappingRuleSet
    {
        private readonly List<string> _hostEquals;
        private readonly List<string> _hostSuffixes;
        private readonly List<string> _hostContains;
        private readonly List<string> _urlKeywords;
        private readonly List<string> _nativeHostFragments;

        private LocalMappingRuleSet(
            string cacheRootPath,
            IEnumerable<string> hostEquals,
            IEnumerable<string> hostSuffixes,
            IEnumerable<string> hostContains,
            IEnumerable<string> urlKeywords)
        {
            CacheRootPath = string.IsNullOrWhiteSpace(cacheRootPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache")
                : cacheRootPath;

            _hostEquals = NormalizeDistinct(hostEquals);
            _hostSuffixes = NormalizeDistinct(hostSuffixes);
            _hostContains = NormalizeDistinct(hostContains);
            _urlKeywords = NormalizeDistinct(urlKeywords);
            _nativeHostFragments = NormalizeDistinct(_hostEquals
                .Concat(_hostContains)
                .Concat(_hostSuffixes.Select(TrimLeadingDot)));
        }

        internal string CacheRootPath { get; }

        internal IReadOnlyList<string> UrlKeywords => _urlKeywords;

        internal IReadOnlyList<string> NativeHostFragments => _nativeHostFragments;

        internal static LocalMappingRuleSet CreateDefault(string cacheRootPath)
        {
            return new LocalMappingRuleSet(
                cacheRootPath,
                new[]
                {
                    "pvzol.org"
                },
                new[]
                {
                    ".pvzol.org",
                    ".youkia.pvz.youkia.com"
                },
                new[]
                {
                    "youkia.pvz",
                    "pvz.youkia",
                    "youkia.com"
                },
                new[]
                {
                    "/pvz/",
                    "/youkia/",
                    "youkia.pvz",
                    "pvz.youkia",
                    ".youkia.com",
                    ".youkia.pvz.youkia.com"
                });
        }

        internal bool Matches(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            string host = Normalize(uri.Host);
            string absoluteUrl = Normalize(uri.AbsoluteUri);
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(absoluteUrl))
            {
                return false;
            }

            if (_hostEquals.Any(value => string.Equals(host, value, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (_hostSuffixes.Any(value => host.EndsWith(value, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (_hostContains.Any(value => host.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            return _urlKeywords.Any(value => absoluteUrl.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal string Describe()
        {
            return $"cacheRoot={CacheRootPath} exactHosts={_hostEquals.Count} hostSuffixes={_hostSuffixes.Count} hostContains={_hostContains.Count} urlKeywords={_urlKeywords.Count}";
        }

        private static List<string> NormalizeDistinct(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Normalize)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string TrimLeadingDot(string value)
        {
            string normalized = Normalize(value);
            while (normalized.StartsWith(".", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            return normalized;
        }
    }
}
