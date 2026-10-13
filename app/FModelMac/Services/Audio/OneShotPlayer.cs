using System.Diagnostics;

namespace FModelMac.Services.Audio;

/// <summary>
/// Plays short sounds over each other, like a game's sound effects (the 3D viewer's animation notifies): a few
/// AudioQueue outputs, each taking the next sound when it's free; with all of them busy, the oldest sound is cut.
/// </summary>
public sealed class OneShotPlayer : IDisposable
{
    private const int MaxVoices = 6;
    private readonly List<(AudioOutput Output, long Started)> _voices = [];

    public float Volume { get; set; } = 0.8f;

    /// <summary>How many sounds have been started (test harness).</summary>
    public int Played { get; private set; }

    public void Play(PcmAudio audio)
    {
        lock (_voices)
        {
            var voice = _voices.FindIndex(v => !v.Output.IsPlaying);
            if (voice < 0 && _voices.Count < MaxVoices)
            {
                _voices.Add((new AudioOutput(), 0));
                voice = _voices.Count - 1;
            }
            if (voice < 0) voice = _voices.IndexOf(_voices.MinBy(v => v.Started));

            var output = _voices[voice].Output;
            try
            {
                output.Load(audio);
                output.Volume = Volume;
                output.Play();
                _voices[voice] = (output, Stopwatch.GetTimestamp());
                Played++;
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not play a notify sound");
            }
        }
    }

    public void StopAll()
    {
        lock (_voices)
            foreach (var (output, _) in _voices) output.Stop();
    }

    public void Dispose()
    {
        lock (_voices)
        {
            foreach (var (output, _) in _voices) output.Dispose();
            _voices.Clear();
        }
    }
}
