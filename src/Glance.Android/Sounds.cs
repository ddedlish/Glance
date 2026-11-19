using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;

namespace Glance.Droid;

/// <summary>Sound cues, synthesized on the fly (no audio files): a soft glass «дзынь».</summary>
internal enum Cue { Connect, Disconnect, Low, Full }

/// <summary>
/// Short pleasant chimes for accessory events. Every tone is generated in code — sine partials with a bell-like
/// overtone and an exponential fade — so there are no recorded sounds and nothing to license.
/// Silent / vibrate mode, «Не беспокоить» and calls mute them; «Звуки» on the main screen switches them off.
/// </summary>
internal static class Sounds
{
    const string Prefs = "glance", PrefSounds = "sounds";
    const int Rate = 44100;
    static long _lastAt;

    public static bool Enabled(Context c) => c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.GetBoolean(PrefSounds, true) ?? true;

    public static void SetEnabled(Context c, bool on) =>
        c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit()?.PutBoolean(PrefSounds, on)?.Apply();

    /// <summary>Plays a cue unless sounds are off or the phone should stay quiet. preview: the user just switched sounds on.</summary>
    public static void Play(Context c, Cue cue, bool preview = false)
    {
        string? why = Quiet(c, preview);
        // two events in the same moment (two devices connect together): one chime is enough
        long now = SystemClock.UptimeMillis();
        if (why is null && now - _lastAt < 700) why = "just played";
        if (why is not null) { Android.Util.Log.Info("Glance", $"sound: {cue} skipped ({why})"); return; }
        _lastAt = now;
        Android.Util.Log.Info("Glance", $"sound: {cue} played");
        Task.Run(() =>
        {
            AudioTrack? track = null;
            try
            {
                short[] pcm = Synth(cue);
                track = new AudioTrack.Builder()
                    .SetAudioAttributes(new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.Notification)!
                        .SetContentType(AudioContentType.Sonification)!
                        .Build()!)!
                    .SetAudioFormat(new AudioFormat.Builder()
                        .SetEncoding(Android.Media.Encoding.Pcm16bit)!
                        .SetSampleRate(Rate)!
                        .SetChannelMask(ChannelOut.Mono)!
                        .Build()!)!
                    .SetBufferSizeInBytes(pcm.Length * 2)!
                    .SetTransferMode(AudioTrackMode.Static)!
                    .Build()!;
                track.Write(pcm, 0, pcm.Length);
                track.Play();
                var t = track;
                track = null;
                new Handler(Looper.MainLooper!).PostDelayed(() => { try { t.Stop(); } catch { } try { t.Release(); } catch { } },
                    pcm.Length * 1000L / Rate + 300);
            }
            catch (Exception e) { Log.Error("Android sound", e); Android.Util.Log.Error("Glance", "sound failed: " + e.Message); }
            finally { try { track?.Release(); } catch { } }
        });
    }

    /// <summary>Null when a chime may play; otherwise the reason to stay quiet.</summary>
    static string? Quiet(Context c, bool preview)
    {
        if (!preview && !Enabled(c)) return "off";
        try
        {
            if (c.GetSystemService(Context.AudioService) is AudioManager am)
            {
                if (am.RingerMode != Android.Media.RingerMode.Normal) return "silent mode";
                if (am.Mode != Android.Media.Mode.Normal) return "call";
            }
            if (c.GetSystemService(Context.NotificationService) is NotificationManager nm
                && nm.CurrentInterruptionFilter != InterruptionFilter.All && nm.CurrentInterruptionFilter != InterruptionFilter.Unknown) return "do not disturb";
        }
        catch { }
        return null;
    }

    // ───────── synthesis ─────────

    /// <summary>(note frequency, start ms, loudness 0…1) per cue.</summary>
    static (float Hz, int At, float Amp)[] Notes(Cue cue) => cue switch
    {
        // rising fifth: something has arrived
        Cue.Connect => new[] { (659.25f, 0, 0.55f), (987.77f, 95, 0.6f) },
        // the same, falling and softer: it has left
        Cue.Disconnect => new[] { (987.77f, 0, 0.42f), (659.25f, 110, 0.38f) },
        // two low gentle taps: time to charge
        Cue.Low => new[] { (523.25f, 0, 0.5f), (440f, 190, 0.5f) },
        // bright major arpeggio: fully charged
        _ => new[] { (523.25f, 0, 0.42f), (659.25f, 75, 0.45f), (783.99f, 150, 0.48f), (1046.5f, 225, 0.5f) },
    };

    static short[] Synth(Cue cue)
    {
        var notes = Notes(cue);
        const float decay = 0.34f; // seconds to fade by e
        int last = notes.Max(n => n.At);
        int len = (int)(Rate * (last / 1000f + decay * 4.2f));
        var mix = new float[len];
        foreach (var (hz, at, amp) in notes)
        {
            int start = Rate * at / 1000;
            for (int i = start; i < len; i++)
            {
                float t = (i - start) / (float)Rate;
                float env = MathF.Exp(-t / decay) * Math.Min(1f, t / 0.006f); // 6 ms attack: no click
                float w = 2 * MathF.PI * hz * t;
                // fundamental + octave + a quickly fading bell overtone = soft glass «дзынь»
                float s = MathF.Sin(w) + 0.28f * MathF.Sin(2 * w) * MathF.Exp(-t / 0.18f) + 0.12f * MathF.Sin(2.76f * w) * MathF.Exp(-t / 0.08f);
                mix[i] += amp * env * s;
            }
        }
        float peak = mix.Max(MathF.Abs);
        float gain = peak > 0 ? 0.42f / peak : 0; // modest level: a cue, not an alarm
        var pcm = new short[len];
        for (int i = 0; i < len; i++)
        {
            float tail = Math.Min(1f, (len - i) / (Rate * 0.03f)); // fade the last 30 ms to silence
            pcm[i] = (short)(Math.Clamp(mix[i] * gain * tail, -1f, 1f) * short.MaxValue);
        }
        return pcm;
    }
}
