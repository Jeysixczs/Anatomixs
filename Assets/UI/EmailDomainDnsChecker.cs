using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Checks that an email's domain really exists and can receive mail, using
    /// Google's public DNS-over-HTTPS API (no API key needed):
    ///   https://dns.google/resolve?name=example.com&amp;type=MX
    /// Looks for MX records first, then falls back to A/AAAA records (some
    /// domains accept mail that way). Results are cached per session.
    ///
    /// It proves the DOMAIN exists, not that a specific mailbox does.
    /// If the lookup itself fails (offline, timeout, service down) the result is
    /// Unknown and callers should let the sign-up continue (fail open).
    ///
    /// Usage (from a MonoBehaviour):
    ///   StartCoroutine(EmailDomainDnsChecker.Check("gmail.com", result => { ... }));
    /// </summary>
    public static class EmailDomainDnsChecker
    {
        public enum Result
        {
            Exists,        // domain has MX (or A/AAAA) records
            NotFound,      // domain does not exist (NXDOMAIN) or has no mail/address records
            NoMail,        // domain publishes a "null MX" - explicitly accepts no email
            Unknown        // lookup failed - could not verify
        }

        private const string Endpoint = "https://dns.google/resolve";
        private const int TimeoutSeconds = 6;
        private const int TypeA = 1, TypeMx = 15, TypeAaaa = 28;

        // Only definitive answers are cached; Unknown is retried next time.
        private static readonly Dictionary<string, Result> Cache = new Dictionary<string, Result>();

        [Serializable] private class DnsAnswer { public int type; public string data; }
        [Serializable] private class DnsResponse { public int Status; public DnsAnswer[] Answer; }

        public static IEnumerator Check(string domain, Action<Result> onComplete)
        {
            domain = domain?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(domain))
            {
                onComplete?.Invoke(Result.NotFound);
                yield break;
            }

            if (Cache.TryGetValue(domain, out Result cached))
            {
                onComplete?.Invoke(cached);
                yield break;
            }

            // 1) MX records
            DnsResponse mx = null;
            yield return Query(domain, "MX", r => mx = r);
            if (mx == null) { onComplete?.Invoke(Result.Unknown); yield break; }

            if (mx.Status == 3) { yield return Finish(domain, Result.NotFound, onComplete); yield break; } // NXDOMAIN

            if (mx.Status == 0 && mx.Answer != null)
            {
                bool hasMx = false, nullMx = false;
                foreach (var a in mx.Answer)
                {
                    if (a.type != TypeMx) continue;
                    hasMx = true;
                    // RFC 7505 "null MX": "0 ." means the domain accepts no mail.
                    string d = (a.data ?? "").Trim();
                    if (d == "0 ." || d == "0 ") nullMx = true;
                    else nullMx = false;
                }
                if (hasMx)
                {
                    yield return Finish(domain, nullMx ? Result.NoMail : Result.Exists, onComplete);
                    yield break;
                }
            }

            // 2) No MX: fall back to A, then AAAA
            foreach (string type in new[] { "A", "AAAA" })
            {
                DnsResponse addr = null;
                yield return Query(domain, type, r => addr = r);
                if (addr == null) { onComplete?.Invoke(Result.Unknown); yield break; }

                if (addr.Status == 0 && addr.Answer != null)
                {
                    foreach (var a in addr.Answer)
                    {
                        if (a.type == TypeA || a.type == TypeAaaa)
                        {
                            yield return Finish(domain, Result.Exists, onComplete);
                            yield break;
                        }
                    }
                }
            }

            yield return Finish(domain, Result.NotFound, onComplete);
        }

        private static IEnumerator Finish(string domain, Result result, Action<Result> onComplete)
        {
            Cache[domain] = result;
            onComplete?.Invoke(result);
            yield break;
        }

        private static IEnumerator Query(string domain, string type, Action<DnsResponse> onDone)
        {
            string url = $"{Endpoint}?name={UnityWebRequest.EscapeURL(domain)}&type={type}";
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = TimeoutSeconds;
                req.SetRequestHeader("Accept", "application/dns-json");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[EmailDomainDnsChecker] {type} lookup for '{domain}' failed: {req.error}");
                    onDone(null);
                    yield break;
                }

                try
                {
                    onDone(JsonUtility.FromJson<DnsResponse>(req.downloadHandler.text));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[EmailDomainDnsChecker] Could not parse DNS response: {e.Message}");
                    onDone(null);
                }
            }
        }
    }
}
