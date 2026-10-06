using UnityEngine;
using UnityEngine.UIElements;
using Anatomia3D.Backend;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Draws a badge's icon into a plain VisualElement - shared by the admin
    /// Gamification Settings list/modal and the student Achievements /
    /// Classroom Detail badge cards so all of them render badges the same way.
    ///
    /// iconUrl (Cloudinary) wins when present; otherwise the preset iconKey is shown
    /// through its .badge-preset-{key} USS class (see Theme/AnatomiaTheme.uss).
    /// While a custom image downloads (or if the download fails) the preset is
    /// shown, so a badge never renders blank. Downloads reuse
    /// StudentAvatarLoader's URL cache.
    /// </summary>
    public static class BadgeIconView
    {
        public static string PresetClass(string key) => "badge-preset-" + BadgeIcons.Normalize(key);

        /// <summary>Creates a fixed-size square element showing the badge icon.</summary>
        public static VisualElement Create(string iconKey, string iconUrl, float size)
        {
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList("badge-art");
            art.style.width = size;
            art.style.height = size;
            art.style.flexShrink = 0;
            Apply(art, iconKey, iconUrl);
            return art;
        }

        /// <summary>(Re)paints an existing element with the given icon.</summary>
        public static void Apply(VisualElement art, string iconKey, string iconUrl)
        {
            if (art == null) return;

            foreach (var k in BadgeIcons.PresetKeys) art.RemoveFromClassList(PresetClass(k));
            art.style.backgroundImage = StyleKeyword.Null;
            art.style.unityBackgroundImageTintColor = StyleKeyword.Null;

            // Preset first: it is the fallback while a custom image loads / if it fails.
            art.AddToClassList(PresetClass(iconKey));

            if (string.IsNullOrEmpty(iconUrl)) return;

            string requested = iconUrl;
            art.userData = requested;

            StudentAvatarLoader.LoadUrl(requested, tex =>
            {
                if (tex == null) return; // keep the preset
                if (!(art.userData is string current) || current != requested) return; // element was reused for another icon

                art.style.backgroundImage = new StyleBackground(Background.FromTexture2D(tex));
                art.style.unityBackgroundImageTintColor = Color.white; // preset tint must not colour a real image
            });
        }
    }
}
