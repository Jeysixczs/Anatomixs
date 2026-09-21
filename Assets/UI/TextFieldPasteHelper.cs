using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// Unity UI Toolkit's runtime TextField implements its own text editing
    /// instead of wrapping the platform's native text view, so on Android it
    /// never gets the OS's long-press Cut/Copy/Paste bubble. This adds a
    /// long-press-to-paste gesture directly on a TextField as a workaround,
    /// without needing any extra button in the UXML.
    ///
    /// Usage (in any screen controller, after querying the field):
    ///   _classroomCodeField.EnableLongPressPaste();
    ///
    /// Optionally transform the pasted text (e.g. trim/uppercase a code):
    ///   _classroomCodeField.EnableLongPressPaste(s => s.Trim().ToUpperInvariant());
    /// </summary>
    public static class TextFieldPasteHelper
    {
        private const long LongPressThresholdMs = 500;

        public static void EnableLongPressPaste(this TextField field, System.Func<string, string> transform = null)
        {
            if (field == null) return;

            long pointerDownTime = 0;
            bool longPressTriggered = false;
            bool pointerDown = false;

            field.RegisterCallback<PointerDownEvent>(evt =>
            {
                pointerDown = true;
                longPressTriggered = false;
                pointerDownTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            });

            field.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!pointerDown) return;
                pointerDown = false;

                long elapsed = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pointerDownTime;
                if (elapsed < LongPressThresholdMs || longPressTriggered) return;

                longPressTriggered = true;

                string clipboard = GUIUtility.systemCopyBuffer;
                if (string.IsNullOrEmpty(clipboard)) return;

                field.value = transform != null ? transform(clipboard) : clipboard;
                field.Focus();
            });

            // Cancel the long-press if the finger/pointer leaves the field
            // (e.g. a drag or scroll started instead of a hold-to-paste).
            field.RegisterCallback<PointerLeaveEvent>(evt => pointerDown = false);
        }
    }
}
