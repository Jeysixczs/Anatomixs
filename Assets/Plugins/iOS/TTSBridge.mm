// TTSBridge.mm
//
// Place this file under Assets/Plugins/iOS/ in the Unity project. Unity
// automatically compiles .mm files found there into the generated Xcode
// project - no extra setup needed beyond adding the file.
//
// Provides a tiny native bridge from C# (via extern "C" + DllImport in
// OfflineTextToSpeech.cs) to AVSpeechSynthesizer, since Unity has no
// built-in iOS TTS API the way it does for Android's TextToSpeech
// (reachable directly through AndroidJavaObject).

#import <AVFoundation/AVFoundation.h>

static AVSpeechSynthesizer *ttsSynthesizer = nil;

extern "C" {

void _TTS_Speak(const char* text) {
    if (ttsSynthesizer == nil) {
        ttsSynthesizer = [[AVSpeechSynthesizer alloc] init];
    }

    // Flush whatever's currently speaking so Play always restarts fresh,
    // matching the Android QUEUE_FLUSH behavior in OfflineTextToSpeech.cs.
    if ([ttsSynthesizer isSpeaking]) {
        [ttsSynthesizer stopSpeakingAtBoundary:AVSpeechBoundaryImmediate];
    }

    NSString *nsText = [NSString stringWithUTF8String:text];
    AVSpeechUtterance *utterance = [AVSpeechUtterance speechUtteranceWithString:nsText];
    utterance.voice = [AVSpeechSynthesisVoice voiceWithLanguage:@"en-US"];
    utterance.rate = AVSpeechUtteranceDefaultSpeechRate;

    [ttsSynthesizer speakUtterance:utterance];
}

void _TTS_Stop() {
    if (ttsSynthesizer != nil && [ttsSynthesizer isSpeaking]) {
        [ttsSynthesizer stopSpeakingAtBoundary:AVSpeechBoundaryImmediate];
    }
}

}
