using System.Runtime.InteropServices;

namespace FModelMac.Services.Video;

/// <summary>
/// Writes an H.264 video (MP4, or QuickTime for .mov) with macOS's own encoder, AVFoundation's AVAssetWriter, reached
/// through the Objective-C runtime: frames are BGRA pixels, top row first, appended in order at a fixed frame rate;
/// an optional soundtrack (48 kHz stereo float samples) is encoded to AAC alongside, interleaved with the frames.
/// Calls must come one at a time (any thread).
/// </summary>
public sealed unsafe class VideoWriter : IDisposable
{
    public const int AudioRate = 48_000;
    private const int AudioChunk = 4_800; // 0.1 s per audio sample buffer

    private IntPtr _writer, _videoInput, _adaptor, _audioInput, _audioFormat, _pixelAttributes;
    private readonly int _width, _height, _fps;
    private readonly float[]? _audio; // interleaved stereo
    private long _audioWritten;       // frames (sample pairs) appended
    private bool _audioFinished;
    private int _frames;
    private readonly string _path;

    public int Width => _width;
    public int Height => _height;

    /// <param name="audio">Interleaved stereo samples at <see cref="AudioRate"/>, or null for a silent video.</param>
    public VideoWriter(string path, int width, int height, int fps, float[]? audio)
    {
        if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("H.264 needs an even width and height");
        (_path, _width, _height, _fps, _audio) = (path, width, height, fps, audio is { Length: > 0 } ? audio : null);
        AV.Load();
        File.Delete(path); // AVAssetWriter won't replace a file

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var url = ObjC.Send(ObjC.Class("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.String(path));
            var fileType = path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ? AV.Constant("AVFileTypeQuickTimeMovie") : AV.Constant("AVFileTypeMPEG4");
            IntPtr error;
            _writer = ObjC.Send(ObjC.Class("AVAssetWriter"), ObjC.Sel("assetWriterWithURL:fileType:error:"), url, fileType, &error);
            if (_writer == IntPtr.Zero) throw new IOException($"Can't write {Path.GetFileName(path)}: {ObjC.Describe(error)}");
            ObjC.Retain(_writer);

            // H.264 at a bit rate that keeps fast motion and particles clean (~0.12 bit per pixel per frame)
            var bitRate = Math.Clamp((long) width * height * fps / 8, 2_000_000, 80_000_000);
            var compression = ObjC.Dictionary(
                (AV.Constant("AVVideoAverageBitRateKey"), ObjC.Number(bitRate)),
                (AV.Constant("AVVideoMaxKeyFrameIntervalKey"), ObjC.Number(fps * 2)),
                (AV.Constant("AVVideoProfileLevelKey"), AV.Constant("AVVideoProfileLevelH264HighAutoLevel")));
            var videoSettings = ObjC.Dictionary(
                (AV.Constant("AVVideoCodecKey"), AV.Constant("AVVideoCodecTypeH264")),
                (AV.Constant("AVVideoWidthKey"), ObjC.Number(width)),
                (AV.Constant("AVVideoHeightKey"), ObjC.Number(height)),
                (AV.Constant("AVVideoCompressionPropertiesKey"), compression));
            _videoInput = ObjC.Retain(ObjC.Send(ObjC.Class("AVAssetWriterInput"), ObjC.Sel("assetWriterInputWithMediaType:outputSettings:"),
                AV.Constant("AVMediaTypeVideo"), videoSettings));
            ObjC.SendVoid(_videoInput, ObjC.Sel("setExpectsMediaDataInRealTime:"), 0);
            _adaptor = ObjC.Retain(ObjC.Send(ObjC.Class("AVAssetWriterInputPixelBufferAdaptor"),
                ObjC.Sel("assetWriterInputPixelBufferAdaptorWithAssetWriterInput:sourcePixelBufferAttributes:"), _videoInput, IntPtr.Zero));
            AddInput(_videoInput);

            if (_audio is not null)
            {
                var audioSettings = ObjC.Dictionary(
                    (AV.Constant("AVFormatIDKey"), ObjC.Number(0x61616320)), // kAudioFormatMPEG4AAC 'aac '
                    (AV.Constant("AVSampleRateKey"), ObjC.Number(AudioRate)),
                    (AV.Constant("AVNumberOfChannelsKey"), ObjC.Number(2)),
                    (AV.Constant("AVEncoderBitRateKey"), ObjC.Number(192_000)));
                _audioInput = ObjC.Retain(ObjC.Send(ObjC.Class("AVAssetWriterInput"), ObjC.Sel("assetWriterInputWithMediaType:outputSettings:"),
                    AV.Constant("AVMediaTypeAudio"), audioSettings));
                ObjC.SendVoid(_audioInput, ObjC.Sel("setExpectsMediaDataInRealTime:"), 0);
                AddInput(_audioInput);

                var format = new AudioStreamBasicDescription
                {
                    SampleRate = AudioRate, FormatID = 0x6C70636D, FormatFlags = 1 | 8, // 'lpcm', float, packed
                    BytesPerPacket = 8, FramesPerPacket = 1, BytesPerFrame = 8, ChannelsPerFrame = 2, BitsPerChannel = 32
                };
                Check(CM.CMAudioFormatDescriptionCreate(IntPtr.Zero, &format, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out _audioFormat),
                    "CMAudioFormatDescriptionCreate");
            }

            // Pixel buffers backed by IOSurface, which the hardware encoder reads without a copy
            _pixelAttributes = ObjC.Retain(ObjC.Dictionary((CV.Constant("kCVPixelBufferIOSurfacePropertiesKey"), ObjC.Dictionary())));

            if (!ObjC.SendBool(_writer, ObjC.Sel("startWriting"))) throw new IOException($"Can't start the video: {WriterError()}");
            ObjC.SendVoid(_writer, ObjC.Sel("startSessionAtSourceTime:"), CMTime.Zero);
        }
        catch
        {
            ReleaseAll();
            throw;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private void AddInput(IntPtr input)
    {
        if (!ObjC.SendBool(_writer, ObjC.Sel("canAddInput:"), input)) throw new IOException("The video writer refused a track");
        ObjC.SendVoid(_writer, ObjC.Sel("addInput:"), input);
    }

    /// <summary>Appends the next frame: <see cref="Width"/> × <see cref="Height"/> BGRA pixels, top row first.</summary>
    public void AppendFrame(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < _width * _height * 4) throw new ArgumentException("Frame too small");
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            WaitReady(_videoInput);
            Check(CV.CVPixelBufferCreate(IntPtr.Zero, _width, _height, 0x42475241 /* 'BGRA' */, _pixelAttributes, out var buffer), "CVPixelBufferCreate");
            try
            {
                CV.CVPixelBufferLockBaseAddress(buffer, 0);
                var target = (byte*) CV.CVPixelBufferGetBaseAddress(buffer);
                var stride = (int) CV.CVPixelBufferGetBytesPerRow(buffer);
                var row = _width * 4;
                fixed (byte* source = bgra)
                    for (var y = 0; y < _height; y++)
                        Buffer.MemoryCopy(source + y * row, target + y * stride, row, row);
                CV.CVPixelBufferUnlockBaseAddress(buffer, 0);

                var time = new CMTime(_frames, _fps);
                if (!ObjC.SendBool(_adaptor, ObjC.Sel("appendPixelBuffer:withPresentationTime:"), buffer, time))
                    throw new IOException($"Can't add frame {_frames}: {WriterError()}");
            }
            finally
            {
                CF.CFRelease(buffer);
            }
            _frames++;

            // The soundtrack up to this frame's end, so the encoder gets both tracks interleaved
            AppendAudioUntil((long) _frames * AudioRate / _fps);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private void AppendAudioUntil(long frame)
    {
        if (_audio is null) return;
        frame = Math.Min(frame, _audio.Length / 2);
        while (_audioWritten < frame)
        {
            WaitReady(_audioInput);
            AppendAudioChunk();
        }
    }

    // The next 0.1 s of the soundtrack
    private void AppendAudioChunk()
    {
        var total = _audio!.Length / 2;
        var count = (int) Math.Min(AudioChunk, total - _audioWritten);
        if (count <= 0) return;
        var bytes = count * 8;
        Check(CM.CMBlockBufferCreateWithMemoryBlock(IntPtr.Zero, IntPtr.Zero, (nuint) bytes, IntPtr.Zero, IntPtr.Zero, 0, (nuint) bytes,
            1 /* kCMBlockBufferAssureMemoryNowFlag */, out var block), "CMBlockBufferCreateWithMemoryBlock");
        try
        {
            fixed (float* samples = &_audio[_audioWritten * 2])
                Check(CM.CMBlockBufferReplaceDataBytes(samples, block, 0, (nuint) bytes), "CMBlockBufferReplaceDataBytes");
            Check(CM.CMAudioSampleBufferCreateReadyWithPacketDescriptions(IntPtr.Zero, block, _audioFormat, count,
                new CMTime(_audioWritten, AudioRate), IntPtr.Zero, out var sample), "CMAudioSampleBufferCreateReadyWithPacketDescriptions");
            try
            {
                if (!ObjC.SendBool(_audioInput, ObjC.Sel("appendSampleBuffer:"), sample))
                    throw new IOException($"Can't add the soundtrack: {WriterError()}");
            }
            finally
            {
                CF.CFRelease(sample);
            }
        }
        finally
        {
            CF.CFRelease(block);
        }
        _audioWritten += count;

        // All of it in: the writer stops waiting for more sound to interleave with the remaining frames
        if (_audioWritten >= total) FinishAudio();
    }

    private void FinishAudio()
    {
        if (_audioInput == IntPtr.Zero || _audioFinished) return;
        ObjC.SendVoid(_audioInput, ObjC.Sel("markAsFinished"));
        _audioFinished = true;
    }

    // Without real-time input the encoder takes data as fast as it can; it only asks to wait while it catches up
    private void WaitReady(IntPtr input)
    {
        var start = Environment.TickCount64;
        while (!ObjC.SendBool(input, ObjC.Sel("isReadyForMoreMediaData")))
        {
            // The writer interleaves the tracks: frames can wait for sound a bit ahead of them
            if (input == _videoInput && _audio is not null && _audioWritten < _audio.Length / 2 &&
                ObjC.SendBool(_audioInput, ObjC.Sel("isReadyForMoreMediaData")))
            {
                AppendAudioChunk();
                continue;
            }
            if (ObjC.SendLong(_writer, ObjC.Sel("status")) == 3) throw new IOException($"The video failed: {WriterError()}");
            if (Environment.TickCount64 - start > 30_000)
                throw new TimeoutException($"The video encoder stopped taking {(input == _audioInput ? "sound" : "frames")} " +
                                           $"(frame {_frames}, sound {_audioWritten / (double) AudioRate:0.00} s, status {ObjC.SendLong(_writer, ObjC.Sel("status"))})");
            Thread.Sleep(2);
        }
    }

    /// <summary>Writes what's left of the soundtrack (up to the video's end) and closes the file.</summary>
    public void Finish()
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            AppendAudioUntil((long) _frames * AudioRate / _fps);
            ObjC.SendVoid(_videoInput, ObjC.Sel("markAsFinished"));
            FinishAudio();
            ObjC.SendVoid(_writer, ObjC.Sel("endSessionAtSourceTime:"), new CMTime(_frames, _fps));

            // finishWritingWithCompletionHandler: takes a block; the synchronous call does the same and is still there
            if (!ObjC.SendBool(_writer, ObjC.Sel("finishWriting")) || ObjC.SendLong(_writer, ObjC.Sel("status")) != 2)
                throw new IOException($"Can't finish the video: {WriterError()}");
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
            ReleaseAll();
        }
    }

    /// <summary>Stops without finishing and deletes the partial file.</summary>
    public void Cancel()
    {
        if (_writer != IntPtr.Zero && ObjC.SendLong(_writer, ObjC.Sel("status")) == 1) ObjC.SendVoid(_writer, ObjC.Sel("cancelWriting"));
        ReleaseAll();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_writer != IntPtr.Zero) Cancel();
    }

