using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;

namespace FModelMac.Rendering;

// The slice of OpenGL 3.2+ core the 3D viewer needs, resolved through Avalonia's context.
// Avalonia's GlInterface only wraps a handful of calls, so everything is loaded the same way.
internal sealed unsafe class Gl
{
    public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
    public const int DEPTH_TEST = 0x0B71, CULL_FACE = 0x0B44, BLEND = 0x0BE2, MULTISAMPLE = 0x809D;
    public const int LEQUAL = 0x0203, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4;
    public const int FLOAT = 0x1406, UNSIGNED_INT = 0x1405, UNSIGNED_BYTE = 0x1401;
    public const int TRIANGLES = 0x0004, LINES = 0x0001;
    public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const int TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, RGBA = 0x1908, RGBA8 = 0x8058, RGBA32F = 0x8814;
    public const int TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
    public const int LINEAR = 0x2601, NEAREST = 0x2600, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901;
    public const int FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41;
    public const int COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, DEPTH_COMPONENT24 = 0x81A6, FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const int FRONT_AND_BACK = 0x0408, LINE = 0x1B01, FILL = 0x1B02;
    public const int VERSION = 0x1F02, RENDERER = 0x1F01, MAX_SAMPLES = 0x8D57, PACK_ALIGNMENT = 0x0D05, UNPACK_ALIGNMENT = 0x0CF5;

    private readonly delegate* unmanaged<float, float, float, float, void> _clearColor;
    private readonly delegate* unmanaged<int, void> _clear, _enable, _disable, _depthFunc, _activeTexture, _useProgram,
        _deleteProgram, _deleteShader, _compileShader, _linkProgram, _bindVertexArray, _enableVertexAttribArray;
    private readonly delegate* unmanaged<int, int, int, int, void> _viewport;
    private readonly delegate* unmanaged<int, int, void> _blendFunc, _polygonMode, _bindBuffer, _bindTexture, _attachShader,
        _bindFramebuffer, _bindRenderbuffer, _pixelStorei, _uniform1i;
    private readonly delegate* unmanaged<int, int*, void> _genVertexArrays, _genBuffers, _genTextures, _genFramebuffers,
        _genRenderbuffers, _deleteVertexArrays, _deleteBuffers, _deleteTextures, _deleteFramebuffers, _deleteRenderbuffers, _getIntegerv;
    private readonly delegate* unmanaged<int, nint, void*, int, void> _bufferData;
    private readonly delegate* unmanaged<int, int, int, byte, int, nint, void> _vertexAttribPointer;
    private readonly delegate* unmanaged<int, int> _createShader, _checkFramebufferStatus;
    private readonly delegate* unmanaged<int> _createProgram, _getError;
    private readonly delegate* unmanaged<int, int, byte**, int*, void> _shaderSource;
    private readonly delegate* unmanaged<int, int, int*, void> _getShaderiv, _getProgramiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getShaderInfoLog, _getProgramInfoLog;
    private readonly delegate* unmanaged<int, int, byte*, void> _bindAttribLocation;
    private readonly delegate* unmanaged<int, byte*, int> _getUniformLocation;
    private readonly delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, int, int, int, void> _uniform3i;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, float, void> _uniform1f;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private readonly delegate* unmanaged<int, int, int, void> _texParameteri, _drawArrays;
    private readonly delegate* unmanaged<int, void> _generateMipmap;
    private readonly delegate* unmanaged<int, int, int, nint, void> _drawElements;
    private readonly delegate* unmanaged<int, int, int, int, void> _framebufferRenderbuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, void> _renderbufferStorageMultisample;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void> _blitFramebuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;
    private readonly delegate* unmanaged<int, byte*> _getString;
    private readonly delegate* unmanaged<int, int, void> _vertexAttribDivisor;
    private readonly delegate* unmanaged<int, int, int, nint, int, void> _drawElementsInstanced;

