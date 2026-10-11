using System;
using System.IO;
using System.Media;
using Dropshelf.Services;

namespace Dropshelf.Activities.Timer;

/// <summary>Plays the timer's "ding" (see <see cref="ChimeSynth"/>) with <see cref="SoundPlayer"/>.</summary>
internal static class Chime
{
    private static byte[]? _wav;
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            _wav ??= ChimeSynth.BuildWav();
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(_wav));
            _player.Play(); // loads and plays on a worker thread
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Log.Error("Could not play the timer chime", ex); // no audio device, or the sound service is down
        }
    }
}
