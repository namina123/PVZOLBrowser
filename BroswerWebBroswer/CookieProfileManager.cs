using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WebBrowserApp
{
    internal sealed class CookieProfileManager
    {
        private const string TargetPath = "/pvz/index.php/default/main";
        private const int MaxImportBytes = 30 * 1024 * 1024;
        private const string CookieKeyPhpSessionId = "PHPSESSID";
        private const string CookieKeyPvzol = "pvzol";
        private const string CookieKeyPvzYoukiaNew1 = "pvz_youkia_new1";
        private const string LegacyYoukiaHost = "www.youkia.com";
        private const string LegacyYoukiaPrefix = "/pvz/";
        private const string LegacyYoukiaIndexPrefix = "/index.php/pvz/";
        private static readonly string[] SaveUrlKeywords =
        {
            "pvzol",
            "youkia.pvz",
            "pvz.youkia",
            "youkua.pvz",
            "pvz.youkua"
        };
        private static readonly Regex UserSettingBlockRegex =
            new Regex("(?is)<UserSetting\\b[^>]*>.*?</UserSetting>", RegexOptions.Compiled);

        private readonly string _baseDirectory;
        private readonly string _cookieDirectory;
        private readonly string _seedDirectory;

        internal CookieProfileManager(string baseDirectory)
        {
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            _cookieDirectory = Path.Combine(_baseDirectory, "cookies");
            _seedDirectory = Path.Combine(_baseDirectory, "assets", "cookies");
        }

        internal string CookieDirectory => _cookieDirectory;

        internal void EnsureInitialized()
        {
            Directory.CreateDirectory(_cookieDirectory);
            if (!Directory.Exists(_seedDirectory))
            {
                return;
            }

            foreach (string seedFile in Directory.GetFiles(_seedDirectory, "*.xml"))
            {
                string targetPath = Path.Combine(_cookieDirectory, Path.GetFileName(seedFile));
                if (File.Exists(targetPath))
                {
                    continue;
                }

                File.Copy(seedFile, targetPath, false);
            }
        }

        internal List<string> LoadProfileFiles()
        {
            EnsureInitialized();
            if (!Directory.Exists(_cookieDirectory))
            {
                return new List<string>();
            }

            return Directory.GetFiles(_cookieDirectory, "*.xml")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal CookieProfile LoadProfile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            string rawText = File.ReadAllText(filePath, Encoding.UTF8);
            return ParseProfileText(rawText, filePath);
        }

        internal ImportResult ImportProfileFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return ImportResult.FromFailure("文件不存在。");
            }

            var sourceFile = new FileInfo(filePath);
            if (!sourceFile.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return ImportResult.FromFailure("仅支持导入 XML Cookie 文件。");
            }

            if (sourceFile.Length <= 0)
            {
                return ImportResult.FromFailure("XML 文件内容为空。");
            }

            if (sourceFile.Length > MaxImportBytes)
            {
                return ImportResult.FromFailure("XML 文件过大。");
            }

            string rawText = File.ReadAllText(filePath, Encoding.UTF8);
            return ImportProfileText(rawText, Path.GetFileNameWithoutExtension(sourceFile.Name), preserveRawXml: false);
        }

        internal ImportResult ImportProfileText(string rawText, string fileNameHint, bool preserveRawXml)
        {
            CookieProfile profile = ParseProfileText(rawText, fileNameHint);
            if (profile == null)
            {
                return ImportResult.FromFailure("XML 缺少必须的 UserDomain 或 UserCookies。");
            }

            FileInfo importedFile = preserveRawXml
                ? SaveRawImportedProfile(rawText, fileNameHint)
                : SaveImportedProfile(profile, fileNameHint);
            if (importedFile == null || !importedFile.Exists)
            {
                return ImportResult.FromFailure("导入 Cookie 文件失败。");
            }

            CookieProfile importedProfile = LoadProfile(importedFile.FullName) ?? profile;
            return ImportResult.FromSuccess(importedFile, importedProfile);
        }

        internal FileInfo SaveProfileFromPage(Uri pageUri, string cookies, string userNameHint)
        {
            SaveCookieMatch match = MatchSavableCookies(pageUri, cookies);
            if (match == null)
            {
                return null;
            }

            EnsureInitialized();

            string userName = SanitizeProfileName(userNameHint);
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = SanitizeProfileName(match.SourceUri?.Host);
            }
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = BuildDefaultProfileName();
            }

            string fileName = BuildUniqueFileName(userName);
            string outputPath = Path.Combine(_cookieDirectory, fileName);
            string xml = BuildProfileXml(1, match.UserDomain, match.PersistedCookies, userName, 1);
            File.WriteAllText(outputPath, xml, new UTF8Encoding(false));
            return new FileInfo(outputPath);
        }

        internal static SaveCookieMatch MatchSavableCookies(Uri sourceUri, string rawCookies)
        {
            if (sourceUri == null || string.IsNullOrWhiteSpace(rawCookies))
            {
                return null;
            }

            string host = SafeLower(sourceUri.Host);
            if (IsYoukiaRuntimeHost(host))
            {
                string strongCookies = SelectStrongYoukiaCookies(rawCookies);
                if (!string.IsNullOrWhiteSpace(strongCookies))
                {
                    string userDomain = NormalizeRootUrl(sourceUri.GetLeftPart(UriPartial.Authority));
                    if (!string.IsNullOrWhiteSpace(userDomain))
                    {
                        return new SaveCookieMatch(sourceUri, userDomain, strongCookies, "youkia-strong");
                    }
                }
            }

            if (IsPvzolHost(host))
            {
                string pvzolCookie = SelectPvzolCookie(rawCookies);
                if (!string.IsNullOrWhiteSpace(pvzolCookie))
                {
                    string userDomain = NormalizeRootUrl(sourceUri.GetLeftPart(UriPartial.Authority));
                    if (!string.IsNullOrWhiteSpace(userDomain))
                    {
                        return new SaveCookieMatch(sourceUri, userDomain, pvzolCookie, "pvzol");
                    }
                }
            }

            return null;
        }

        internal static bool IsSupportedSavePage(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            if (IsLegacyYoukiaLandingPage(uri))
            {
                return !string.IsNullOrWhiteSpace(ExtractLegacyYoukiaSubdomain(uri));
            }

            string path = SafeLower(uri.AbsolutePath);
            if (string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string host = SafeLower(uri.Host);
            string full = SafeLower(uri.AbsoluteUri);
            foreach (string keyword in SaveUrlKeywords)
            {
                if (host.Contains(keyword) || path.Contains(keyword) || full.Contains(keyword))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string BuildTargetUrl(CookieProfile profile)
        {
            if (profile == null)
            {
                return null;
            }

            string rootUrl = NormalizeRootUrl(profile.UserDomain);
            if (string.IsNullOrWhiteSpace(rootUrl))
            {
                return null;
            }

            return new Uri(new Uri(rootUrl + "/"), TargetPath.TrimStart('/')).ToString();
        }

        internal static bool IsLegacyYoukiaLandingPage(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            string path = SafeLower(uri.AbsolutePath);
            return string.Equals(SafeLower(uri.Host), LegacyYoukiaHost, StringComparison.OrdinalIgnoreCase)
                && (path.StartsWith(LegacyYoukiaPrefix, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(LegacyYoukiaIndexPrefix, StringComparison.OrdinalIgnoreCase));
        }

        internal static string ExtractLegacyYoukiaSubdomain(Uri uri)
        {
            if (!IsLegacyYoukiaLandingPage(uri))
            {
                return null;
            }

            string[] segments = (uri.AbsolutePath ?? string.Empty)
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int subdomainIndex = SafeLower(uri.AbsolutePath).StartsWith(LegacyYoukiaIndexPrefix, StringComparison.OrdinalIgnoreCase)
                ? 2
                : 1;
            if (segments.Length <= subdomainIndex)
            {
                return null;
            }

            string subdomain = segments[subdomainIndex];
            string normalized = SafeLower(subdomain);
            if (string.IsNullOrWhiteSpace(subdomain)
                || normalized == "index.php"
                || normalized == "default"
                || normalized == "main")
            {
                return null;
            }

            return subdomain;
        }

        internal static string ResolveLegacyYoukiaRedirectTarget(Uri uri)
        {
            string subdomain = ExtractLegacyYoukiaSubdomain(uri);
            if (string.IsNullOrWhiteSpace(subdomain))
            {
                return null;
            }

            return "http://" + subdomain + ".youkia.pvz.youkia.com" + TargetPath;
        }

        internal static List<string> BuildCookieApplicationList(string rawCookies)
        {
            CookieApplicationPlan plan = BuildCookieApplicationPlan(rawCookies);
            return plan == null ? new List<string>() : new List<string>(plan.CookieEntries);
        }

        internal static CookieApplicationPlan BuildCookieApplicationPlan(string rawCookies)
        {
            return ExtractImportantCookies(rawCookies, includeFallbackCookies: true);
        }

        internal static string BuildProfileSignature(CookieProfile profile)
        {
            if (profile == null)
            {
                return string.Empty;
            }

            string domain = NormalizeRootUrl(profile.UserDomain) ?? string.Empty;
            CookieApplicationPlan plan = BuildCookieApplicationPlan(profile.UserCookies);
            string cookieBody = plan == null ? string.Empty : NormalizeCookieHeader(plan.CookieHeader);
            return (domain + "|" + cookieBody).Trim();
        }

        internal static string BuildSaveCookieMatchSignature(SaveCookieMatch match)
        {
            if (match == null)
            {
                return string.Empty;
            }

            string domain = NormalizeRootUrl(match.UserDomain) ?? string.Empty;
            string cookieBody = NormalizeCookieHeader(match.PersistedCookies);
            return (domain + "|" + cookieBody).Trim();
        }

        private CookieProfile ParseProfileText(string rawText, string filePath)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            List<string> blocks = ExtractUserSettingBlocks(rawText);
            if (blocks.Count == 0 && rawText.TrimStart().StartsWith("<", StringComparison.Ordinal))
            {
                blocks.Add(rawText);
            }

            foreach (string block in blocks)
            {
                ParsedProfileFields fields = ParseProfileFields(block);
                if (fields == null || string.IsNullOrWhiteSpace(fields.UserDomain) || string.IsNullOrWhiteSpace(fields.UserCookies))
                {
                    continue;
                }

                string normalizedDomain = NormalizeRootUrl(fields.UserDomain);
                string normalizedCookies = SelectPersistedCookies(fields.UserCookies);
                if (string.IsNullOrWhiteSpace(normalizedDomain) || string.IsNullOrWhiteSpace(normalizedCookies))
                {
                    continue;
                }

                return new CookieProfile(
                    filePath,
                    fields.UserId <= 0 ? 1 : fields.UserId,
                    string.IsNullOrWhiteSpace(fields.UserName) ? "未知用户" : fields.UserName.Trim(),
                    normalizedDomain,
                    normalizedCookies,
                    fields.UserLevel <= 0 ? 1 : fields.UserLevel);
            }

            return null;
        }

        private static ParsedProfileFields ParseProfileFields(string xmlText)
        {
            try
            {
                XDocument document = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace);
                XElement root = document.Element("UserSetting");
                if (root == null)
                {
                    return null;
                }

                return new ParsedProfileFields
                {
                    UserId = ParseInteger(root.Element("UserID")?.Value, 1),
                    UserName = root.Element("UserName")?.Value,
                    UserDomain = root.Element("UserDomain")?.Value,
                    UserCookies = root.Element("UserCookies")?.Value,
                    UserLevel = ParseInteger(root.Element("UserLevel")?.Value, 1)
                };
            }
            catch
            {
                return null;
            }
        }

        private string BuildUniqueFileName(string baseName)
        {
            string safeBase = SanitizeFileName(baseName);
            if (string.IsNullOrWhiteSpace(safeBase))
            {
                safeBase = "PVZOLCookie";
            }

            string candidateName = safeBase + ".xml";
            string candidatePath = Path.Combine(_cookieDirectory, candidateName);
            if (!File.Exists(candidatePath))
            {
                return candidateName;
            }

            int index = 2;
            while (true)
            {
                candidateName = safeBase + "_" + index + ".xml";
                candidatePath = Path.Combine(_cookieDirectory, candidateName);
                if (!File.Exists(candidatePath))
                {
                    return candidateName;
                }

                index += 1;
            }
        }

        private static string BuildProfileXml(int userId, string userDomain, string userCookies, string userName, int userLevel)
        {
            return "<?xml version=\"1.0\" ?>\n"
                + "<UserSetting>\n"
                + "  <UserID>" + userId + "</UserID>\n"
                + "  <UserDomain>" + EscapeXml(userDomain) + "</UserDomain>\n"
                + "  <UserCookies>" + EscapeXml(userCookies) + "</UserCookies>\n"
                + "  <UserName>" + EscapeXml(userName) + "</UserName>\n"
                + "  <UserLevel>" + userLevel + "</UserLevel>\n"
                + "</UserSetting>\n";
        }

        private FileInfo SaveImportedProfile(CookieProfile profile, string fileNameHint)
        {
            if (profile == null)
            {
                return null;
            }

            EnsureInitialized();

            string userDomain = NormalizeRootUrl(profile.UserDomain);
            string normalizedCookies = SelectPersistedCookies(profile.UserCookies);
            if (string.IsNullOrWhiteSpace(userDomain) || string.IsNullOrWhiteSpace(normalizedCookies))
            {
                return null;
            }

            string userName = SanitizeProfileName(profile.UserName);
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = "未知用户";
            }

            string fileNameBase = SanitizeProfileName(fileNameHint);
            if (string.IsNullOrWhiteSpace(fileNameBase))
            {
                fileNameBase = userName;
            }

            string fileName = BuildUniqueFileName(fileNameBase);
            string outputPath = Path.Combine(_cookieDirectory, fileName);
            string xml = BuildProfileXml(
                profile.UserId <= 0 ? 1 : profile.UserId,
                userDomain,
                normalizedCookies,
                userName,
                profile.UserLevel <= 0 ? 1 : profile.UserLevel);
            File.WriteAllText(outputPath, xml, new UTF8Encoding(false));
            return new FileInfo(outputPath);
        }

        private FileInfo SaveRawImportedProfile(string rawText, string fileNameHint)
        {
            EnsureInitialized();

            string fileNameBase = SanitizeProfileName(fileNameHint);
            if (string.IsNullOrWhiteSpace(fileNameBase))
            {
                fileNameBase = BuildDefaultProfileName();
            }

            string fileName = BuildUniqueFileName(fileNameBase);
            string outputPath = Path.Combine(_cookieDirectory, fileName);
            File.WriteAllText(outputPath, rawText ?? string.Empty, new UTF8Encoding(false));
            return new FileInfo(outputPath);
        }

        private static string ResolveSaveUserDomain(Uri pageUri)
        {
            if (pageUri == null)
            {
                return null;
            }

            if (IsLegacyYoukiaLandingPage(pageUri))
            {
                string subdomain = ExtractLegacyYoukiaSubdomain(pageUri);
                if (!string.IsNullOrWhiteSpace(subdomain))
                {
                    return "http://" + subdomain + ".youkia.pvz.youkia.com";
                }
            }

            if (string.IsNullOrWhiteSpace(pageUri.Authority))
            {
                return null;
            }

            return NormalizeRootUrl(pageUri.GetLeftPart(UriPartial.Authority));
        }

        private static string NormalizeRootUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string normalized = value.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "http://" + normalized;
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri uri) || string.IsNullOrWhiteSpace(uri.Authority))
            {
                return null;
            }

            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string BuildDefaultProfileName()
        {
            return "cookie_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        private static string SanitizeProfileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string sanitized = value.Trim();
            sanitized = sanitized.Replace(TargetPath, string.Empty);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalid, '_');
            }

            sanitized = Regex.Replace(sanitized, "\\s+", " ").Trim();
            if (sanitized.Length > 40)
            {
                sanitized = sanitized.Substring(0, 40).Trim();
            }

            return sanitized;
        }

        private static string SanitizeFileName(string value)
        {
            return SanitizeProfileName(value).Replace(' ', '_');
        }

        private static string EscapeXml(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string SelectPersistedCookies(string rawCookies)
        {
            CookieApplicationPlan plan = ExtractImportantCookies(rawCookies, includeFallbackCookies: false);
            return plan == null ? null : plan.CookieHeader;
        }

        private static string SelectStrongYoukiaCookies(string rawCookies)
        {
            List<string> entries = SplitCookieEntries(rawCookies);
            if (entries.Count == 0)
            {
                return null;
            }

            List<string> phpSessions = new List<string>();
            List<string> pvzYoukiaEntries = new List<string>();
            foreach (string entry in entries)
            {
                string key = GetCookieKey(entry);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.Equals(key, CookieKeyPhpSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueCookieEntry(phpSessions, entry);
                    continue;
                }

                if (string.Equals(key, CookieKeyPvzYoukiaNew1, StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueCookieEntry(pvzYoukiaEntries, entry);
                }
            }

            if (phpSessions.Count == 0 || pvzYoukiaEntries.Count == 0)
            {
                return null;
            }

            List<string> persistedEntries = new List<string>(phpSessions.Count + pvzYoukiaEntries.Count + 1);
            persistedEntries.AddRange(phpSessions);
            persistedEntries.AddRange(pvzYoukiaEntries);
            string pvzolEntry = SelectPvzolCookie(rawCookies);
            if (!string.IsNullOrWhiteSpace(pvzolEntry))
            {
                AddUniqueCookieEntry(persistedEntries, pvzolEntry);
            }
            return string.Join("; ", persistedEntries);
        }

        private static string SelectPvzolCookie(string rawCookies)
        {
            List<string> entries = SplitCookieEntries(rawCookies);
            foreach (string entry in entries)
            {
                string key = GetCookieKey(entry);
                if (string.Equals(key, CookieKeyPvzol, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private static CookieApplicationPlan ExtractImportantCookies(string rawCookies, bool includeFallbackCookies)
        {
            List<string> entries = SplitCookieEntries(rawCookies);
            if (entries.Count == 0)
            {
                return null;
            }

            List<string> phpSessions = new List<string>();
            List<string> pvzYoukiaEntries = new List<string>();
            string pvzolEntry = null;

            foreach (string entry in entries)
            {
                string key = GetCookieKey(entry);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (string.Equals(key, CookieKeyPhpSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueCookieEntry(phpSessions, entry);
                    continue;
                }

                if (string.Equals(key, CookieKeyPvzYoukiaNew1, StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueCookieEntry(pvzYoukiaEntries, entry);
                    continue;
                }

                if (pvzolEntry == null && string.Equals(key, CookieKeyPvzol, StringComparison.OrdinalIgnoreCase))
                {
                    pvzolEntry = entry;
                }
            }

            if (phpSessions.Count > 0 && pvzYoukiaEntries.Count > 0)
            {
                List<string> persistedEntries = new List<string>(phpSessions.Count + pvzYoukiaEntries.Count + 1);
                persistedEntries.AddRange(phpSessions);
                persistedEntries.AddRange(pvzYoukiaEntries);
                if (!string.IsNullOrWhiteSpace(pvzolEntry))
                {
                    AddUniqueCookieEntry(persistedEntries, pvzolEntry);
                }

                return new CookieApplicationPlan(persistedEntries, "youkia-strong");
            }

            if (!string.IsNullOrWhiteSpace(pvzolEntry))
            {
                return new CookieApplicationPlan(new[] { pvzolEntry }, "pvzol");
            }

            if (includeFallbackCookies)
            {
                List<string> fallbackEntries = entries
                    .Where(entry => !string.IsNullOrWhiteSpace(GetCookieKey(entry)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (fallbackEntries.Count > 0)
                {
                    return new CookieApplicationPlan(fallbackEntries, "fallback");
                }
            }

            return null;
        }

        private static void AddUniqueCookieEntry(List<string> target, string entry)
        {
            string key = SafeLower(GetCookieKey(entry));
            string value = SafeLower(GetCookieValue(entry));
            foreach (string existing in target)
            {
                if (key == SafeLower(GetCookieKey(existing)) && value == SafeLower(GetCookieValue(existing)))
                {
                    return;
                }
            }

            target.Add(entry);
        }

        private static List<string> SplitCookieEntries(string rawCookies)
        {
            if (string.IsNullOrWhiteSpace(rawCookies))
            {
                return new List<string>();
            }

            List<string> entries = new List<string>();
            string[] parts = rawCookies.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string entry = (part ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(entry) || entry.IndexOf('=') <= 0)
                {
                    continue;
                }

                entries.Add(entry);
            }

            return entries;
        }

        private static string GetCookieKey(string cookieEntry)
        {
            if (string.IsNullOrWhiteSpace(cookieEntry))
            {
                return null;
            }

            int index = cookieEntry.IndexOf('=');
            if (index <= 0)
            {
                return null;
            }

            return cookieEntry.Substring(0, index).Trim();
        }

        private static string GetCookieValue(string cookieEntry)
        {
            if (string.IsNullOrWhiteSpace(cookieEntry))
            {
                return null;
            }

            int index = cookieEntry.IndexOf('=');
            if (index < 0 || index >= cookieEntry.Length - 1)
            {
                return string.Empty;
            }

            return cookieEntry.Substring(index + 1).Trim();
        }

        private static List<string> ExtractUserSettingBlocks(string rawText)
        {
            List<string> blocks = new List<string>();
            MatchCollection matches = UserSettingBlockRegex.Matches(rawText ?? string.Empty);
            foreach (Match match in matches)
            {
                if (match.Success && !string.IsNullOrWhiteSpace(match.Value))
                {
                    blocks.Add(match.Value);
                }
            }

            return blocks;
        }

        private static int ParseInteger(string value, int fallback)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out int parsed) ? parsed : fallback;
        }

        private static string SafeLower(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static string NormalizeCookieHeader(string rawCookies)
        {
            return string.Join("; ", SplitCookieEntries(rawCookies)
                .Select(entry => entry.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static bool IsYoukiaRuntimeHost(string host)
        {
            const string suffix = ".youkia.pvz.youkia.com";
            return !string.IsNullOrWhiteSpace(host)
                && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }

        private static bool IsPvzolHost(string host)
        {
            return string.Equals(host, "pvzol.org", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".pvzol.org", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ParsedProfileFields
        {
            internal int UserId { get; set; }

            internal string UserName { get; set; }

            internal string UserDomain { get; set; }

            internal string UserCookies { get; set; }

            internal int UserLevel { get; set; }
        }

        internal sealed class SaveCookieMatch
        {
            internal SaveCookieMatch(Uri sourceUri, string userDomain, string persistedCookies, string rule)
            {
                SourceUri = sourceUri;
                UserDomain = userDomain ?? string.Empty;
                PersistedCookies = persistedCookies ?? string.Empty;
                Rule = rule ?? string.Empty;
            }

            internal Uri SourceUri { get; }

            internal string UserDomain { get; }

            internal string PersistedCookies { get; }

            internal string Rule { get; }
        }

        internal sealed class CookieApplicationPlan
        {
            internal CookieApplicationPlan(IEnumerable<string> cookieEntries, string rule)
            {
                CookieEntries = (cookieEntries ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                CookieHeader = string.Join("; ", CookieEntries);
                Rule = rule ?? string.Empty;
            }

            internal List<string> CookieEntries { get; }

            internal string CookieHeader { get; }

            internal string Rule { get; }
        }

        internal sealed class CookieProfile
        {
            internal CookieProfile(string filePath, int userId, string userName, string userDomain, string userCookies, int userLevel)
            {
                FilePath = filePath;
                UserId = userId;
                UserName = userName;
                UserDomain = userDomain;
                UserCookies = userCookies;
                UserLevel = userLevel;
            }

            internal string FilePath { get; }

            internal int UserId { get; }

            internal string UserName { get; }

            internal string UserDomain { get; }

            internal string UserCookies { get; }

            internal int UserLevel { get; }
        }

        internal sealed class ImportResult
        {
            private ImportResult(bool success, FileInfo importedFile, CookieProfile profile, string errorMessage)
            {
                Success = success;
                ImportedFile = importedFile;
                Profile = profile;
                ErrorMessage = errorMessage ?? string.Empty;
            }

            internal bool Success { get; }

            internal FileInfo ImportedFile { get; }

            internal CookieProfile Profile { get; }

            internal string ErrorMessage { get; }

            internal static ImportResult FromSuccess(FileInfo importedFile, CookieProfile profile)
            {
                return new ImportResult(true, importedFile, profile, string.Empty);
            }

            internal static ImportResult FromFailure(string errorMessage)
            {
                return new ImportResult(false, null, null, errorMessage);
            }
        }
    }
}
