using System;
using System.Collections.Generic;
using System.Numerics;

namespace SensorSdk.ExampleWinUI3;

/// <summary>Magnitude spectrum of the snapshot channels.</summary>
public static class SpectrumCompute
{
    public static void Compute(List<float[]> channels, float rate,
                               out float[] freqs, out List<float[]> mags)
    {
        freqs = [];
        mags = new List<float[]>();
        if (channels.Count == 0 || rate <= 0)
            return;
        int n = channels[0].Length;
        if (n < 16)
            return;
        int nfft = 1;
        while (nfft < n)
            nfft <<= 1;
        // Hann window
        var window = new double[n];
        double winSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            window[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            winSum += window[i];
        }
        freqs = new float[nfft / 2 + 1];
        for (int k = 0; k <= nfft / 2; k++)
            freqs[k] = (float)(k * (double)rate / nfft);
        var buf = new Complex[nfft];
        foreach (float[] ch in channels)
        {
            int m = Math.Min(n, ch.Length);
            for (int i = 0; i < nfft; i++)
                buf[i] = i < m ? new Complex(ch[i] * window[i], 0.0) : Complex.Zero;
            // FFT
            for (int i = 1, j = 0; i < nfft; i++)
            {
                int bit = nfft >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                    (buf[i], buf[j]) = (buf[j], buf[i]);
            }
            for (int len = 2; len <= nfft; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
                for (int i = 0; i < nfft; i += len)
                {
                    Complex w = Complex.One;
                    for (int k = 0; k < len / 2; k++)
                    {
                        Complex u = buf[i + k];
                        Complex v = buf[i + k + len / 2] * w;
                        buf[i + k] = u + v;
                        buf[i + k + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
            var row = new float[nfft / 2 + 1];
            for (int k = 0; k <= nfft / 2; k++)
                row[k] = (float)(2.0 * Complex.Abs(buf[k]) / Math.Max(winSum, 1e-12));
            mags.Add(row);
        }
    }
}
