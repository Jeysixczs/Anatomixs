using System;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using UnityEngine;

/// <summary>
/// Minimal offline (on-device) text-to-speech wrapper. No cloud/network
/// calls - each platform's own built-in TTS engine does the work:
///   - Android: android.speech.tts.TextToSpeech, which ships on every
///     Android device. Called directly via AndroidJavaObject - no extra
///     .aar/.jar plugin needed.
///   - iOS: AVSpeechSynthesizer. Unlike Android, Unity has no built-in C#
///     API for this, so it's bridged through a tiny native Objective-C
///     plugin (see TTSBridge.mm) via DllImport("__Internal").
///   - Editor / Standalone / any other platform: no OS TTS is reachable
///     from here, so calls are logged and ignored instead of throwing.
///
/// Usage: OfflineTextToSpeech.Speak(someText); OfflineTextToSpeech.Stop();
/// </summary>
public static class OfflineTextToSpeech
{
    public static object Instance { get; internal set; }
#if UNITY_ANDROID && !UNITY_EDITOR
    private static AndroidJavaObject _tts;

    // Kept as a static field (not just a local/inline "new InitListener()")
    // so the proxy object stays alive for as long as _tts does - an
    // inline-only reference can be garbage collected by Mono while the
    // Java side still expects to call back into it.
    private static InitListener _initListener;

    // TextToSpeech initializes asynchronously - onInit() can fire well
    // after the constructor returns. Calling speak() before that happens
    // is silently ignored by Android (no exception, no sound), which is
    // why Speak() queues text here instead and flushes it once ready.
    private static bool _isReady;
    private static string _pendingText;

    // Implements android.speech.tts.TextToSpeech.OnInitListener so we know
    // once the engine has actually finished loading before speaking.
    private class InitListener : AndroidJavaProxy
    {
        public InitListener() : base("android.speech.tts.TextToSpeech$OnInitListener") { }

        // Signature must match the Java interface exactly - called on the
        // Android main thread once TTS finishes (or fails to) initialize.
        void onInit(int status)
        {
            _isReady = (status == 0 /* TextToSpeech.SUCCESS */);
            Debug.Log("[OfflineTextToSpeech] Android TTS onInit status=" + status +
                      " (ready=" + _isReady + "). If this is anything other than " +
                      "0/true, the device/emulator likely has no TTS engine installed " +
                      "- check Settings > Accessibility > Text-to-speech output.");

            if (_isReady && !string.IsNullOrEmpty(_pendingText))
            {
                string text = _pendingText;
                _pendingText = null;
                SpeakNow(text);
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (_tts != null) return;

        try
        {
            _initListener = new InitListener();
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _tts = new AndroidJavaObject(
                    "android.speech.tts.TextToSpeech",
                    activity,
                    _initListener);
            }
            Debug.Log("[OfflineTextToSpeech] TextToSpeech constructed, waiting for onInit...");
        }
        catch (System.Exception e)
        {
            Debug.LogError("[OfflineTextToSpeech] Failed to construct Android TTS: " + e);
            _tts = null;
        }
    }

    private static void SpeakNow(string text)
    {
        try
        {
            // QUEUE_FLUSH = 0 - stop whatever's currently playing and
            // speak this instead.
            _tts.Call<int>("speak", text, 0, null, "anatomy_tts");
        }
        catch (System.Exception e)
        {
            Debug.LogError("[OfflineTextToSpeech] speak() failed: " + e);
        }
    }
#elif UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void _TTS_Speak(string text);

    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void _TTS_Stop();
#endif

    /// <summary>
    /// Strips wiki-style formatting that reads badly out loud - "==
    /// Heading ==" markers, raw source URLs, and the extra blank lines
    /// left behind - so TTS speaks clean sentences instead of literally
    /// saying "equals equals" or spelling out a web address.
    /// </summary>
    private static string CleanForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // "== Heading ==" / "=== Heading ===" -> just "Heading" (keep the
        // text, drop the "=" symbols so it reads as a normal sentence).
        string cleaned = Regex.Replace(text, @"^\s*=+\s*(.*?)\s*=+\s*$", "$1", RegexOptions.Multiline);

        // Drop raw URLs entirely - TTS engines otherwise spell these out
        // character by character (e.g. a trailing Wikipedia source link).
        cleaned = Regex.Replace(cleaned, @"https?://\S+", "");

        // Collapse the extra blank lines/whitespace the removals above
        // leave behind, turning line breaks into sentence pauses.
        cleaned = Regex.Replace(cleaned, @"[ \t]{2,}", " ");
        cleaned = Regex.Replace(cleaned, @"\s*\n\s*", ". ");
        cleaned = Regex.Replace(cleaned, @"(\.\s*){2,}", ". ");

        return cleaned.Trim();
    }

    /// <summary>
    /// Speaks the given text aloud using the device's built-in TTS voice.
    /// Interrupts anything currently being spoken first (flushes the
    /// queue), so pressing Play on a newly-selected bone always starts
    /// fresh rather than queuing behind a previous description.
    /// </summary>
    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        text = CleanForSpeech(text);
        if (string.IsNullOrWhiteSpace(text)) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        EnsureInitialized();
        if (_tts == null) return;

        if (_isReady)
            SpeakNow(text);
        else
            _pendingText = text; // spoken automatically once onInit fires
#elif UNITY_IOS && !UNITY_EDITOR
        _TTS_Speak(text);
#else
        Debug.LogWarning("[OfflineTextToSpeech] No on-device TTS available on this platform " +
                          "(Editor/Standalone). Text that would have been spoken: " + text);
#endif
    }

    /// <summary>Stops any speech currently playing.</summary>
    public static void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _pendingText = null;
        if (_tts != null)
        {
            try
            {
                // TextToSpeech.stop() returns an int status code, not
                // void - Call<int>(...) must be used, not the plain
                // void-returning Call(...), or this throws a
                // NoSuchMethodError (wrong signature: expects "()V",
                // actual method is "()I").
                _tts.Call<int>("stop");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[OfflineTextToSpeech] stop() failed (engine may not have " +
                                  "initialized correctly - see the onInit log above): " + e);
            }
        }
#elif UNITY_IOS && !UNITY_EDITOR
        _TTS_Stop();
#endif
    }

    public static void InitializeOnStartup()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        EnsureInitialized();
        Debug.Log("[OfflineTextToSpeech] Android TTS initialization started on startup.");
#endif
        Debug.Log("[OfflineTextToSpeech] Initialized on startup.");
    }


}