    private string WriterError() => _writer == IntPtr.Zero ? "unknown error" : ObjC.Describe(ObjC.Send(_writer, ObjC.Sel("error")));

    private void ReleaseAll()
    {
        foreach (var obj in new[] { _adaptor, _videoInput, _audioInput, _writer, _pixelAttributes })
            if (obj != IntPtr.Zero) ObjC.Release(obj);
        if (_audioFormat != IntPtr.Zero) CF.CFRelease(_audioFormat);
        _adaptor = _videoInput = _audioInput = _writer = _pixelAttributes = _audioFormat = IntPtr.Zero;
    }

    private static void Check(int status, string call)
    {
        if (status != 0) throw new IOException($"{call} failed ({status})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CMTime(long value, int timescale)
    {
        public readonly long Value = value;
        public readonly int Timescale = timescale;
        public readonly uint Flags = 1; // kCMTimeFlags_Valid
        public readonly long Epoch = 0;

        public static CMTime Zero => new(0, 1);
    }

    // ---------- Native APIs ----------

    private static class AV
    {
        private static IntPtr _avFoundation, _avfAudio;

        public static void Load()
        {
            if (_avFoundation != IntPtr.Zero) return;
            _avFoundation = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            NativeLibrary.TryLoad("/System/Library/Frameworks/AVFAudio.framework/AVFAudio", out _avfAudio);
        }

        /// <summary>An NSString constant exported by AVFoundation (or AVFAudio, where the audio settings keys live).</summary>
        public static IntPtr Constant(string name)
        {
            if (NativeLibrary.TryGetExport(_avFoundation, name, out var address) ||
                _avfAudio != IntPtr.Zero && NativeLibrary.TryGetExport(_avfAudio, name, out address))
                return Marshal.ReadIntPtr(address);
            throw new EntryPointNotFoundException($"AVFoundation has no {name}");
        }
    }

    private static class CV
    {
        private const string Lib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

        public static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Lib), name));

