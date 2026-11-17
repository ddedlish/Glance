using System.Media;

namespace Glance;

/// <summary>
/// Plays Glance's synthesised sounds (see <see cref="SoundSynth"/>): the Liquid Glass set or the Classic set,
/// following the current look. Rendering happens once per sound/volume and is cached; playback is asynchronous
/// on the thread pool, so the UI never waits. Night (quiet hours) and fullscreen games mute everything
/// except the critical-battery sound.
/// </summary>
internal static class Sounds
{
    static readonly Dictionary<(SoundKind, bool, int), byte[]> Cache = new();
    static readonly object Gate = new();
    static SoundPlayer? _player;
    static MemoryStream? _stream;
    static DateTime _lastAt;
    static SoundKind _last;

    public static void Play(SoundKind kind, AppSettings s, bool preview = false)
    {
        if (!preview)
        {
            if (!s.Sounds) return;
            if (kind != SoundKind.Critical && (s.IsQuietNow() || (s.RespectFocus && Native.UserIsBusy()))) return;
            if (kind == _last && (DateTime.UtcNow - _lastAt).TotalSeconds < 1.5) return; // no machine-gun repeats
        }
        _last = kind;
        _lastAt = DateTime.UtcNow;
        bool glass = Theme.Current.Glass;
        int vol = Math.Clamp(s.SoundVolume, 0, 100);
        _ = Task.Run(() =>
        {
            try
            {
                byte[] wav;
                lock (Gate)
                {
                    if (!Cache.TryGetValue((kind, glass, vol), out wav!))
                        Cache[(kind, glass, vol)] = wav = SoundSynth.Wav(kind, glass, vol / 100.0);
                    _player?.Stop();
                    _player?.Dispose();
                    _stream?.Dispose();
                    _stream = new MemoryStream(wav, writable: false);
                    _player = new SoundPlayer(_stream);
                    _player.Play();
                }
            }
            catch (Exception e) { Log.Info("Sound: " + e.Message); }
        });
    }
}
