using UnityEngine;
using UnityEngine.UIElements;

namespace Anatomia3D.UI
{
    /// <summary>
    /// UI Toolkit's TextField draws and manages its own text input instead of
    /// wrapping a real native Android/iOS text view. That's why, on Android:
    ///   - long-press never shows the native paste bubble
    ///   - the keyboard never shows predictive-text word suggestions
    ///   - the keyboard never shows Autofill (saved email/account) suggestions
    ///
    /// This routes a TextField's input through TouchScreenKeyboard.Open
    /// instead, which DOES create a real native input on Android, so the OS
    /// keyboard behaves normally - including Autofill, once the field is
    /// tagged with the right TouchScreenKeyboardType (e.g. EmailAddress).
    ///
    /// Usage (after querying the field, e.g. in QueryElements()):
    ///   _emailField.EnableNativeKeyboard(this, TouchScreenKeyboardType.EmailAddress);
    ///
    /// "this" must be a MonoBehaviour so the helper can drive an Update loop
    /// to keep the field's value in sync with the native keyboard while it's open.
    /// </summary>
    public static class NativeKeyboardHelper
    {
        public static void EnableNativeKeyboard(
            this TextField field,
            MonoBehaviour owner,
            TouchScreenKeyboardType keyboardType = TouchScreenKeyboardType.Default,
            bool autocorrection = true)
        {
            if (field == null || owner == null) return;

            // TouchScreenKeyboard is a mobile-only concept. In the Editor and
            // on desktop builds there's no real native keyboard to hand off
            // to, and calling Open() there is what tends to produce a
            // half-alive keyboard object that throws on .text access. Leave
            // UI Toolkit's own (perfectly fine) handling in place everywhere
            // except real Android/iOS.
            if (Application.platform != RuntimePlatform.Android &&
                Application.platform != RuntimePlatform.IPhonePlayer)
            {
                return;
            }

            // The screen this field belongs to gets its whole visual tree
            // rebuilt on every OnEnable, so QueryElements() - and this call -
            // runs again each time the screen is shown. Without cleaning up
            // the previous driver, each re-show would add another
            // NativeKeyboardDriver component pointed at the now-detached old
            // field, leaking components and leaving stale drivers around.
            foreach (var stale in owner.gameObject.GetComponents<NativeKeyboardDriver>())
            {
                Object.Destroy(stale);
            }

            var driver = owner.gameObject.AddComponent<NativeKeyboardDriver>();
            driver.Bind(field, keyboardType, autocorrection);
        }

        /// <summary>
        /// Small internal MonoBehaviour that owns the per-frame sync between
        /// TouchScreenKeyboard.text and the TextField's value. One of these
        /// is added per field that opts in.
        /// </summary>
        private class NativeKeyboardDriver : MonoBehaviour
        {
            private TextField _field;
            private TouchScreenKeyboardType _keyboardType;
            private bool _autocorrection;
            private TouchScreenKeyboard _keyboard;

            public void Bind(TextField field, TouchScreenKeyboardType keyboardType, bool autocorrection)
            {
                _field = field;
                _keyboardType = keyboardType;
                _autocorrection = autocorrection;

                _field.RegisterCallback<FocusInEvent>(OnFocusIn);
                _field.RegisterCallback<FocusOutEvent>(OnFocusOut);
            }

            private void OnFocusIn(FocusInEvent evt)
            {
                // secure = false here even for the login screen's own field,
                // since this driver is only meant for non-password fields
                // (email, name, classroom code, etc). Don't reuse it for
                // password fields without reviewing secure-entry behavior.
                _keyboard = TouchScreenKeyboard.Open(
                    _field.value ?? string.Empty,
                    _keyboardType,
                    autocorrection: _autocorrection,
                    multiline: false,
                    secure: false);
            }

            private void OnFocusOut(FocusOutEvent evt)
            {
                _keyboard = null;
            }

            private void Update()
            {
                if (_keyboard == null) return;

                // TouchScreenKeyboard can end up in a state where the C#
                // wrapper object is non-null but the native keyboard behind
                // it has already gone away (closed via the OS back button,
                // app losing focus, etc). Reading .text or .status in that
                // state can throw a NullReferenceException from native code
                // rather than returning a safe default, so this is
                // deliberately defensive rather than trusting the object is
                // still alive just because the reference isn't null.
                try
                {
                    if (_field != null && _keyboard.text != _field.value)
                    {
                        _field.SetValueWithoutNotify(_keyboard.text);
                    }

                    if (_keyboard.status != TouchScreenKeyboard.Status.Visible)
                    {
                        _keyboard = null;
                    }
                }
                catch (System.NullReferenceException)
                {
                    // The native keyboard died out from under us - drop the
                    // reference and fall back to UI Toolkit's own field value
                    // as-is rather than crashing every frame.
                    _keyboard = null;
                }
            }

            private void OnDestroy()
            {
                if (_field == null) return;
                _field.UnregisterCallback<FocusInEvent>(OnFocusIn);
                _field.UnregisterCallback<FocusOutEvent>(OnFocusOut);
            }
        }
    }
}
