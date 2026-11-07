using System;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace Crest.Services;

/// <summary>
/// Listens to what the speakers are playing (WASAPI loopback, nothing leaves the PC)
/// and turns it into a handful of smoothed, log-spaced frequency bands.
/// Runs only while the visualizer is on screen.
/// </summary>
public sealed class SpectrumService : IDisposable
{
    public const int Bands = 12;
    private const int N = 1024, M = 10; // 2^10

    private readonly object _lock = new();
    private readonly float[] _ring = new float[N];
    private int _write;
    private readonly Complex[] _fft = new Complex[N];
    private readonly float[] _window = new float[N];
    private readonly int[] _edges = new int[Bands + 1];
    private float _gain = 0.02f;
    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private long _lastData; // Environment.TickCount64 of the last non-silent buffer

    /// <summary>0..1 per band, low frequencies first. Read on the UI thread.</summary>
    public readonly float[] Levels = new float[Bands];

    public bool IsRunning => _capture != null;
    public bool HasSignal => Environment.TickCount64 - Interlocked.Read(ref _lastData) < 400;

    public SpectrumService()
    {
        for (int i = 0; i < N; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, N);
    }

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var cap = new WasapiLoopbackCapture();
            _sampleRate = cap.WaveFormat.SampleRate;
            ComputeEdges();
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, _) => { if (ReferenceEquals(_capture, cap)) { _capture = null; cap.Dispose(); } };
            cap.StartRecording();
            _capture = cap;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _capture = null;
        }
    }

    public void Stop()
    {
        var cap = _capture;
        _capture = null;
        if (cap == null) return;
        try { cap.StopRecording(); cap.Dispose(); } catch { }
        Array.Clear(Levels);
    }

    private void ComputeEdges()
    {
        // 45 Hz .. 14 kHz, log spaced.
        double lo = 45, hi = Math.Min(14000, _sampleRate / 2.0 - 1);
        double binHz = (double)_sampleRate / N;
        for (int i = 0; i <= Bands; i++)
        {
            double f = lo * Math.Pow(hi / lo, (double)i / Bands);
            _edges[i] = Math.Clamp((int)Math.Round(f / binHz), 1, N / 2 - 1);
        }
        for (int i = 1; i <= Bands; i++)
            if (_edges[i] <= _edges[i - 1]) _edges[i] = Math.Min(N / 2 - 1, _edges[i - 1] + 1);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture cap) return;
        var fmt = cap.WaveFormat;
        int ch = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frame = bytesPerSample * ch;
        if (frame == 0) return;

        bool any = false;
        lock (_lock)
        {
            for (int off = 0; off + frame <= e.BytesRecorded; off += frame)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int p = off + c * bytesPerSample;
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, p)
                         : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, p) / 32768f : 0;
                }
                float s = sum / ch;
                if (s != 0) any = true;
                _ring[_write] = s;
                _write = (_write + 1) % N;
            }
        }
        if (any) Interlocked.Exchange(ref _lastData, Environment.TickCount64);
    }

    /// <summary>Advance the smoothed bands by dt seconds.</summary>
    public void Update(double dt)
    {
        if (_capture == null || !HasSignal)
        {
            Decay(dt);
            return;
        }

        lock (_lock)
        {
            for (int i = 0; i < N; i++)
            {
                _fft[i].X = _ring[(_write + i) % N] * _window[i];
                _fft[i].Y = 0;
            }
        }
        FastFourierTransform.FFT(true, M, _fft);

        Span<float> raw = stackalloc float[Bands];
        float frameMax = 0;
        for (int b = 0; b < Bands; b++)
        {
            float acc = 0;
            int from = _edges[b], to = Math.Max(_edges[b] + 1, _edges[b + 1]);
            for (int k = from; k < to; k++)
            {
                float mag = MathF.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                if (mag > acc) acc = mag;
            }
            // Tilt up the highs so the treble is visible, not just the kick.
            acc *= 1f + b * 0.22f;
            raw[b] = acc;
            if (acc > frameMax) frameMax = acc;
        }

        // Automatic gain: quick to follow loud passages, slow to forget them.
        float a = (float)Math.Min(1, dt * (frameMax > _gain ? 6 : 0.35));
        _gain = Math.Max(0.0008f, _gain + (frameMax - _gain) * a);

        for (int b = 0; b < Bands; b++)
        {
            float v = MathF.Pow(Math.Clamp(raw[b] / (_gain * 1.05f), 0, 1), 0.8f);
            float k = (float)Math.Min(1, dt * (v > Levels[b] ? 28 : 7)); // fast attack, gentle release
            Levels[b] += (v - Levels[b]) * k;
        }
    }

    private void Decay(double dt)
    {
        float k = (float)Math.Min(1, dt * 5);
        for (int b = 0; b < Bands; b++) Levels[b] -= Levels[b] * k;
    }

    public void Dispose() => Stop();
}
