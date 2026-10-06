using System.Collections.Generic;

namespace Anatomia3D.Backend
{
    /// <summary>
    /// Single source of truth for the preset badge icons. A badge stores either
    ///   - iconKey  : one of PresetKeys (drawn from the project's icon PNGs via the
    ///                .badge-preset-{key} classes in Theme/AnatomiaTheme.uss), or
    ///   - iconUrl  : a Cloudinary URL of an image the teacher uploaded (wins over iconKey).
    /// Old badges saved before this change only have an emoji in the "icon" field;
    /// FromLegacyEmoji() maps those onto the closest preset so nothing breaks.
    /// </summary>
    public static class BadgeIcons
    {
        public const string DefaultKey = "trophy";

        public static readonly string[] PresetKeys =
        {
            "trophy", "medal", "shield", "brain", "bone",
            "skull", "cardio", "muscular", "chart", "heart",
        };

        private static readonly Dictionary<string, string> LegacyEmojiMap = new Dictionary<string, string>
        {
            { "\U0001F3C6", "trophy" },  // trophy
            { "\U0001F31F", "medal" },   // glowing star
            { "\u2B50",     "medal" },   // star
            { "\U0001F396", "medal" },   // military medal
            { "\U0001F3C5", "medal" },   // sports medal
            { "\U0001F451", "trophy" },  // crown
            { "\U0001F48E", "shield" },  // gem
            { "\U0001F3AF", "chart" },   // target
            { "\U0001F680", "chart" },   // rocket
            { "\U0001F525", "heart" },   // fire
            { "\U0001F44D", "heart" },   // thumbs up
            { "\U0001F9E0", "brain" },   // brain
        };

        public static string Normalize(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                foreach (var k in PresetKeys) if (k == key) return key;
            }
            return DefaultKey;
        }

        public static string FromLegacyEmoji(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return DefaultKey;
            string cleaned = emoji.Replace("\uFE0F", string.Empty).Trim();
            return LegacyEmojiMap.TryGetValue(cleaned, out var key) ? key : DefaultKey;
        }
    }
}
