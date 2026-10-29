using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

class TTSTest
{
    static async Task Main()
    {
        try
        {
            var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
            var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
            var voice = Environment.GetEnvironmentVariable("AZURE_SPEECH_VOICE") ?? "en-GB-RyanNeural";

            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region))
            {
                Console.WriteLine("❌ Azure Speech credentials missing!");
                return;
            }

            Console.WriteLine($"✅ Using voice: {voice} in {region}");

            var speechConfig = SpeechConfig.FromSubscription(key, region);
            speechConfig.SpeechSynthesisVoiceName = voice;
            
            using var audioConfig = AudioConfig.FromDefaultSpeakerOutput();
            using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);

            var result = await synthesizer.SpeakTextAsync("Hello world. Text to speech is working correctly.", CancellationToken.None);
            
            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                Console.WriteLine("✅ TTS WORKING: Audio played successfully!");
            else
                Console.WriteLine($"❌ TTS FAILED: {result.Reason} - {result.ErrorDetails}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
    }
}
