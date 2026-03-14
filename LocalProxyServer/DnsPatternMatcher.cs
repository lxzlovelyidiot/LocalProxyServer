using System.Text.RegularExpressions;

namespace LocalProxyServer
{
    public static class DnsPatternMatcher
    {
        public static bool IsMatch(string name, List<string>? patterns)
        {
            if (patterns == null || patterns.Count == 0)
            {
                return false;
            }

            foreach (var pattern in patterns)
            {
                if (IsMatch(name, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsMatch(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            // Convert glob pattern to regex
            // * -> .*
            // ? -> .
            // Escape other regex special characters
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
        }
    }
}
