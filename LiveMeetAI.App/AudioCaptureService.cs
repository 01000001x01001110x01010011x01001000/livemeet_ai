using System;
using System.IO;
using NAudio.Wave;
using System.Threading;
using LiveMeetAI.App;

namespace LiveMeetAI.Audio
{
    public enum CaptureSource
    {
        SystemLoopback,
        Microphone
    }

    public class AudioChunkEventArgs : EventArgs
    {
        public byte[]? WavBytes { get; set; }
    }

    public class AudioCaptureService : IDisposable
    {
        public event EventHandler<AudioChunkEventArgs>? OnChunkReady;

        private WasapiLoopbackCapture? loopback;
        private WaveInEvent? waveIn;
        private MemoryStream? bufferStream;
        private WaveFileWriter? writer;
        private readonly object lockObj = new object();

        // choose chunk length (ms)
        // Increase to 3000ms (3s) to prevent splitting short sentences.
        private const int ChunkMilliseconds = 3000;


        public void Start(CaptureSource source)
        {
            Stop();

            // Log available devices to help debug Bluetooth issues
            try
            {
                int deviceCount = WaveIn.DeviceCount;
                FileLogger.Info($"AudioCaptureService: Found {deviceCount} recording devices.");
                for (int i = 0; i < deviceCount; i++)
                {
                    var caps = WaveIn.GetCapabilities(i);
                    FileLogger.Info($"  Device {i}: {caps.ProductName} (Channels: {caps.Channels})");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"AudioCaptureService: Failed to list devices: {ex.Message}");
            }

            if (source == CaptureSource.SystemLoopback)
            {
                loopback = new WasapiLoopbackCapture();
                SetUpWriter(loopback.WaveFormat);
                loopback.DataAvailable += Loopback_DataAvailable;
                loopback.StartRecording();
            }
            else
            {
                // Find best device (User prefers 'realme')
                int targetDeviceIndex = 0;
                string targetDeviceName = "Default (0)";

                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var caps = WaveIn.GetCapabilities(i);
                    // Match "realme" case-insensitive
                    if (caps.ProductName.IndexOf("realme", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetDeviceIndex = i;
                        targetDeviceName = caps.ProductName;
                        break;
                    }
                }

                var selectedCaps = WaveIn.GetCapabilities(targetDeviceIndex);
                int channels = selectedCaps.Channels;
                if (channels < 1) channels = 1; // safety

                FileLogger.Info($"AudioCaptureService: Selected Microphone: '{targetDeviceName}' (Device {targetDeviceIndex}) - Channels: {channels}");

                waveIn = new WaveInEvent
                {
                    DeviceNumber = targetDeviceIndex, 
                    // Use 44.1kHz. Use device's native channel count to avoid mismatch artifacts.
                    WaveFormat = new WaveFormat(44100, 16, channels), 
                    BufferMilliseconds = 200
                };
                
                FileLogger.Info($"AudioCaptureService: Configured WaveFormat: {waveIn.WaveFormat}");
                SetUpWriter(waveIn.WaveFormat);
                waveIn.DataAvailable += WaveIn_DataAvailable;
                waveIn.RecordingStopped += (s, a) => FileLogger.Info("AudioCaptureService: Microphone recording stopped.");
                waveIn.StartRecording();
            }
        }

        private void SetUpWriter(WaveFormat format)
        {
            lock (lockObj)
            {
                writer?.Dispose();
                bufferStream?.Dispose();

                bufferStream = new MemoryStream();
                writer = new WaveFileWriter(new IgnoreDisposeStream(bufferStream), format);
            }
        }
        private void Loopback_DataAvailable(object? sender, WaveInEventArgs e)
        {
            OnAudioData(e.Buffer, 0, e.BytesRecorded);
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            OnAudioData(e.Buffer, 0, e.BytesRecorded);
        }

        private void OnAudioData(byte[] buffer, int offset, int count)
        {
            lock (lockObj)
            {
                if (writer == null || bufferStream == null) return;
                writer.Write(buffer, offset, count);
                writer.Flush();

                // If buffer length exceeds chunk threshold, emit chunk
                if (bufferStream.Length >= writer.WaveFormat.AverageBytesPerSecond * ChunkMilliseconds / 1000)
                {
                    FlushAndEmit();
                }
            }
        }

        private void FlushAndEmit()
        {
            if (writer == null || bufferStream == null) return;
            var format = writer.WaveFormat;

            // finalize writer to write proper WAV headers
            writer.Flush();
            writer.Dispose();

            // copy bytes (complete WAV)
            byte[] bytes;
            try
            {
                bytes = bufferStream.ToArray();
            }
            catch
            {
                bytes = Array.Empty<byte>();
            }

            // reset stream & writer
            bufferStream.Dispose();
            bufferStream = new MemoryStream();
            writer = new WaveFileWriter(new IgnoreDisposeStream(bufferStream), format);
            try
            {
                var debugDir = @"C:\Temp\LiveMeetAI_chunks";
                Directory.CreateDirectory(debugDir);
                var fname = Path.Combine(debugDir, $"chunk_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
                File.WriteAllBytes(fname, bytes);
            }
            catch { /* ignore debug errors */ }
            // send synchronously so we can track pending requests
            try
            {
                OnChunkReady?.Invoke(this, new AudioChunkEventArgs { WavBytes = bytes });
            }
            catch { /* swallow */ }
        }

        public void Stop()
        {
            try
            {
                loopback?.StopRecording();
            }
            catch { }
            try
            {
                waveIn?.StopRecording();
            }
            catch { }

            loopback?.Dispose();
            loopback = null;
            waveIn?.Dispose();
            waveIn = null;

            lock (lockObj)
            {
                // Flush any remaining audio in the buffer so we don't lose the last word
                FlushAndEmit();

                writer?.Dispose();
                writer = null;
                bufferStream?.Dispose();
                bufferStream = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // helper to avoid disposing underlying memory stream when WaveFileWriter is disposed
        private class IgnoreDisposeStream : Stream
        {
            private readonly Stream _inner;
            public IgnoreDisposeStream(Stream inner) { _inner = inner; }
            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) { /* don't dispose underlying stream */ }
        }
    }
}
