using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Xena.Desktop;

/// <summary>
/// Captures the default microphone and exposes a smoothed amplitude level plus
/// a small set of frequency-band magnitudes, so the UI can react to the user's
/// voice in real time. All public reads are thread-safe.
/// </summary>
public sealed class AudioMonitor : IDisposable
{
    private const int FftLength = 1024;                     // power of two
    private static readonly int FftM = (int)Math.Log2(FftLength);

    public int BandCount { get; }

    private WaveInEvent? _waveIn;
    private readonly float[] _sampleRing = new float[FftLength];
    private int _ringPos;
    private readonly float[] _window = new float[FftLength]; // Hann window
    private readonly Complex[] _fft = new Complex[FftLength];

    private readonly object _lock = new();
    private double _level;
    private readonly float[] _bands;
    private readonly double[] _bandEdges;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Amplitude gain applied to RMS before clamping to 0..1.</summary>
    public double Gain { get; set; } = 14.0;

    /// <summary>Ambient noise gate subtracted from RMS.</summary>
    public double NoiseFloor { get; set; } = 0.007;

    public AudioMonitor(int bandCount = 8)
    {
        BandCount = bandCount;
        _bands = new float[bandCount];

        for (int i = 0; i < FftLength; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftLength - 1))));

        // Log-spaced FFT-bin edges (roughly voice range) split into bands.
        _bandEdges = new double[bandCount + 1];
        const double lo = 2, hi = 460;
        for (int i = 0; i <= bandCount; i++)
            _bandEdges[i] = lo * Math.Pow(hi / lo, (double)i / bandCount);
    }

    public double Level
    {
        get { lock (_lock) return _level; }
    }

    public void CopyBands(float[] dest)
    {
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _bands.Length);
            Array.Copy(_bands, dest, n);
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 25,
                NumberOfBuffers = 3,
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
            IsRunning = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            try { _waveIn?.Dispose(); } catch { /* ignore */ }
            _waveIn = null;
        }
    }

    public void Stop()
    {
        if (!IsRunning && _waveIn == null) return;
        IsRunning = false;
        try { _waveIn?.StopRecording(); } catch { /* ignore */ }
        try { _waveIn?.Dispose(); } catch { /* ignore */ }
        _waveIn = null;
        lock (_lock)
        {
            _level = 0;
            Array.Clear(_bands, 0, _bands.Length);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int samples = e.BytesRecorded / 2;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            float f = s / 32768f;
            sumSq += f * f;
            _sampleRing[_ringPos] = f;
            _ringPos = (_ringPos + 1) % FftLength;
        }

        double rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
        double lvl = Math.Clamp((rms - NoiseFloor) * Gain, 0, 1);

        // FFT over the ring buffer in chronological order.
        for (int i = 0; i < FftLength; i++)
        {
            int idx = (_ringPos + i) % FftLength;
            _fft[i].X = _sampleRing[idx] * _window[i];
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftM, _fft);

        var bandTmp = new float[BandCount];
        int half = FftLength / 2;
        for (int b = 0; b < BandCount; b++)
        {
            int start = (int)Math.Floor(_bandEdges[b]);
            int end = (int)Math.Floor(_bandEdges[b + 1]);
            if (end <= start) end = start + 1;
            double max = 0;
            for (int k = start; k < end && k < half; k++)
            {
                double mag = Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                if (mag > max) max = mag;
            }
            bandTmp[b] = (float)Math.Clamp(max * 14.0, 0, 1);
        }

        lock (_lock)
        {
            _level = lvl;
            Array.Copy(bandTmp, _bands, BandCount);
        }
    }

    public void Dispose() => Stop();
}
