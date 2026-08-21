using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;


[Serializable]
public class BoneDatabaseEntry
{
    public string boneId;      // the original JSON key, e.g. "Parietal bone.r"
    public string displayName; // human-readable name shown in the Info Panel title
    public string baseName;    // bone name without the .l/.r side suffix, if any
    public string description; // full description shown in the Info Panel body
}

// Loads BoneDatabase.json once and resolves a clicked bone GameObject's
// name to its display data. BoneDatabase.json is the single source of
// truth here - this class never hardcodes a bone name; every key it knows
// about comes straight from the JSON text it's given via Load().
public class BoneDatabaseService
{
    
    private readonly Dictionary<string, BoneDatabaseEntry> _byNormalizedKey = new Dictionary<string, BoneDatabaseEntry>();

    public int Count => _byNormalizedKey.Count;

    // Every loaded entry, for callers that need to diff the full JSON key
    // set against something else (e.g. AnatomyScreenController checking
    // which bones in BoneDatabase.json have no matching GameObject under
    // skeletonRoot). TryGetEntry alone can't answer that - it only goes
    // name -> entry, never "give me everything you have".
    public IEnumerable<BoneDatabaseEntry> AllEntries => _byNormalizedKey.Values;




    public void Load(string json)
    {
        _byNormalizedKey.Clear();

        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("[BoneDatabaseService] Load called with empty/null JSON text.");
            return;
        }

        object parsed;
        try
        {
            parsed = MiniJson.Parse(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[BoneDatabaseService] Failed to parse BoneDatabase.json: {e.Message}");
            return;
        }

        if (!(parsed is Dictionary<string, object> root))
        {
            Debug.LogError("[BoneDatabaseService] BoneDatabase.json root is not a JSON object - expected {\"Bone Name\": { \"displayName\": ..., \"description\": ... }, ...}.");
            return;
        }

        foreach (var kvp in root)
        {
            if (!(kvp.Value is Dictionary<string, object> fields))
            {
                Debug.LogWarning($"[BoneDatabaseService] Skipping '{kvp.Key}' - its value is not a JSON object.");
                continue;
            }

            var entry = new BoneDatabaseEntry
            {
                boneId = kvp.Key,
                displayName = GetString(fields, "displayName") ?? kvp.Key,
                baseName = GetString(fields, "baseName") ?? string.Empty,
                description = GetString(fields, "description") ?? string.Empty
            };

            string key = NormalizeKey(kvp.Key);
            if (string.IsNullOrEmpty(key)) continue;

            if (_byNormalizedKey.ContainsKey(key))
                Debug.LogWarning($"[BoneDatabaseService] Duplicate bone key after normalization ('{kvp.Key}' -> '{key}') - keeping the first entry seen.");
            else
                _byNormalizedKey[key] = entry;
        }

        Debug.Log($"[BoneDatabaseService] Loaded {_byNormalizedKey.Count} bone entries from BoneDatabase.json.");
    }

    // Robustly resolves a clicked bone GameObject's raw name - which may
    // carry extra whitespace, mixed case, or a Unity-appended "(Clone)"
    // suffix (added whenever the bone was Instantiate()'d rather than
    // referenced straight from the imported FBX) - to its BoneDatabase.json
    // entry.
    public bool TryGetEntry(string rawBoneName, out BoneDatabaseEntry entry)
    {
        string key = NormalizeKey(rawBoneName);
        if (string.IsNullOrEmpty(key))
        {
            entry = null;
            return false;
        }
        return _byNormalizedKey.TryGetValue(key, out entry);
    }

    private static string GetString(Dictionary<string, object> fields, string key)
    {
        return fields.TryGetValue(key, out var v) && v is string s ? s : null;
    }

    // Normalizes a bone name for matching: strips a trailing Unity
    // "(Clone)" suffix, collapses/trims whitespace, and lower-cases
    // everything. Applied identically to JSON keys (once, at load time)
    // and to every clicked GameObject's name (at lookup time), so both
    // sides are compared on equal footing regardless of stray spacing,
    // casing, or a runtime "(Clone)" suffix.
    public static string NormalizeKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        string s = Regex.Replace(raw, @"\s*\(Clone\)\s*", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.ToLowerInvariant();
    }
}
