using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using LiveMeetAI.Audio;
using LiveMeetAI.App; // For FileLogger
using Newtonsoft.Json.Linq;

namespace LiveMeetAI.STT
{
    // Local transcription service that converts to 16k mono WAV and posts to a local server
    public class LocalWhisperTranscriptionService : ITranscriptionService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private readonly string endpoint;
        public event Action<string>? OnTranscriptChunk;

        public LocalWhisperTranscriptionService(string endpointUrl)
        {
            endpoint = endpointUrl ?? throw new ArgumentNullException(nameof(endpointUrl));
        }

        public async Task TranscribeChunkAsync(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length < 100) return; // Ignore headers/empty


            // Convert to 16k mono WAV in-memory
            byte[] normalized;
            try
            {
                normalized = AudioUtils.ConvertWavTo16kMono(wavBytes);
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory("logs"); File.AppendAllText("logs/stt_error.log", $"Audio conversion failed: {ex}\n"); } catch { }
                normalized = wavBytes; // fallback
            }

            using var content = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(normalized);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(byteContent, "file", "chunk.wav");

            try
            {
                var resp = await _httpClient.PostAsync(endpoint, content);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    FileLogger.Error($"STT Server HTTP Error {resp.StatusCode}: {body}");
                    return;
                }

                var text = body.Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

                // Try to parse as JSON first (since our Python server returns JSON {"text": "..."})
                try 
                {
                    if (text.StartsWith("{"))
                    {
                        var j = JObject.Parse(text);
                        if (j["text"] != null)
                        {
                            var t = j["text"].ToString();
                            if (!string.IsNullOrWhiteSpace(t))
                            {
                                OnTranscriptChunk?.Invoke(t);
                            }
                            return; // Success JSON handling
                        }
                        
                        // If it's JSON but no "text" field (maybe error json that slipped through as 200?), ignore it.
                        FileLogger.Warn($"STT received JSON without 'text' field: {text}. Ignoring.");
                        return;
                    }
                }
                catch 
                {
                    // Fallback: If parsing fails but it looked like JSON, or if it didn't look like JSON
                    // We might treat it as raw text if it doesn't look like an error.
                }

                // Fallback for raw text responses (if server changes or weird case)
                // But filter out obviously bad JSON-like strings that failed parsing
                if (!text.StartsWith("{"))
                {
                     OnTranscriptChunk?.Invoke(text);
                }
            }
            catch (Exception ex)
            {
                // Log error but DO NOT send it as a transcript chunk.
                // This prevents "{"error":...}" from appearing in the chat.
                try { 
                    FileLogger.Error($"Transcription request failed: {ex.Message}");
                    // Directory.CreateDirectory("logs"); File.AppendAllText("logs/stt_error.log", $"Transcription request failed: {ex}\n"); 
                } catch { }
                
                // OnTranscriptChunk?.Invoke($"{{\"error\":\"{ex.Message}\"}}"); // REMOVED: Do not bubble errors to chat
            }
        }
        // ensure at top of file: using System.Threading.Tasks;

        public async Task StopAsync()
        {
            // Minimal safe stop: signal any running work here if you have it.
            // This is intentionally a no-op that returns a completed Task.
            try
            {
                await Task.CompletedTask;
            }
            catch
            {
                // swallow - Stop should be safe during shutdown
            }
        }

        public void Dispose()
        {
            // Minimal safe dispose (idempotent no-op).
            // If your real implementation has disposable fields, dispose them here.
            try
            {
                // e.g. _httpClient?.Dispose(); _cts?.Cancel(); _cts?.Dispose();
            }
            catch
            {
                // swallow - Dispose must not throw
            }
        }

    }


}
