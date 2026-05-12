using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WebBrowserApp
{
    internal sealed class ZoneOrderManager
    {
        private const int MinZone = 1;
        private const int MaxZone = 46;
        private const string FileName = "zone_jump_order.txt";
        private const string FavoritesFileName = "zone_jump_favorites.txt";
        private const string DefaultOrderText = "18-28,46,1,29-38,2-17,39-45";

        private readonly string _filePath;
        private readonly string _favoritesFilePath;

        internal ZoneOrderManager(string baseDirectory)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            _filePath = Path.Combine(root, FileName);
            _favoritesFilePath = Path.Combine(root, FavoritesFileName);
        }

        internal string FilePath => _filePath;

        internal string FavoritesFilePath => _favoritesFilePath;

        internal IReadOnlyList<int> LoadZoneOrder()
        {
            EnsureInitialized();

            string rawText = File.ReadAllText(_filePath, Encoding.UTF8);
            List<int> parsed = ParseOrderText(rawText);
            if (parsed.Count > 0)
            {
                return parsed;
            }

            File.WriteAllText(_filePath, BuildDefaultFileContent(), new UTF8Encoding(false));
            return ParseOrderText(DefaultOrderText);
        }

        internal IReadOnlyList<int> BuildDisplayOrder()
        {
            IReadOnlyList<int> baseOrder = LoadZoneOrder();
            HashSet<int> favorites = LoadFavoriteZones();
            var ordered = new List<int>(baseOrder.Count);
            ordered.AddRange(baseOrder.Where(zone => favorites.Contains(zone)));
            ordered.AddRange(baseOrder.Where(zone => !favorites.Contains(zone)));
            return ordered;
        }

        internal HashSet<int> LoadFavoriteZones()
        {
            EnsureFavoritesInitialized();
            string rawText = File.ReadAllText(_favoritesFilePath, Encoding.UTF8);
            return new HashSet<int>(ParseExplicitZones(rawText));
        }

        internal HashSet<int> ToggleFavorite(int zone)
        {
            HashSet<int> favorites = LoadFavoriteZones();
            if (!favorites.Add(zone))
            {
                favorites.Remove(zone);
            }

            SaveFavoriteZones(favorites);
            return favorites;
        }

        internal void SaveFavoriteZones(IEnumerable<int> zones)
        {
            EnsureFavoritesInitialized();
            List<int> normalized = (zones ?? Enumerable.Empty<int>())
                .Where(zone => zone >= MinZone && zone <= MaxZone)
                .Distinct()
                .OrderBy(zone => zone)
                .ToList();
            File.WriteAllText(_favoritesFilePath, string.Join(",", normalized), new UTF8Encoding(false));
        }

        private void EnsureInitialized()
        {
            if (File.Exists(_filePath))
            {
                return;
            }

            File.WriteAllText(_filePath, BuildDefaultFileContent(), new UTF8Encoding(false));
        }

        private void EnsureFavoritesInitialized()
        {
            if (!File.Exists(_favoritesFilePath))
            {
                File.WriteAllText(_favoritesFilePath, string.Empty, new UTF8Encoding(false));
            }
        }

        private static string BuildDefaultFileContent()
        {
            return "# PVZOL 区服跳转顺序，支持单个数字或区间，用逗号分隔\r\n"
                + "# 例如：18-28,46,1,29-38,2-17,39-45\r\n"
                + DefaultOrderText + "\r\n";
        }

        private static List<int> ParseOrderText(string rawText)
        {
            var ordered = new List<int>();
            var seen = new HashSet<int>();
            foreach (int zone in ParseExplicitZones(rawText))
            {
                if (zone < MinZone || zone > MaxZone || !seen.Add(zone))
                {
                    continue;
                }

                ordered.Add(zone);
            }

            for (int zone = MinZone; zone <= MaxZone; zone += 1)
            {
                if (seen.Add(zone))
                {
                    ordered.Add(zone);
                }
            }

            return ordered;
        }

        private static List<int> ParseExplicitZones(string rawText)
        {
            var ordered = new List<int>();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return ordered;
            }

            string normalized = rawText.Replace("\r", "\n");
            string[] lines = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length == 0 || trimmedLine.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string token in trimmedLine.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    ordered.AddRange(ExpandToken(token));
                }
            }

            return ordered;
        }

        private static IEnumerable<int> ExpandToken(string token)
        {
            string trimmed = (token ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                yield break;
            }

            int dashIndex = trimmed.IndexOf('-');
            if (dashIndex > 0 && dashIndex < trimmed.Length - 1)
            {
                string left = trimmed.Substring(0, dashIndex).Trim();
                string right = trimmed.Substring(dashIndex + 1).Trim();
                if (int.TryParse(left, out int start) && int.TryParse(right, out int end))
                {
                    int step = start <= end ? 1 : -1;
                    for (int current = start; current != end + step; current += step)
                    {
                        yield return current;
                    }
                }

                yield break;
            }

            if (int.TryParse(trimmed, out int zoneNumber))
            {
                yield return zoneNumber;
            }
        }
    }
}