    public Gl(GlInterface gl)
    {
        nint Load(string name)
        {
            var proc = gl.GetProcAddress(name);
            return proc != 0 ? proc : throw new EntryPointNotFoundException($"OpenGL function {name} is not available");
        }

        _clearColor = (delegate* unmanaged<float, float, float, float, void>) Load("glClearColor");
        _clear = (delegate* unmanaged<int, void>) Load("glClear");
        _enable = (delegate* unmanaged<int, void>) Load("glEnable");
        _disable = (delegate* unmanaged<int, void>) Load("glDisable");
        _depthFunc = (delegate* unmanaged<int, void>) Load("glDepthFunc");
        _activeTexture = (delegate* unmanaged<int, void>) Load("glActiveTexture");
        _useProgram = (delegate* unmanaged<int, void>) Load("glUseProgram");
        _deleteProgram = (delegate* unmanaged<int, void>) Load("glDeleteProgram");
        _deleteShader = (delegate* unmanaged<int, void>) Load("glDeleteShader");
        _compileShader = (delegate* unmanaged<int, void>) Load("glCompileShader");
        _linkProgram = (delegate* unmanaged<int, void>) Load("glLinkProgram");
        _bindVertexArray = (delegate* unmanaged<int, void>) Load("glBindVertexArray");
        _enableVertexAttribArray = (delegate* unmanaged<int, void>) Load("glEnableVertexAttribArray");
        _viewport = (delegate* unmanaged<int, int, int, int, void>) Load("glViewport");
        _blendFunc = (delegate* unmanaged<int, int, void>) Load("glBlendFunc");
        _polygonMode = (delegate* unmanaged<int, int, void>) Load("glPolygonMode");
        _bindBuffer = (delegate* unmanaged<int, int, void>) Load("glBindBuffer");
        _bindTexture = (delegate* unmanaged<int, int, void>) Load("glBindTexture");
        _attachShader = (delegate* unmanaged<int, int, void>) Load("glAttachShader");
        _bindFramebuffer = (delegate* unmanaged<int, int, void>) Load("glBindFramebuffer");
        _bindRenderbuffer = (delegate* unmanaged<int, int, void>) Load("glBindRenderbuffer");
        _pixelStorei = (delegate* unmanaged<int, int, void>) Load("glPixelStorei");
        _uniform1i = (delegate* unmanaged<int, int, void>) Load("glUniform1i");
        _genVertexArrays = (delegate* unmanaged<int, int*, void>) Load("glGenVertexArrays");
        _genBuffers = (delegate* unmanaged<int, int*, void>) Load("glGenBuffers");
        _genTextures = (delegate* unmanaged<int, int*, void>) Load("glGenTextures");
        _genFramebuffers = (delegate* unmanaged<int, int*, void>) Load("glGenFramebuffers");
        _genRenderbuffers = (delegate* unmanaged<int, int*, void>) Load("glGenRenderbuffers");
        _deleteVertexArrays = (delegate* unmanaged<int, int*, void>) Load("glDeleteVertexArrays");
        _deleteBuffers = (delegate* unmanaged<int, int*, void>) Load("glDeleteBuffers");
        _deleteTextures = (delegate* unmanaged<int, int*, void>) Load("glDeleteTextures");
        _deleteFramebuffers = (delegate* unmanaged<int, int*, void>) Load("glDeleteFramebuffers");
        _deleteRenderbuffers = (delegate* unmanaged<int, int*, void>) Load("glDeleteRenderbuffers");
        _getIntegerv = (delegate* unmanaged<int, int*, void>) Load("glGetIntegerv");
        _bufferData = (delegate* unmanaged<int, nint, void*, int, void>) Load("glBufferData");
        _vertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, nint, void>) Load("glVertexAttribPointer");
        _createShader = (delegate* unmanaged<int, int>) Load("glCreateShader");
        _checkFramebufferStatus = (delegate* unmanaged<int, int>) Load("glCheckFramebufferStatus");
        _createProgram = (delegate* unmanaged<int>) Load("glCreateProgram");
        _getError = (delegate* unmanaged<int>) Load("glGetError");
        _shaderSource = (delegate* unmanaged<int, int, byte**, int*, void>) Load("glShaderSource");
        _getShaderiv = (delegate* unmanaged<int, int, int*, void>) Load("glGetShaderiv");
        _getProgramiv = (delegate* unmanaged<int, int, int*, void>) Load("glGetProgramiv");
        _getShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>) Load("glGetShaderInfoLog");
        _getProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>) Load("glGetProgramInfoLog");
        _bindAttribLocation = (delegate* unmanaged<int, int, byte*, void>) Load("glBindAttribLocation");
        _getUniformLocation = (delegate* unmanaged<int, byte*, int>) Load("glGetUniformLocation");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>) Load("glUniformMatrix4fv");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>) Load("glUniform3f");
        _uniform3i = (delegate* unmanaged<int, int, int, int, void>) Load("glUniform3i");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>) Load("glUniform4f");
        _uniform1f = (delegate* unmanaged<int, float, void>) Load("glUniform1f");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>) Load("glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>) Load("glTexParameteri");
        _drawArrays = (delegate* unmanaged<int, int, int, void>) Load("glDrawArrays");
        _generateMipmap = (delegate* unmanaged<int, void>) Load("glGenerateMipmap");
        _drawElements = (delegate* unmanaged<int, int, int, nint, void>) Load("glDrawElements");
        _framebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>) Load("glFramebufferRenderbuffer");
        _renderbufferStorageMultisample = (delegate* unmanaged<int, int, int, int, int, void>) Load("glRenderbufferStorageMultisample");
        _blitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void>) Load("glBlitFramebuffer");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>) Load("glReadPixels");
        _getString = (delegate* unmanaged<int, byte*>) Load("glGetString");
        _vertexAttribDivisor = (delegate* unmanaged<int, int, void>) Load("glVertexAttribDivisor");
        _drawElementsInstanced = (delegate* unmanaged<int, int, int, nint, int, void>) Load("glDrawElementsInstanced");
    }

    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void Clear(int mask) => _clear(mask);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void DepthFunc(int func) => _depthFunc(func);
    public void BlendFunc(int src, int dst) => _blendFunc(src, dst);
    public void PolygonMode(int face, int mode) => _polygonMode(face, mode);
    public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public void PixelStore(int name, int value) => _pixelStorei(name, value);
    public int GetError() => _getError();

    public int GetInteger(int name)
    {
        int value;
        _getIntegerv(name, &value);
        return value;
    }

    public string GetString(int name) => Marshal.PtrToStringUTF8((nint) _getString(name)) ?? "";

    // ---------- Buffers / vertex arrays ----------

    public int GenVertexArray() { int id; _genVertexArrays(1, &id); return id; }
    public int GenBuffer() { int id; _genBuffers(1, &id); return id; }
    public void BindVertexArray(int id) => _bindVertexArray(id);
    public void BindBuffer(int target, int id) => _bindBuffer(target, id);
    public void DeleteVertexArray(int id) { if (id != 0) _deleteVertexArrays(1, &id); }
    public void DeleteBuffer(int id) { if (id != 0) _deleteBuffers(1, &id); }

    public void BufferData<T>(int target, ReadOnlySpan<T> data) where T : unmanaged
    {
        fixed (T* ptr = data) _bufferData(target, data.Length * sizeof(T), ptr, STATIC_DRAW);
    }

    public void VertexAttribPointer(int index, int size, int strideBytes, int offsetBytes)
    {
        _vertexAttribPointer(index, size, FLOAT, 0, strideBytes, offsetBytes);
        _enableVertexAttribArray(index);
    }

    public void DrawElements(int mode, int count, int firstIndex) => _drawElements(mode, count, UNSIGNED_INT, firstIndex * sizeof(uint));
    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);

    public void DrawElementsInstanced(int mode, int count, int firstIndex, int instances) =>
        _drawElementsInstanced(mode, count, UNSIGNED_INT, firstIndex * sizeof(uint), instances);

    /// <summary>A mat4 attribute fed per instance: 4 consecutive vec4 locations starting at <paramref name="index"/>.</summary>
    public void InstanceMatrixAttribute(int index)
    {
        const int stride = 16 * sizeof(float);
        for (var i = 0; i < 4; i++)
        {
            VertexAttribPointer(index + i, 4, stride, i * 4 * sizeof(float));
            _vertexAttribDivisor(index + i, 1);
        }
    }

    // ---------- Textures ----------

    public int GenTexture() { int id; _genTextures(1, &id); return id; }
    public void BindTexture(int target, int id) => _bindTexture(target, id);
    public void ActiveTexture(int unit) => _activeTexture(unit);
    public void DeleteTexture(int id) { if (id != 0) _deleteTextures(1, &id); }
    public void TexParameter(int target, int name, int value) => _texParameteri(target, name, value);
    public void GenerateMipmap(int target) => _generateMipmap(target);

    public void TexImage2D(int width, int height, ReadOnlySpan<byte> rgba)
    {
        fixed (byte* ptr = rgba) _texImage2D(TEXTURE_2D, 0, RGBA8, width, height, 0, RGBA, UNSIGNED_BYTE, ptr);
    }

    public void TexImage2DFloat(int width, int height, ReadOnlySpan<float> rgba)
    {
        fixed (float* ptr = rgba) _texImage2D(TEXTURE_2D, 0, RGBA32F, width, height, 0, RGBA, FLOAT, ptr);
    }

    // ---------- Framebuffers ----------

    public int GenFramebuffer() { int id; _genFramebuffers(1, &id); return id; }
    public int GenRenderbuffer() { int id; _genRenderbuffers(1, &id); return id; }
    public void BindFramebuffer(int target, int id) => _bindFramebuffer(target, id);
    public void BindRenderbuffer(int id) => _bindRenderbuffer(RENDERBUFFER, id);
    public void DeleteFramebuffer(int id) { if (id != 0) _deleteFramebuffers(1, &id); }
    public void DeleteRenderbuffer(int id) { if (id != 0) _deleteRenderbuffers(1, &id); }
    public void RenderbufferStorageMultisample(int samples, int format, int w, int h) => _renderbufferStorageMultisample(RENDERBUFFER, samples, format, w, h);
    public void FramebufferRenderbuffer(int attachment, int renderbuffer) => _framebufferRenderbuffer(FRAMEBUFFER, attachment, RENDERBUFFER, renderbuffer);
    public int CheckFramebufferStatus() => _checkFramebufferStatus(FRAMEBUFFER);

    public void BlitFramebuffer(int w, int h) => _blitFramebuffer(0, 0, w, h, 0, 0, w, h, COLOR_BUFFER_BIT, NEAREST);

    public void ReadPixels(int w, int h, Span<byte> rgba)
    {
        fixed (byte* ptr = rgba) _readPixels(0, 0, w, h, RGBA, UNSIGNED_BYTE, ptr);
    }

    // ---------- Shaders ----------

    public int CompileProgram(string vertex, string fragment, params string[] attributes)
    {
        var vs = Compile(VERTEX_SHADER, vertex);
        var fs = Compile(FRAGMENT_SHADER, fragment);
        var program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        for (var i = 0; i < attributes.Length; i++)
        {
            var name = Encoding.UTF8.GetBytes(attributes[i] + '\0');
            fixed (byte* ptr = name) _bindAttribLocation(program, i, ptr);
        }
        _linkProgram(program);
        _deleteShader(vs);
        _deleteShader(fs);

        int ok;
        _getProgramiv(program, LINK_STATUS, &ok);
        if (ok != 0) return program;

        var log = InfoLog(program, _getProgramInfoLog);
        _deleteProgram(program);
        throw new InvalidOperationException($"Could not link the viewer shaders: {log}");
    }

    public void DeleteProgram(int id) { if (id != 0) _deleteProgram(id); }
    public void UseProgram(int id) => _useProgram(id);

    public int UniformLocation(int program, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* ptr = bytes) return _getUniformLocation(program, ptr);
    }

    public void UniformMatrix(int location, System.Numerics.Matrix4x4 m) => _uniformMatrix4fv(location, 1, 0, &m.M11);
    public void Uniform(int location, float x, float y, float z) => _uniform3f(location, x, y, z);
    public void Uniform(int location, float x, float y, float z, float w) => _uniform4f(location, x, y, z, w);
    public void Uniform(int location, float x) => _uniform1f(location, x);
    public void Uniform(int location, int x) => _uniform1i(location, x);
    public void Uniform(int location, int x, int y, int z) => _uniform3i(location, x, y, z);

    private int Compile(int type, string source)
    {
        var shader = _createShader(type);
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* ptr = bytes)
        {
            var length = bytes.Length;
            var sources = ptr;
            _shaderSource(shader, 1, &sources, &length);
        }
        _compileShader(shader);

        int ok;
        _getShaderiv(shader, COMPILE_STATUS, &ok);
        if (ok != 0) return shader;

        var log = InfoLog(shader, _getShaderInfoLog);
        _deleteShader(shader);
        throw new InvalidOperationException($"Could not compile the viewer shader: {log}");
    }

    private static string InfoLog(int id, delegate* unmanaged<int, int, int*, byte*, void> getLog)
    {
        var buffer = new byte[4096];
        int length;
        fixed (byte* ptr = buffer) getLog(id, buffer.Length, &length, ptr);
        return Encoding.UTF8.GetString(buffer, 0, Math.Clamp(length, 0, buffer.Length));
    }
}
