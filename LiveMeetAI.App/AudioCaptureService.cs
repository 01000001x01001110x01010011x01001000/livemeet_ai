using System;
using System.IO;
using NAudio.Wave;
using System.Threading;

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
        // reduce from 2000 -> 1000 (milliseconds)
        private const int ChunkMilliseconds = 1000;


        public void Start(CaptureSource source)
        {
            Stop();

            if (source == CaptureSource.SystemLoopback)
            {
                loopback = new WasapiLoopbackCapture();
                SetUpWriter(loopback.WaveFormat);
                loopback.DataAvailable += Loopback_DataAvailable;
                loopback.StartRecording();
            }
            else
            {
                waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1), // 16k mono - adjust if needed
                    BufferMilliseconds = 200
                };
                SetUpWriter(waveIn.WaveFormat);
                waveIn.DataAvailable += WaveIn_DataAvailable;
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
                    // send on background thread
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            OnChunkReady?.Invoke(this, new AudioChunkEventArgs { WavBytes = bytes });
                        }
                        catch { /* swallow */ }
                    });
                }
            }
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
