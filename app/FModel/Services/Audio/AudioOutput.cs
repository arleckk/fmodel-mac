using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FModel.Services.Audio;

public sealed record AudioDevice(string Uid, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Plays PCM through macOS AudioToolbox (an AudioQueue fed 32-bit float buffers): the replacement for FModel's
/// CSCore/WASAPI output. Every seek or device change rebuilds the queue, which is cheap and avoids having to
/// reason about buffers still in flight.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    private const int BufferCount = 3;
    private const int FramesPerBuffer = 4096;

    /// <summary>State shared with the (real-time) callback thread of one queue.</summary>
    private sealed class QueueState
    {
        public float[] Samples = [];
        public int Channels;
        public long ReadFrame;
        public int Outstanding;
        public bool Draining, Started;
        public IntPtr Queue;
        public AudioOutput? Owner;
    }

    private QueueState? _state;
    private GCHandle _handle;
    private PcmAudio? _audio;
    private float[] _playback = [];
    private int _playbackChannels;
    private long _startFrame;
    private long _lastPosition;
    private bool _playing;
    private float _volume = 1;
    private string? _deviceUid;

    /// <summary>Raised (on a CoreAudio thread) when playback reaches the end.</summary>
    public event Action? Ended;

    public bool IsPlaying => _playing;
    public PcmAudio? Audio => _audio;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_state is { Queue: not 0 } s) AudioQueueSetParameter(s.Queue, ParamVolume, _volume);
        }
    }

    /// <summary>The output device UID, or null for the system default.</summary>
    public string? DeviceUid
    {
        get => _deviceUid;
        set
        {
            if (_deviceUid == value) return;
            _deviceUid = value;
            if (_audio is not null) Rebuild(PositionFrames, _playing);
        }
    }

    public TimeSpan Position => _audio is { } a ? TimeSpan.FromSeconds((double) PositionFrames / a.SampleRate) : TimeSpan.Zero;

    private long PositionFrames
    {
        get
        {
            if (_audio is null) return 0;
            if (_state is { Queue: not 0 } s && AudioQueueGetCurrentTime(s.Queue, IntPtr.Zero, out var time, out _) == 0 && time.SampleTime >= 0)
                _lastPosition = Math.Min(_startFrame + (long) time.SampleTime, _audio.Frames);
            return _lastPosition;
        }
    }

    public void Load(PcmAudio audio)
    {
        Close();
        _audio = audio;
        // AudioQueue maps more than two channels unpredictably without a channel layout: fold them into stereo
        (_playback, _playbackChannels) = audio.Channels <= 2 ? (audio.Samples, audio.Channels) : (Downmix(audio), 2);
        _startFrame = _lastPosition = 0;
        _playing = false;
    }

    public void Play()
    {
        if (_audio is null) return;
        // A paused (or primed but never started) queue just resumes; a finished one starts over
        if (_state is { Queue: not 0, Draining: false } s)
            Start(s);
        else
            Rebuild(PositionFrames >= _audio.Frames ? 0 : PositionFrames, true);
    }

    public void Pause()
    {
        if (_state is { Queue: not 0 } s && _playing) AudioQueuePause(s.Queue);
        _ = PositionFrames;
        _playing = false;
    }

    public void Seek(TimeSpan position)
    {
        if (_audio is null) return;
        var frame = Math.Clamp((long) (position.TotalSeconds * _audio.SampleRate), 0, _audio.Frames);
        Rebuild(frame, _playing);
    }

    public void Stop()
    {
        DisposeQueue();
        _startFrame = _lastPosition = 0;
        _playing = false;
    }

    /// <summary>Forgets the loaded sound.</summary>
    public void Close()
    {
        Stop();
        _audio = null;
        _playback = [];
    }

    public void Dispose() => Close();

    private void Rebuild(long fromFrame, bool play)
    {
        DisposeQueue();
        if (_audio is null) return;
        _startFrame = _lastPosition = fromFrame;
        _playing = false;
        if (fromFrame >= _audio.Frames) return;

        var state = new QueueState { Samples = _playback, Channels = _playbackChannels, ReadFrame = fromFrame, Owner = this };
        _handle = GCHandle.Alloc(state);
        var format = new AudioStreamBasicDescription
        {
            SampleRate = _audio.SampleRate,
            FormatID = FormatLinearPcm,
            FormatFlags = FlagIsFloat | FlagIsPacked,
            BytesPerPacket = (uint) (4 * state.Channels),
            FramesPerPacket = 1,
            BytesPerFrame = (uint) (4 * state.Channels),
            ChannelsPerFrame = (uint) state.Channels,
            BitsPerChannel = 32
        };
        Check(AudioQueueNewOutput(&format, &OnBufferDone, GCHandle.ToIntPtr(_handle), IntPtr.Zero, IntPtr.Zero, 0, out state.Queue), "create the audio queue");
        _state = state;

        if (_deviceUid is { } uid)
        {
            var cf = CoreFoundation.CreateString(uid);
            try
            {
                if (AudioQueueSetProperty(state.Queue, PropertyCurrentDevice, &cf, (uint) sizeof(IntPtr)) != 0)
                    Serilog.Log.Warning("Audio device {Device} is unavailable, using the default output", uid);
            }
            finally
            {
                CoreFoundation.CFRelease(cf);
            }
        }
        AudioQueueSetParameter(state.Queue, ParamVolume, _volume);

        for (var i = 0; i < BufferCount; i++)
        {
            Check(AudioQueueAllocateBuffer(state.Queue, (uint) (FramesPerBuffer * 4 * state.Channels), out var buffer), "allocate a buffer");
            Fill(state, buffer);
        }
        if (play) Start(state);
    }

    private void Start(QueueState state)
    {
        Check(AudioQueueStart(state.Queue, IntPtr.Zero), "start");
        state.Started = true;
        _playing = true;
        // A sound shorter than the primed buffers ran out before starting: stop once they've played
        if (state.ReadFrame >= state.Samples.Length / state.Channels && !state.Draining)
        {
            state.Draining = true;
            AudioQueueStop(state.Queue, 0);
        }
    }

    private void DisposeQueue()
    {
        if (_state is { } state)
        {
            _ = PositionFrames;
            if (state.Queue != 0) AudioQueueDispose(state.Queue, 1);
            state.Queue = 0;
            state.Owner = null;
            _state = null;
        }
        if (_handle.IsAllocated) _handle.Free();
    }

    // Copies the next frames into a buffer and queues it; once the data runs out, lets the queue drain and stop
    private static void Fill(QueueState state, IntPtr bufferPtr)
    {
        var buffer = (AudioQueueBuffer*) bufferPtr;
        var totalFrames = state.Samples.Length / state.Channels;
        var frames = (int) Math.Min(FramesPerBuffer, totalFrames - state.ReadFrame);
        if (frames <= 0)
        {
            if (!state.Draining && state.Started)
            {
                state.Draining = true;
                AudioQueueStop(state.Queue, 0); // asynchronous: plays what is queued, then stops
            }
            return;
        }
        Marshal.Copy(state.Samples, (int) (state.ReadFrame * state.Channels), buffer->AudioData, frames * state.Channels);
        buffer->AudioDataByteSize = (uint) (frames * state.Channels * 4);
        state.ReadFrame += frames;
        if (AudioQueueEnqueueBuffer(state.Queue, bufferPtr, 0, IntPtr.Zero) == 0) Interlocked.Increment(ref state.Outstanding);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferDone(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not QueueState { Queue: not 0 } state) return;
            var left = Interlocked.Decrement(ref state.Outstanding);
            Fill(state, buffer);
            if (state.Draining && left <= 0 && state.Owner is { } owner)
            {
                owner._playing = false;
                owner._lastPosition = state.Samples.Length / state.Channels;
                owner.Ended?.Invoke();
            }
        }
        catch
        {
            // never let an exception cross into CoreAudio
        }
    }

    private static float[] Downmix(PcmAudio audio)
    {
        var frames = audio.Frames;
        var output = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            float l = 0, r = 0;
            for (var c = 0; c < audio.Channels; c++)
            {
                var s = audio.Samples[f * audio.Channels + c];
                if (c % 2 == 0) l += s; else r += s;
            }
            var scale = 2f / audio.Channels;
            output[f * 2] = l * scale;
            output[f * 2 + 1] = r * scale;
        }
        return output;
    }

    private static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"Could not {what} (CoreAudio error {status})");
    }

    // ---------- Output devices (CoreAudio) ----------

    public static AudioDevice[] OutputDevices()
    {
        var devices = new List<AudioDevice>();
        var address = new AudioObjectPropertyAddress(PropertyDevices, ScopeGlobal, ElementMain);
        if (AudioObjectGetPropertyDataSize(SystemObject, &address, 0, IntPtr.Zero, out var size) != 0 || size == 0) return [];
        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
            if (AudioObjectGetPropertyData(SystemObject, &address, 0, IntPtr.Zero, &size, p) != 0) return [];

        foreach (var id in ids)
        {
            if (OutputChannels(id) == 0) continue;
            var uid = StringProperty(id, PropertyDeviceUid);
            if (uid is null) continue;
            devices.Add(new AudioDevice(uid, StringProperty(id, PropertyName) ?? uid));
        }
        return devices.ToArray();
    }

    public static string? DefaultOutputUid()
    {
        var address = new AudioObjectPropertyAddress(PropertyDefaultOutput, ScopeGlobal, ElementMain);
        uint id = 0, size = sizeof(uint);
        return AudioObjectGetPropertyData(SystemObject, &address, 0, IntPtr.Zero, &size, &id) == 0 ? StringProperty(id, PropertyDeviceUid) : null;
    }

    private static int OutputChannels(uint device)
    {
        var address = new AudioObjectPropertyAddress(PropertyStreamConfiguration, ScopeOutput, ElementMain);
        if (AudioObjectGetPropertyDataSize(device, &address, 0, IntPtr.Zero, out var size) != 0 || size < 8) return 0;
        var data = new byte[size];
        fixed (byte* p = data)
        {
            if (AudioObjectGetPropertyData(device, &address, 0, IntPtr.Zero, &size, p) != 0) return 0;
            // AudioBufferList: UInt32 count, then AudioBuffer { UInt32 channels; UInt32 bytes; void* data } at 8-byte alignment
            var count = *(uint*) p;
            var channels = 0;
            for (var i = 0; i < count && 8 + i * 16 + 4 <= size; i++) channels += (int) *(uint*) (p + 8 + i * 16);
            return channels;
        }
    }

    private static string? StringProperty(uint device, uint selector)
    {
        var address = new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMain);
        IntPtr value = 0;
        uint size = (uint) sizeof(IntPtr);
        if (AudioObjectGetPropertyData(device, &address, 0, IntPtr.Zero, &size, &value) != 0 || value == 0) return null;
        try
        {
            return CoreFoundation.ToManaged(value);
        }
        finally
        {
            CoreFoundation.CFRelease(value);
        }
    }

    // ---------- Native API ----------

    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagIsFloat = 1, FlagIsPacked = 8;
    private const uint ParamVolume = 1;
    private const uint PropertyCurrentDevice = 0x61716364; // 'aqcd'
    private const uint SystemObject = 1;
    private const uint PropertyDevices = 0x64657623; // 'dev#'
    private const uint PropertyDefaultOutput = 0x644F7574; // 'dOut'
    private const uint PropertyStreamConfiguration = 0x736C6179; // 'slay'
    private const uint PropertyName = 0x6C6E616D; // 'lnam'
    private const uint PropertyDeviceUid = 0x75696420; // 'uid '
    private const uint ScopeGlobal = 0x676C6F62, ScopeOutput = 0x6F757470; // 'glob', 'outp'
    private const uint ElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioObjectPropertyAddress(uint selector, uint scope, uint element)
    {
        public readonly uint Selector = selector, Scope = scope, Element = element;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(AudioStreamBasicDescription* format, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback,
        IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

    [DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packets, IntPtr descriptions);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(AudioToolbox)] private static extern int AudioQueuePause(IntPtr queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStop(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueSetParameter(IntPtr queue, uint parameter, float value);
    [DllImport(AudioToolbox)] private static extern int AudioQueueSetProperty(IntPtr queue, uint property, void* data, uint size);
    [DllImport(AudioToolbox)] private static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, out AudioTimeStamp time, out byte discontinuity);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, IntPtr qualifier, out uint size);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(uint objectId, AudioObjectPropertyAddress* address, uint qualifierSize, IntPtr qualifier, uint* size, void* data);

    private static class CoreFoundation
    {
        private const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Library)] public static extern void CFRelease(IntPtr value);
        [DllImport(Library)] private static extern long CFStringGetLength(IntPtr value);
        [DllImport(Library)] private static extern void CFStringGetCharacters(IntPtr value, CFRange range, char* buffer);
        [DllImport(Library)] private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, long length);

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRange(long location, long length)
        {
            public long Location = location, Length = length;
        }

        public static string ToManaged(IntPtr value)
        {
            var length = (int) CFStringGetLength(value);
            var chars = new char[length];
            fixed (char* p = chars) CFStringGetCharacters(value, new CFRange(0, length), p);
            return new string(chars);
        }

        public static IntPtr CreateString(string value)
        {
            fixed (char* p = value) return CFStringCreateWithCharacters(IntPtr.Zero, p, value.Length);
        }
    }
}
