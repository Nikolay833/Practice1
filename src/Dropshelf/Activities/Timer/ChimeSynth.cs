using System;
using System.IO;

namespace Dropshelf.Activities.Timer;

/// <summary>
/// The timer's "ding": two soft bell notes synthesized as a 16-bit mono WAV in memory (sine partials with an exponential decay,
/// no audio file in the app).
/// </summary>
internal static class ChimeSynth
{
    private const int SampleRate = 44100;

    /// <summary>A complete WAV file: G5 then C6, each a bell-like decaying sine with two quiet overtones.</summary>
    public static byte[] BuildWav()
    {
        const double seconds = 1.7;
        int count = (int)(SampleRate * seconds);
        var mix = new double[count];
        AddBell(mix, 783.99, 0.00, 0.30);   // G5
        AddBell(mix, 1046.50, 0.17, 0.32);  // C6

        var pcm = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            double v = Math.Clamp(mix[i], -1, 1);
            short sample = (short)Math.Round(v * short.MaxValue);
            pcm[2 * i] = (byte)(sample & 0xFF);
            pcm[2 * i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);                 // PCM header size
            w.Write((short)1);           // PCM
            w.Write((short)1);           // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);     // byte rate
            w.Write((short)2);           // block align
            w.Write((short)16);          // bits per sample
            w.Write("data"u8.ToArray());
            w.Write(pcm.Length);
            w.Write(pcm);
        }

        return ms.ToArray();
    }

    private static void AddBell(double[] mix, double frequency, double startSeconds, double gain)
    {
        int start = (int)(startSeconds * SampleRate);
        for (int i = 0; start + i < mix.Length; i++)
        {
            double t = i / (double)SampleRate;
            double attack = Math.Min(1, t / 0.006);          // 6 ms fade-in: no click
            double decay = Math.Exp(-t * 3.4);
            double tone = Math.Sin(2 * Math.PI * frequency * t)
                          + 0.28 * Math.Sin(2 * Math.PI * frequency * 2.0 * t) * Math.Exp(-t * 2.0)
                          + 0.10 * Math.Sin(2 * Math.PI * frequency * 3.01 * t) * Math.Exp(-t * 4.0);
            mix[start + i] += gain * attack * decay * tone;
        }
    }
}
