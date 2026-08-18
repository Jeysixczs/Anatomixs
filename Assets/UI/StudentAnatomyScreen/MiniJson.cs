using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Minimal, dependency-free JSON parser used to load BoneDatabase.json at
// runtime. UnityEngine.JsonUtility can't deserialize BoneDatabase.json
// because its root is a dictionary keyed by bone name (not a fixed set of
// serialized fields), so this small recursive-descent parser reads the
// file into a plain object graph instead:
//
//   JSON object       -> Dictionary<string, object>
//   JSON array         -> List<object>
//   JSON string         -> string
//   JSON number         -> double
//   JSON true / false   -> bool
//   JSON null           -> null
//
// Only used by BoneDatabaseService. Kept intentionally small and
// self-contained so the anatomy screen has no external JSON package
// dependency.
public static class MiniJson
{
    public static object Parse(string json)
    {
        int index = 0;
        object result = ParseValue(json, ref index);
        return result;
    }

    private static object ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i);
            case '[': return ParseArray(s, ref i);
            case '"': return ParseString(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default: return ParseNumber(s, ref i);
        }
    }

    private static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var obj = new Dictionary<string, object>();
        i++; // consume '{'
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return obj; }

        while (true)
        {
            SkipWhitespace(s, ref i);
            string key = ParseString(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':')
                throw new FormatException($"Expected ':' at position {i}.");
            i++;
            object value = ParseValue(s, ref i);
            obj[key] = value;

            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON object.");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; break; }
            throw new FormatException($"Expected ',' or '}}' at position {i}.");
        }
        return obj;
    }

    private static List<object> ParseArray(string s, ref int i)
    {
        var list = new List<object>();
        i++; // consume '['
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }

        while (true)
        {
            object value = ParseValue(s, ref i);
            list.Add(value);

            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON array.");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; break; }
            throw new FormatException($"Expected ',' or ']' at position {i}.");
        }
        return list;
    }

    private static string ParseString(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"')
            throw new FormatException($"Expected '\"' at position {i}.");
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("Unterminated string in JSON.");
            char c = s[i++];
            if (c == '"') break;

            if (c == '\\')
            {
                if (i >= s.Length) throw new FormatException("Unterminated escape sequence in JSON.");
                char esc = s[i++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Invalid unicode escape in JSON.");
                        string hex = s.Substring(i, 4);
                        i += 4;
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new FormatException($"Invalid escape character '\\{esc}' in JSON.");
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static double ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
            i++;
        string numStr = s.Substring(start, i - start);
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            throw new FormatException($"Invalid number '{numStr}' in JSON at position {start}.");
        return result;
    }

    private static void Expect(string s, ref int i, string literal)
    {
        if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            throw new FormatException($"Expected '{literal}' at position {i}.");
        i += literal.Length;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