        [DllImport(Lib)] public static extern int CVPixelBufferCreate(IntPtr allocator, nint width, nint height, uint format, IntPtr attributes, out IntPtr buffer);
        [DllImport(Lib)] public static extern int CVPixelBufferLockBaseAddress(IntPtr buffer, ulong flags);
        [DllImport(Lib)] public static extern int CVPixelBufferUnlockBaseAddress(IntPtr buffer, ulong flags);
        [DllImport(Lib)] public static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr buffer);
        [DllImport(Lib)] public static extern nint CVPixelBufferGetBytesPerRow(IntPtr buffer);
    }

    private static class CM
    {
        private const string Lib = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";

        [DllImport(Lib)]
        public static extern int CMAudioFormatDescriptionCreate(IntPtr allocator, AudioStreamBasicDescription* format, nuint layoutSize, IntPtr layout,
            nuint cookieSize, IntPtr cookie, IntPtr extensions, out IntPtr description);

        [DllImport(Lib)]
        public static extern int CMBlockBufferCreateWithMemoryBlock(IntPtr allocator, IntPtr memory, nuint length, IntPtr blockAllocator,
            IntPtr customSource, nuint offset, nuint dataLength, uint flags, out IntPtr block);

        [DllImport(Lib)] public static extern int CMBlockBufferReplaceDataBytes(void* source, IntPtr block, nuint offset, nuint length);

        [DllImport(Lib)]
        public static extern int CMAudioSampleBufferCreateReadyWithPacketDescriptions(IntPtr allocator, IntPtr block, IntPtr format, nint samples,
            CMTime time, IntPtr packetDescriptions, out IntPtr sample);
    }

    private static class CF
    {
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern void CFRelease(IntPtr obj);
    }

    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
        [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
        [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
        [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
        [DllImport(Lib)] private static extern IntPtr objc_retain(IntPtr obj);
        [DllImport(Lib)] private static extern void objc_release(IntPtr obj);

        // objc_msgSend isn't variadic on arm64: each argument list gets its own exact signature
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr* error);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr* objects, IntPtr* keys, nuint count);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendLongArg(IntPtr receiver, IntPtr selector, long value);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern long SendLong(IntPtr receiver, IntPtr selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, byte a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr receiver, IntPtr selector, CMTime a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr a);
        [DllImport(Lib, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool SendBool(IntPtr receiver, IntPtr selector, IntPtr a, CMTime b);

        public static IntPtr Class(string name) => objc_getClass(name) is var cls && cls != IntPtr.Zero ? cls : throw new EntryPointNotFoundException($"No class {name}");
        public static IntPtr Sel(string name) => sel_registerName(name);
        public static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : objc_retain(obj);
        public static void Release(IntPtr obj) => objc_release(obj);
        public static IntPtr String(string value) => SendString(Class("NSString"), Sel("stringWithUTF8String:"), value);
        public static IntPtr Number(long value) => SendLongArg(Class("NSNumber"), Sel("numberWithLongLong:"), value);

        public static IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
        {
            var keys = stackalloc IntPtr[Math.Max(1, entries.Length)];
            var values = stackalloc IntPtr[Math.Max(1, entries.Length)];
            for (var i = 0; i < entries.Length; i++) (keys[i], values[i]) = entries[i];
            return Send(Class("NSDictionary"), Sel("dictionaryWithObjects:forKeys:count:"), values, keys, (nuint) entries.Length);
        }

        /// <summary>An NSError's (or any object's) description, as text.</summary>
        public static string Describe(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return "unknown error";
            var description = Send(obj, Sel("description"));
            return Marshal.PtrToStringUTF8(SendUtf8(description, Sel("UTF8String"))) ?? "unknown error";
        }
    }
}
