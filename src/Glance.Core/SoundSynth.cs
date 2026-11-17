namespace Glance;

public enum SoundKind { Connected, Disconnected, Low, Critical, Charged, Update }

/// <summary>
/// Glance's own sounds, synthesised at runtime (no audio files in the package, a few KB of PCM each).
/// Two families that match the two looks:
/// • Liquid Glass — soft glassy chimes: sine tones with inharmonic bell partials, gentle shimmer and long tails.
/// • Classic — short, clean, Windows-like UI blips (triangle tones, quick decay).
/// Every note gets a 4 ms attack and a smooth release, so nothing clicks.
/// </summary>
public static class SoundSynth
{
    const int Rate = 44100;

    readonly record struct Note(double Freq, double Start, double Length, double Gain = 1);

    /// <summary>Complete 16-bit mono WAV file for <paramref name="kind"/> at <paramref name="volume"/> (0–1).</summary>
    public static byte[] Wav(SoundKind kind, bool glass, double volume)
    {
        var notes = glass ? GlassNotes(kind) : ClassicNotes(kind);
        double total = notes.Max(n => n.Start + n.Length) + 0.05;
        int count = (int)(total * Rate);
        var mix = new double[count];
        foreach (var n in notes)
        {
            if (glass) Bell(mix, n, kind == SoundKind.Critical);
            else Blip(mix, n);
        }
        // normalise to a comfortable peak, then apply the user volume
        double peak = mix.Max(Math.Abs);
        double scale = peak > 0 ? 0.55 / peak * Math.Clamp(volume, 0, 1) : 0;
        var pcm = new short[count];
        for (int i = 0; i < count; i++) pcm[i] = (short)Math.Clamp(mix[i] * scale * short.MaxValue, short.MinValue, short.MaxValue);
        return ToWav(pcm);
    }

    // ── Liquid Glass ──
    static Note[] GlassNotes(SoundKind k) => k switch
    {
        SoundKind.Connected => new[] { new Note(1318.5, 0, 0.55), new Note(1975.5, 0.09, 0.7, 0.85) },            // E6 → B6, rising
        SoundKind.Disconnected => new[] { new Note(1567.9, 0, 0.45, 0.8), new Note(1046.5, 0.1, 0.6, 0.75) },     // G6 → C6, falling
        SoundKind.Low => new[] { new Note(880, 0, 0.35), new Note(880, 0.18, 0.5) },                              // two soft taps
        SoundKind.Critical => new[] { new Note(1046.5, 0, 0.3), new Note(880, 0.14, 0.3), new Note(698.5, 0.28, 0.6) },
        SoundKind.Charged => new[] { new Note(1046.5, 0, 0.5, 0.8), new Note(1318.5, 0.08, 0.5, 0.8), new Note(1568, 0.16, 0.5, 0.8), new Note(2093, 0.24, 0.9) },
        _ => new[] { new Note(1568, 0, 0.3, 0.6), new Note(1975.5, 0.05, 0.3, 0.6), new Note(2349.3, 0.1, 0.35, 0.65), new Note(3136, 0.15, 0.6, 0.7) }, // sparkle
    };

    static void Bell(double[] mix, Note n, bool tremolo)
    {
        int s0 = (int)(n.Start * Rate), len = (int)(n.Length * Rate);
        // inharmonic partials of a small glass bell + a faint octave shimmer
        (double Ratio, double Gain, double Decay)[] partials = { (1, 1, 5.5), (2.76, 0.32, 9), (5.40, 0.12, 14), (2.0, 0.18, 7) };
        for (int i = 0; i < len && s0 + i < mix.Length; i++)
        {
            double t = i / (double)Rate;
            double attack = Math.Min(1, t / 0.004);
            double release = i > len - 400 ? (len - i) / 400.0 : 1;
            double v = 0;
            foreach (var (ratio, gain, decay) in partials)
                v += gain * Math.Exp(-decay * t) * Math.Sin(2 * Math.PI * n.Freq * ratio * t + 0.3 * Math.Sin(2 * Math.PI * 5.2 * t) * ratio * 0.002);
            if (tremolo) v *= 0.8 + 0.2 * Math.Sin(2 * Math.PI * 9 * t);
            mix[s0 + i] += v * attack * release * n.Gain;
        }
    }

    // ── Classic ──
    static Note[] ClassicNotes(SoundKind k) => k switch
    {
        SoundKind.Connected => new[] { new Note(784, 0, 0.07), new Note(1046.5, 0.075, 0.1) },
        SoundKind.Disconnected => new[] { new Note(1046.5, 0, 0.07), new Note(784, 0.075, 0.1) },
        SoundKind.Low => new[] { new Note(660, 0, 0.08), new Note(660, 0.14, 0.08) },
        SoundKind.Critical => new[] { new Note(880, 0, 0.09), new Note(660, 0.11, 0.09), new Note(880, 0.22, 0.12) },
        SoundKind.Charged => new[] { new Note(523.3, 0, 0.07), new Note(659.3, 0.075, 0.07), new Note(784, 0.15, 0.12) },
        _ => new[] { new Note(1046.5, 0, 0.06), new Note(1318.5, 0.065, 0.09) },
    };

    static void Blip(double[] mix, Note n)
    {
        int s0 = (int)(n.Start * Rate), len = (int)(n.Length * Rate);
        for (int i = 0; i < len && s0 + i < mix.Length; i++)
        {
            double t = i / (double)Rate;
            double ph = n.Freq * t % 1.0;
            double tri = 4 * Math.Abs(ph - 0.5) - 1;               // triangle: soft but defined
            double env = Math.Min(1, t / 0.004) * Math.Exp(-18 * t) * (i > len - 200 ? (len - i) / 200.0 : 1);
            mix[s0 + i] += tri * env * n.Gain;
        }
    }

    static byte[] ToWav(short[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
