using System;
using System.Threading.Tasks;

namespace LiveMeetAI
{
    // Minimal interface to satisfy compile-time references.
    // Adjust signatures later to match your real implementations.
    public interface ITranscriptionService : IDisposable
    {
        // Event invoked when a partial/complete transcript chunk is available.
        // Code earlier referenced OnTranscriptChunk  provide an event to match.
        event Action<string>? OnTranscriptChunk;

        // Accepts WAV bytes captured by AudioCaptureService.
        // MainWindow used TranscribeChunkAsync(byte[] wavBytes) earlier  keep a Task return.
        Task TranscribeChunkAsync(byte[] wavBytes);

        // Optional helper to stop/cleanup transcription (no-op in many implementations)
        Task StopAsync();
    }
}
