using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Shared email-domain checks used by the student and admin Create Account screens.
    /// 1) IsProperDomain: the domain must be well-formed (any TLD, not just .edu).
    /// 2) TryGetTypoSuggestion: catches common provider typos like gmial.com / gmail.con.
    /// 3) IsAllowed: OPTIONAL allow-list. An empty list means "any proper domain is accepted".
    /// Entries can be a domain ("school.edu"), with a leading "@" ("@school.edu"),
    /// or a wildcard ("*.edu" matches school.edu and cs.school.edu).
    /// </summary>
    public static class EmailDomainValidator
    {
        /// <summary>Returns the lower-cased domain part of the email, or null if malformed.</summary>
        public static string GetDomain(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            string trimmed = email.Trim();
            int at = trimmed.LastIndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1) return null;
            return trimmed.Substring(at + 1).ToLowerInvariant();
        }

        // One DNS label: letters/digits/hyphens, 1-63 chars, no leading/trailing hyphen.
        private static readonly Regex LabelRegex =
            new Regex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

        // TLD: letters only (2+), or punycode for internationalised TLDs (xn--...).
        private static readonly Regex TldRegex =
            new Regex(@"^([a-z]{2,63}|xn--[a-z0-9-]{1,59})$", RegexOptions.Compiled);

        /// <summary>
        /// True when the email's domain is structurally proper: at least two labels,
        /// valid characters, no empty labels ("a..com"), no leading/trailing hyphens,
        /// a real-looking TLD (not numeric), and within DNS length limits.
        /// Works for any TLD (.com, .org, .ph, .edu.ph, .co.uk, ...).
        /// </summary>
        public static bool IsProperDomain(string email)
        {
            string domain = GetDomain(email);
            if (domain == null || domain.Length > 253) return false;

            string[] labels = domain.Split('.');
            if (labels.Length < 2) return false;

            for (int i = 0; i < labels.Length; i++)
            {
                if (!LabelRegex.IsMatch(labels[i])) return false;
            }
            return TldRegex.IsMatch(labels[labels.Length - 1]);
        }

        private static readonly Dictionary<string, string> CommonTypos = new Dictionary<string, string>
        {
            { "gmial.com", "gmail.com" }, { "gmai.com", "gmail.com" }, { "gmail.con", "gmail.com" },
            { "gmail.co", "gmail.com" }, { "gmail.cm", "gmail.com" }, { "gnail.com", "gmail.com" },
            { "gamil.com", "gmail.com" }, { "gmaill.com", "gmail.com" }, { "gmail.om", "gmail.com" },
            { "yahooo.com", "yahoo.com" }, { "yaho.com", "yahoo.com" }, { "yahoo.con", "yahoo.com" },
            { "yahoo.co", "yahoo.com" }, { "hotmial.com", "hotmail.com" }, { "hotmail.con", "hotmail.com" },
            { "hotmal.com", "hotmail.com" }, { "outlok.com", "outlook.com" }, { "outlook.con", "outlook.com" },
            { "iclod.com", "icloud.com" }, { "icloud.con", "icloud.com" },
        };

        /// <summary>If the domain is a known typo of a popular provider, returns the corrected address.</summary>
        public static bool TryGetTypoSuggestion(string email, out string suggestion)
        {
            suggestion = null;
            string domain = GetDomain(email);
            if (domain == null || !CommonTypos.TryGetValue(domain, out string fixedDomain)) return false;

            string trimmed = email.Trim();
            suggestion = trimmed.Substring(0, trimmed.LastIndexOf('@') + 1) + fixedDomain;
            return true;
        }

        /// <summary>True when the email's domain is on the allow-list (or the list is empty).</summary>
        public static bool IsAllowed(string email, IList<string> allowedDomains)
        {
            if (allowedDomains == null || allowedDomains.Count == 0) return true;

            string domain = GetDomain(email);
            if (domain == null) return false;

            foreach (string raw in allowedDomains)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string rule = raw.Trim().TrimStart('@').ToLowerInvariant();

                if (rule.StartsWith("*."))
                {
                    string suffix = rule.Substring(1); // ".edu"
                    if (domain.EndsWith(suffix, StringComparison.Ordinal)) return true;
                }
                else if (domain == rule)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Friendly list for error messages, e.g. "@school.edu, @gmail.com".</summary>
        public static string Describe(IList<string> allowedDomains)
        {
            var parts = new List<string>();
            foreach (string raw in allowedDomains)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                parts.Add("@" + raw.Trim().TrimStart('@'));
            }
            return string.Join(", ", parts);
        }
    }
}
