using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using SkiaSharp;

namespace FModelMac.Rendering;

/// <summary>
/// OpenGL 3.2 core viewport for a <see cref="ViewerScene"/>: lit, textured meshes (instanced, so a map's
/// thousands of placements cost one draw per mesh) over a ground grid, with Snooper's orbit and fly cameras
/// (see Input). Only redraws when something changes.
/// </summary>
public sealed class ModelViewport : OpenGlControlBase, ICustomHitTest
{
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<ModelViewport, bool>(nameof(ShowGrid), true);
    public static readonly StyledProperty<bool> ShowTexturesProperty =
        AvaloniaProperty.Register<ModelViewport, bool>(nameof(ShowTextures), true);
    /// <summary>Normal maps and roughness/metallic masks (off: the flat diffuse look, to compare).</summary>
    public static readonly StyledProperty<bool> ShowNormalMapsProperty =
        AvaloniaProperty.Register<ModelViewport, bool>(nameof(ShowNormalMaps), true);
    public static readonly StyledProperty<bool> WireframeProperty =
        AvaloniaProperty.Register<ModelViewport, bool>(nameof(Wireframe));
    public static readonly StyledProperty<bool> ShowBonesProperty =
        AvaloniaProperty.Register<ModelViewport, bool>(nameof(ShowBones));
    public static readonly StyledProperty<CameraMode> CameraModeProperty =
        AvaloniaProperty.Register<ModelViewport, CameraMode>(nameof(CameraMode));
    public static readonly StyledProperty<float> AnimationPhaseProperty =
        AvaloniaProperty.Register<ModelViewport, float>(nameof(AnimationPhase));
    public static readonly StyledProperty<SceneItem?> SelectedProperty =
        AvaloniaProperty.Register<ModelViewport, SceneItem?>(nameof(Selected));

    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowTextures { get => GetValue(ShowTexturesProperty); set => SetValue(ShowTexturesProperty, value); }
    public bool ShowNormalMaps { get => GetValue(ShowNormalMapsProperty); set => SetValue(ShowNormalMapsProperty, value); }
    public bool Wireframe { get => GetValue(WireframeProperty); set => SetValue(WireframeProperty, value); }
    public bool ShowBones { get => GetValue(ShowBonesProperty); set => SetValue(ShowBonesProperty, value); }
    /// <summary>Orbit around a target, or fly like in a game (FModel's Arcball / Fly Cam).</summary>
    public CameraMode CameraMode { get => GetValue(CameraModeProperty); set => SetValue(CameraModeProperty, value); }
    /// <summary>Position in the animation cycle, 0 to 1, for every object with a <see cref="SceneObject.Pose"/>.</summary>
    public float AnimationPhase { get => GetValue(AnimationPhaseProperty); set => SetValue(AnimationPhaseProperty, value); }
    /// <summary>Outlined in the scene; its skeleton is the one drawn with <see cref="ShowBones"/>.</summary>
    public SceneItem? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    public ViewerScene Scene { get; } = new();

    /// <summary>Set once the context is up: "OpenGL 4.1 (Apple M1)", or the reason it failed.</summary>
    public string? RendererInfo { get; private set; }
    public event EventHandler? RendererReady;

    private const string MeshVertex = """
        #version 150
        in vec3 aPosition;
        in vec3 aNormal;
        in vec2 aUv;
        in vec4 aBones;
        in vec4 aWeights;
        in vec4 aModel0; // per-instance transform, one row per attribute
        in vec4 aModel1;
        in vec4 aModel2;
        in vec4 aModel3;
        in vec4 aTangent; // xyz + the binormal's sign
        uniform mat4 uViewProjection;
        uniform int uSkinned;
        uniform sampler2D uBones; // one skinning matrix per row, 4 RGBA32F texels each (macOS GL has no SSBOs)
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vTangent;
        out vec3 vLocal;

        mat4 bone(float index)
        {
            int row = int(index + 0.5);
            return mat4(texelFetch(uBones, ivec2(0, row), 0), texelFetch(uBones, ivec2(1, row), 0),
                        texelFetch(uBones, ivec2(2, row), 0), texelFetch(uBones, ivec2(3, row), 0));
        }

        void main()
        {
            vec4 position = vec4(aPosition, 1.0);
            vLocal = aPosition;
            vec3 normal = aNormal;
            vec3 tangent = aTangent.xyz;
            float total = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
            if (uSkinned == 1 && total > 0.001)
            {
                mat4 skin = aWeights.x * bone(aBones.x) + aWeights.y * bone(aBones.y)
                          + aWeights.z * bone(aBones.z) + aWeights.w * bone(aBones.w);
                position = skin * position;
                normal = mat3(skin) * normal;
                tangent = mat3(skin) * tangent;
            }
            mat4 model = mat4(aModel0, aModel1, aModel2, aModel3);
            position = model * position;
            vWorld = position.xyz;
            vNormal = mat3(model) * normal;
            // A mirroring transform (negative scale) flips the binormal
            vTangent = vec4(mat3(model) * tangent, determinant(mat3(model)) < 0.0 ? -aTangent.w : aTangent.w);
            vUv = aUv;
            gl_Position = uViewProjection * position;
        }
        """;

    // Physically based enough to read materials: normal maps, packed roughness/metallic/occlusion masks, GGX
    // specular from a key light that follows the camera, a fill from the camera, a sky/ground ambient, then ACES
    // tone mapping. Textures are sRGB; lighting happens in linear space.
    private const string MeshFragment = """
        #version 150
        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vTangent;
        in vec3 vLocal;
        uniform sampler2D uTexture;
        uniform int uHasTexture;
        uniform vec3 uColor;
        uniform sampler2D uNormalMap;
        uniform int uHasNormalMap;
        uniform sampler2D uMaskMap;
        uniform int uHasMaskMap;
        uniform ivec3 uMaskLayout; // channels of roughness, metallic, occlusion (-1: none)
        uniform vec3 uCamera;
        uniform vec3 uKeyLight;    // toward the light
        uniform int uFlat;
        // Landscapes: four paint layers' weights (RGBA, at vUv) and their diffuse textures, each repeating every
        // uLayerTiling quads of the landscape's own space (0: no texture, that weight shows uTexture's blend instead)
        uniform int uLandscape;
        uniform sampler2D uWeights;
        uniform sampler2D uLayer0;
        uniform sampler2D uLayer1;
        uniform sampler2D uLayer2;
        uniform sampler2D uLayer3;
        uniform vec4 uLayerTiling;
        out vec4 fragColor;

        const float PI = 3.14159265;

        float channel(vec4 v, int i) { return i == 0 ? v.r : i == 1 ? v.g : i == 2 ? v.b : v.a; }

        vec3 aces(vec3 x)
        {
            return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
        }

        // GGX / Smith / Schlick, the usual UE-like BRDF
        vec3 light(vec3 n, vec3 v, vec3 l, vec3 albedo, float roughness, float metallic, vec3 radiance)
        {
            float nl = max(dot(n, l), 0.0);
            if (nl <= 0.0) return vec3(0.0);
            vec3 h = normalize(v + l);
            float nv = max(dot(n, v), 1e-4);
            float nh = max(dot(n, h), 0.0);
            float a = roughness * roughness;
            float a2 = a * a;
            float d = nh * nh * (a2 - 1.0) + 1.0;
            float ndf = a2 / (PI * d * d);
            float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
            float g = nv / (nv * (1.0 - k) + k) * nl / (nl * (1.0 - k) + k);
            vec3 f0 = mix(vec3(0.04), albedo, metallic);
            vec3 f = f0 + (1.0 - f0) * pow(1.0 - max(dot(h, v), 0.0), 5.0);
            vec3 specular = ndf * g * f / (4.0 * nv * nl + 1e-4);
            vec3 diffuse = (1.0 - f) * (1.0 - metallic) * albedo / PI;
            return (diffuse + specular) * radiance * nl;
        }

        void main()
        {
            if (uFlat == 1) { fragColor = vec4(uColor, 1.0); return; }

            vec3 base = uHasTexture == 1 ? texture(uTexture, vUv).rgb : uColor;
            if (uLandscape == 1)
            {
                vec4 w = texture(uWeights, vUv) * step(vec4(1e-4), uLayerTiling);
                vec2 p = vLocal.xz;
                vec3 layers = vec3(0.0);
                if (w.x > 0.0) layers += w.x * texture(uLayer0, p / uLayerTiling.x).rgb;
                if (w.y > 0.0) layers += w.y * texture(uLayer1, p / uLayerTiling.y).rgb;
                if (w.z > 0.0) layers += w.z * texture(uLayer2, p / uLayerTiling.z).rgb;
                if (w.w > 0.0) layers += w.w * texture(uLayer3, p / uLayerTiling.w).rgb;
                base = layers + clamp(1.0 - dot(w, vec4(1.0)), 0.0, 1.0) * base;
            }
            vec3 albedo = pow(base, vec3(2.2));
            vec3 v = normalize(uCamera - vWorld);
            vec3 n = dot(vNormal, vNormal) > 1e-6 ? normalize(vNormal) : v;
            bool back = dot(n, v) < 0.0; // game meshes are often two-sided

            if (uHasNormalMap == 1 && dot(vTangent.xyz, vTangent.xyz) > 1e-6)
            {
                vec3 t = vTangent.xyz - n * dot(n, vTangent.xyz);
                if (dot(t, t) > 1e-8)
                {
                    t = normalize(t);
                    // UE's binormal is cross(N, T)·sign; this space is mirrored (Y/Z swapped), hence the minus
                    vec3 b = -cross(n, t) * (vTangent.w < 0.0 ? -1.0 : 1.0);
                    vec2 xy = texture(uNormalMap, vUv).rg * 2.0 - 1.0;
                    float z = sqrt(max(1.0 - dot(xy, xy), 0.0)); // BC5 keeps only X and Y
                    n = normalize(t * xy.x + b * xy.y + n * z);
                }
            }
            if (back) n = -n;

            float roughness = 0.65, metallic = 0.0, occlusion = 1.0;
            if (uHasMaskMap == 1)
            {
                vec4 m = texture(uMaskMap, vUv);
                if (uMaskLayout.x >= 0) roughness = channel(m, uMaskLayout.x);
                if (uMaskLayout.y >= 0) metallic = channel(m, uMaskLayout.y);
                if (uMaskLayout.z >= 0) occlusion = channel(m, uMaskLayout.z);
            }
            roughness = clamp(roughness, 0.08, 1.0);

            vec3 color = light(n, v, normalize(uKeyLight), albedo, roughness, metallic, vec3(3.2))
                       + light(n, v, v, albedo, roughness, metallic, vec3(0.9)); // fill from the camera
            vec3 sky = mix(vec3(0.20, 0.19, 0.18), vec3(0.42, 0.46, 0.55), 0.5 + 0.5 * n.y);
            color += albedo * sky * (1.0 - 0.6 * metallic) * occlusion;

            fragColor = vec4(pow(aces(color), vec3(1.0 / 2.2)), 1.0);
        }
        """;

    private const string LineVertex = """
        #version 150
        in vec3 aPosition;
        in vec3 aColor;
        uniform mat4 uViewProjection;
        out vec3 vColor;
        out vec3 vWorld;
        void main()
        {
            vColor = aColor;
            vWorld = aPosition;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string LineFragment = """
        #version 150
        in vec3 vColor;
        in vec3 vWorld;
        uniform vec3 uFadeCenter;
        uniform float uExtent;
        out vec4 fragColor;
        void main()
        {
            float fade = 1.0 - smoothstep(0.35, 1.0, length(vWorld.xz - uFadeCenter.xz) / uExtent);
            fragColor = vec4(vColor, fade * 0.9);
        }
        """;

    // AdonisUI Layer0 (#2A2B34) so the viewport blends with the rest of the app
    private static readonly Vector3 Background = new(0x2A / 255f, 0x2B / 255f, 0x34 / 255f);
    private static readonly Vector3 Accent = new(0x20 / 255f, 0x6B / 255f, 0xD4 / 255f);

    /// <summary>GPU side of a scene object: its geometry, skinning data and instance transforms.</summary>
    private sealed class GpuObject
    {
        public int Vao, Vbo, SkinVbo, Ebo, InstanceVbo;
        public int InstanceVersion = -1, InstanceCount;
        public float[] MorphWeights = [];
        public float[]? MorphedVertices;
    }

    private Gl? _gl;
    private int _meshProgram, _lineProgram;
    private int _gridVao, _gridVbo, _gridVertexCount, _gridVersion = -1;
    private Vector3 _gridCenter;
    private float _gridExtent = 1;
    private int _linesVao, _linesVbo;
    private int _fbo, _colorRb, _depthRb, _fboWidth, _fboHeight, _samples;
    private readonly Dictionary<SceneObject, GpuObject> _objects = [];
    private readonly Dictionary<ViewerTexture, int> _textures = [];
    private int _whiteTexture;   // keeps the samplers valid for untextured sections
    private int _boneTexture;

    // Orbit camera around _target
    private Vector3 _target;
    private float _yaw = MathF.PI / 4, _pitch = 0.35f, _distance = 500;
    private Point _lastPointer;
    private bool _dragging;

    private TaskCompletionSource<byte[]>? _capture;

    static ModelViewport()
    {
        FocusableProperty.OverrideDefaultValue<ModelViewport>(true);
        AffectsRender<ModelViewport>(ShowGridProperty, ShowTexturesProperty, WireframeProperty, ShowBonesProperty, SelectedProperty);
    }

    public ModelViewport()
    {
        AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            OnMagnify((float) e.Delta.X);
            e.Handled = true;
        });
        Scene.Items.CollectionChanged += (_, _) => RequestNextFrameRendering();
    }

    /// <summary>Renders the next frame and returns it as PNG (used by "Save Screenshot" and the test harness).</summary>
    public Task<byte[]> CaptureAsync()
    {
        _capture ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestNextFrameRendering();
        return _capture.Task;
    }

    /// <summary>Morph weights applied to an object in the last frame (sliders, or animation curves when they drive a morph).</summary>
    public float[] AppliedMorphWeights(SceneObject obj) => _objects.TryGetValue(obj, out var gpu) ? gpu.MorphWeights : [];

    /// <summary>Centers the camera on the selection, or on the whole scene when nothing is selected.</summary>
    public void FrameModel() =>
        FrameBounds(Selected is { } item && Scene.Items.Contains(item) ? (item.Min, item.Max) : Scene.Bounds());

    /// <summary>Centers the camera on everything visible, and sets Fly's speed to the scene's size.</summary>
    public void FrameAll()
    {
        if (Scene.Bounds() is not var (min, max)) return;
        FrameBounds((min, max));
        _flySpeed = Math.Clamp((max - min).Length() / 4, 100, 3000); // 1–30 m/s
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Centers the camera on a box of the scene, from above at an angle.</summary>
    public void FrameBox(Vector3 min, Vector3 max, float pitch = 0.3f)
    {
        FrameBounds((min, max));
        _pitch = pitch;
    }

    private void FrameBounds((Vector3 Min, Vector3 Max)? bounds)
    {
        if (bounds is not var (min, max)) return;
        _target = (min + max) / 2;
        _distance = Math.Max((max - min).Length() / 2, 1) / MathF.Sin(_fov / 2) * 0.8f; // the bounding sphere is loose
        _yaw = MathF.PI / 4;
        _pitch = 0.3f;
        RequestNextFrameRendering();
    }

    private float _fov = 45 * MathF.PI / 180;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowGridProperty || change.Property == ShowTexturesProperty || change.Property == ShowNormalMapsProperty ||
            change.Property == WireframeProperty ||
            change.Property == ShowBonesProperty || change.Property == AnimationPhaseProperty || change.Property == SelectedProperty)
            RequestNextFrameRendering();
        if (change.Property == CameraModeProperty)
        {
            RequestNextFrameRendering();
            NavigationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------- GL lifetime ----------

    protected override void OnOpenGlInit(GlInterface glInterface)
    {
        try
        {
            var gl = new Gl(glInterface);
            _meshProgram = gl.CompileProgram(MeshVertex, MeshFragment,
                "aPosition", "aNormal", "aUv", "aBones", "aWeights", "aModel0", "aModel1", "aModel2", "aModel3", "aTangent");
            _lineProgram = gl.CompileProgram(LineVertex, LineFragment, "aPosition", "aColor");
            _samples = Math.Min(4, gl.GetInteger(Gl.MAX_SAMPLES));
            _whiteTexture = CreateTexture(gl, 1, 1, [255, 255, 255, 255]);
            _boneTexture = gl.GenTexture();
            _linesVao = gl.GenVertexArray();
            _linesVbo = gl.GenBuffer();
            _gl = gl;
            RendererInfo = $"OpenGL {gl.GetString(Gl.VERSION)} · {gl.GetString(Gl.RENDERER)}";
            Serilog.Log.Information("3D viewer ready: {Renderer}", RendererInfo);
        }
        catch (Exception e)
        {
            RendererInfo = $"OpenGL unavailable: {e.Message}";
            Serilog.Log.Error(e, "Could not initialize the 3D viewer");
        }
        Dispatcher.UIThread.Post(() => RendererReady?.Invoke(this, EventArgs.Empty));
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        if (_gl is not { } gl) return;
        foreach (var gpu in _objects.Values) Release(gl, gpu);
        _objects.Clear();
        foreach (var id in _textures.Values) gl.DeleteTexture(id);
        _textures.Clear();
        ReleaseFramebuffer(gl);
        gl.DeleteVertexArray(_gridVao);
        gl.DeleteBuffer(_gridVbo);
        gl.DeleteVertexArray(_linesVao);
        gl.DeleteBuffer(_linesVbo);
        gl.DeleteTexture(_whiteTexture);
        gl.DeleteTexture(_boneTexture);
        gl.DeleteProgram(_meshProgram);
        gl.DeleteProgram(_lineProgram);
        _gridVao = _gridVbo = _linesVao = _linesVbo = _whiteTexture = _boneTexture = _meshProgram = _lineProgram = 0;
        _gridVersion = -1;
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl is not { } gl) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int) (Bounds.Width * scaling));
        var height = Math.Max(1, (int) (Bounds.Height * scaling));

        SyncScene(gl);
        EnsureFramebuffer(gl, width, height);

        gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo != 0 ? _fbo : fb);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(Background.X, Background.Y, Background.Z, 1);
        gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        gl.Enable(Gl.DEPTH_TEST);
        gl.DepthFunc(Gl.LEQUAL);
        gl.Disable(Gl.CULL_FACE);
        if (_samples > 1) gl.Enable(Gl.MULTISAMPLE);

        var eye = _target + _distance * Forward;
        var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
        var sceneRadius = Scene.Bounds() is var (min, max) ? (max - min).Length() / 2 : 100;
        // Orbit: depth range around the target. Fly: the eye can be anywhere, right next to a wall
        var far = CameraMode == CameraMode.Fly ? sceneRadius * 4 + _distance * 2 : _distance * 100 + sceneRadius * 4;
        var near = CameraMode == CameraMode.Fly ? Math.Max(far * 1e-5f, 0.5f) : Math.Max(_distance * 0.01f, 1e-2f);
        var viewProjection = view * Perspective(_fov, (float) width / height, near, far);

        DrawScene(gl, viewProjection, eye);
        if (ShowGrid && _gridVertexCount > 0) DrawGrid(gl, viewProjection);
        DrawOverlays(gl, viewProjection);

        if (_fbo != 0)
        {
            gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _fbo);
            gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, fb);
            gl.BlitFramebuffer(width, height);
            gl.BindFramebuffer(Gl.FRAMEBUFFER, fb);
        }

        if (_capture is { } capture)
        {
            _capture = null;
            gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, fb);
            var pixels = new byte[width * height * 4];
            gl.PixelStore(Gl.PACK_ALIGNMENT, 1);
            gl.ReadPixels(width, height, pixels);
            gl.BindFramebuffer(Gl.FRAMEBUFFER, fb);
            Task.Run(() => capture.TrySetResult(EncodePng(pixels, width, height)));
        }
    }

    private Vector3 Forward => new(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));

    // ---------- Scene → GPU ----------

    private void SyncScene(Gl gl)
    {
        var live = Scene.Items.SelectMany(i => i.Objects).ToHashSet();

        foreach (var gone in _objects.Keys.Where(o => !live.Contains(o)).ToList())
        {
            Release(gl, _objects[gone]);
            _objects.Remove(gone);
        }

        foreach (var obj in live)
        {
            if (!_objects.TryGetValue(obj, out var gpu)) _objects[obj] = gpu = Upload(gl, obj.Model);
            if (gpu.InstanceVersion != obj.Version || gpu.InstanceCount != obj.Instances.Length) UploadInstances(gl, gpu, obj);
        }

        // Textures are shared between models (maps reuse them a lot): keep exactly the ones in use
        var needed = live.SelectMany(o => o.Model.Sections)
            .SelectMany(s => new[] { s.Texture, s.NormalTexture, s.MaskTexture, s.Landscape?.Weights }.Concat(s.Landscape?.Textures ?? []))
            .OfType<ViewerTexture>().ToHashSet();
        foreach (var unused in _textures.Keys.Where(t => !needed.Contains(t)).ToList())
        {
            gl.DeleteTexture(_textures[unused]);
            _textures.Remove(unused);
        }
        foreach (var texture in needed.Where(t => !_textures.ContainsKey(t)))
            _textures[texture] = CreateTexture(gl, texture.Width, texture.Height, texture.Rgba);

        if (_gridVersion != Scene.Version)
        {
            _gridVersion = Scene.Version;
            BuildGrid(gl);
        }
    }

    private static GpuObject Upload(Gl gl, ViewerModel model)
    {
        var gpu = new GpuObject { Vao = gl.GenVertexArray(), MorphWeights = new float[model.Morphs.Length] };
        gl.BindVertexArray(gpu.Vao);

        gpu.Vbo = gl.GenBuffer();
        gl.BindBuffer(Gl.ARRAY_BUFFER, gpu.Vbo);
        gl.BufferData<float>(Gl.ARRAY_BUFFER, model.Vertices);
        const int stride = ViewerModel.FloatsPerVertex * sizeof(float);
        gl.VertexAttribPointer(0, 3, stride, 0);
        gl.VertexAttribPointer(1, 3, stride, 3 * sizeof(float));
        gl.VertexAttribPointer(2, 2, stride, 6 * sizeof(float));
        gl.VertexAttribPointer(9, 4, stride, 8 * sizeof(float));

        if (model.Skin is { } skin)
        {
            gpu.SkinVbo = gl.GenBuffer();
            gl.BindBuffer(Gl.ARRAY_BUFFER, gpu.SkinVbo);
            gl.BufferData<float>(Gl.ARRAY_BUFFER, skin);
            const int skinStride = ViewerModel.FloatsPerSkin * sizeof(float);
            gl.VertexAttribPointer(3, 4, skinStride, 0);
            gl.VertexAttribPointer(4, 4, skinStride, 4 * sizeof(float));
        }

        gpu.InstanceVbo = gl.GenBuffer();
        gl.BindBuffer(Gl.ARRAY_BUFFER, gpu.InstanceVbo);
        gl.InstanceMatrixAttribute(5);

        gpu.Ebo = gl.GenBuffer();
        gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, gpu.Ebo);
        gl.BufferData<uint>(Gl.ELEMENT_ARRAY_BUFFER, model.Indices);
        gl.BindVertexArray(0);
        gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        return gpu;
    }

    private static void UploadInstances(Gl gl, GpuObject gpu, SceneObject obj)
    {
        var data = new float[obj.Instances.Length * 16];
        for (var i = 0; i < obj.Instances.Length; i++) CopyMatrix(obj.Instances[i], data, i * 16);
        gl.BindBuffer(Gl.ARRAY_BUFFER, gpu.InstanceVbo);
        gl.BufferData<float>(Gl.ARRAY_BUFFER, data);
        gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
        gpu.InstanceVersion = obj.Version;
        gpu.InstanceCount = obj.Instances.Length;
    }

    private static void Release(Gl gl, GpuObject gpu)
    {
        gl.DeleteVertexArray(gpu.Vao);
        gl.DeleteBuffer(gpu.Vbo);
        gl.DeleteBuffer(gpu.SkinVbo);
        gl.DeleteBuffer(gpu.Ebo);
        gl.DeleteBuffer(gpu.InstanceVbo);
    }

    // ---------- Drawing ----------

    private void DrawScene(Gl gl, Matrix4x4 viewProjection, Vector3 eye)
    {
        gl.UseProgram(_meshProgram);
        gl.UniformMatrix(gl.UniformLocation(_meshProgram, "uViewProjection"), viewProjection);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uCamera"), eye.X, eye.Y, eye.Z);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uTexture"), 0);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uBones"), 1);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uNormalMap"), 2);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uMaskMap"), 3);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uWeights"), 4);
        for (var i = 0; i < 4; i++) gl.Uniform(gl.UniformLocation(_meshProgram, $"uLayer{i}"), 5 + i);
        var landscape = gl.UniformLocation(_meshProgram, "uLandscape");
        var layerTiling = gl.UniformLocation(_meshProgram, "uLayerTiling");
        // Key light from above, over the viewer's left shoulder, so shapes read the same from any side
        var forward = Vector3.Normalize(_target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var key = Vector3.Normalize(-forward * 0.55f + Vector3.UnitY * 0.75f - right * 0.4f);
        gl.Uniform(gl.UniformLocation(_meshProgram, "uKeyLight"), key.X, key.Y, key.Z);
        var hasNormalMap = gl.UniformLocation(_meshProgram, "uHasNormalMap");
        var hasMaskMap = gl.UniformLocation(_meshProgram, "uHasMaskMap");
        var maskLayout = gl.UniformLocation(_meshProgram, "uMaskLayout");
        var skinned = gl.UniformLocation(_meshProgram, "uSkinned");
        var hasTexture = gl.UniformLocation(_meshProgram, "uHasTexture");
        var color = gl.UniformLocation(_meshProgram, "uColor");
        var flat = gl.UniformLocation(_meshProgram, "uFlat");

        if (Wireframe) gl.PolygonMode(Gl.FRONT_AND_BACK, Gl.LINE);
        gl.Uniform(flat, Wireframe ? 1 : 0);

        foreach (var obj in Scene.VisibleObjects)
        {
            if (!_objects.TryGetValue(obj, out var gpu) || gpu.InstanceCount == 0) continue;
            var model = obj.Model;
            var pose = model.IsSkinned ? obj.Pose : null;
            pose?.Evaluate(AnimationPhase);
            ApplyMorphs(gl, obj, gpu, pose);

            gl.Uniform(skinned, pose is not null ? 1 : 0);
            gl.ActiveTexture(Gl.TEXTURE0 + 1);
            if (pose is not null) UploadBones(gl, pose.Skinning);
            else gl.BindTexture(Gl.TEXTURE_2D, _whiteTexture);
            gl.ActiveTexture(Gl.TEXTURE0);

            gl.BindVertexArray(gpu.Vao);
            foreach (var section in model.Sections)
            {
                if (!section.IsVisible) continue;
                var texture = ShowTextures && section.Texture is { } t ? _textures.GetValueOrDefault(t) : 0;
                var normalMap = ShowTextures && ShowNormalMaps && section.NormalTexture is { } nt ? _textures.GetValueOrDefault(nt) : 0;
                var maskMap = ShowTextures && ShowNormalMaps && section.MaskTexture is { } mt ? _textures.GetValueOrDefault(mt) : 0;
                gl.ActiveTexture(Gl.TEXTURE0 + 2);
                gl.BindTexture(Gl.TEXTURE_2D, normalMap != 0 ? normalMap : _whiteTexture);
                gl.ActiveTexture(Gl.TEXTURE0 + 3);
                gl.BindTexture(Gl.TEXTURE_2D, maskMap != 0 ? maskMap : _whiteTexture);
                gl.ActiveTexture(Gl.TEXTURE0);
                var layers = ShowTextures && section.Landscape is { } l ? l : null;
                var weights = layers is not null ? _textures.GetValueOrDefault(layers.Weights) : 0;
                gl.ActiveTexture(Gl.TEXTURE0 + 4);
                gl.BindTexture(Gl.TEXTURE_2D, weights != 0 ? weights : _whiteTexture);
                var tiling = Vector4.Zero;
                for (var i = 0; i < 4; i++)
                {
                    var layer = weights != 0 && layers!.Textures[i] is { } lt ? _textures.GetValueOrDefault(lt) : 0;
                    gl.ActiveTexture(Gl.TEXTURE0 + 5 + i);
                    gl.BindTexture(Gl.TEXTURE_2D, layer != 0 ? layer : _whiteTexture);
                    if (layer != 0) tiling[i] = layers!.Tiling[i];
                }
                gl.ActiveTexture(Gl.TEXTURE0);
                gl.Uniform(landscape, weights != 0 ? 1 : 0);
                gl.Uniform(layerTiling, tiling.X, tiling.Y, tiling.Z, tiling.W);
                gl.Uniform(hasNormalMap, normalMap != 0 ? 1 : 0);
                gl.Uniform(hasMaskMap, maskMap != 0 ? 1 : 0);
                var layout = section.MaskLayout;
                gl.Uniform(maskLayout, layout.Roughness, layout.Metallic, layout.Occlusion);
                gl.BindTexture(Gl.TEXTURE_2D, texture != 0 ? texture : _whiteTexture);
                gl.Uniform(hasTexture, texture != 0 ? 1 : 0);
                var c = Wireframe ? new Vector3(0.85f, 0.87f, 0.92f) : section.Color;
                gl.Uniform(color, c.X, c.Y, c.Z);
                gl.DrawElementsInstanced(Gl.TRIANGLES, section.IndexCount, section.FirstIndex, gpu.InstanceCount);
            }
        }

        if (Wireframe) gl.PolygonMode(Gl.FRONT_AND_BACK, Gl.FILL);
        gl.BindVertexArray(0);
        gl.BindTexture(Gl.TEXTURE_2D, 0);
        for (var unit = 1; unit <= 8; unit++)
        {
            gl.ActiveTexture(Gl.TEXTURE0 + unit);
            gl.BindTexture(Gl.TEXTURE_2D, 0);
        }
        gl.ActiveTexture(Gl.TEXTURE0);
    }

    // Morph targets on the CPU, before GPU skinning: base vertices + Σ weight × delta, re-uploaded only on change
    private static void ApplyMorphs(Gl gl, SceneObject obj, GpuObject gpu, AnimationPose? pose)
    {
        var model = obj.Model;
        if (model.Morphs.Length == 0) return;

        var changed = false;
        for (var i = 0; i < model.Morphs.Length; i++)
        {
            var morph = model.Morphs[i];
            var weight = pose is not null && pose.Curves.TryGetValue(morph.Name, out var curve) ? curve : morph.Weight;
            if (Math.Abs(weight - gpu.MorphWeights[i]) <= 1e-4f) continue;
            gpu.MorphWeights[i] = weight;
            changed = true;
        }
        if (!changed) return;

        var vertices = gpu.MorphedVertices ??= new float[model.Vertices.Length];
        model.Vertices.CopyTo(vertices, 0);
        for (var m = 0; m < model.Morphs.Length; m++)
        {
            var weight = gpu.MorphWeights[m];
            if (Math.Abs(weight) <= 1e-4f) continue;
            var morph = model.Morphs[m];
            for (var k = 0; k < morph.VertexIndices.Length; k++)
            {
                var o = morph.VertexIndices[k] * ViewerModel.FloatsPerVertex;
                var d = k * 6;
                for (var c = 0; c < 6; c++) vertices[o + c] += weight * morph.Deltas[d + c];
            }
        }

        gl.BindBuffer(Gl.ARRAY_BUFFER, gpu.Vbo);
        gl.BufferData<float>(Gl.ARRAY_BUFFER, vertices);
        gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
    }

    private void UploadBones(Gl gl, Matrix4x4[] matrices)
    {
        var data = new float[matrices.Length * 16];
        for (var i = 0; i < matrices.Length; i++) CopyMatrix(matrices[i], data, i * 16);
        gl.BindTexture(Gl.TEXTURE_2D, _boneTexture);
        gl.TexImage2DFloat(4, matrices.Length, data);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
    }

    private static void CopyMatrix(Matrix4x4 m, float[] data, int o)
    {
        data[o] = m.M11; data[o + 1] = m.M12; data[o + 2] = m.M13; data[o + 3] = m.M14;
        data[o + 4] = m.M21; data[o + 5] = m.M22; data[o + 6] = m.M23; data[o + 7] = m.M24;
        data[o + 8] = m.M31; data[o + 9] = m.M32; data[o + 10] = m.M33; data[o + 11] = m.M34;
        data[o + 12] = m.M41; data[o + 13] = m.M42; data[o + 14] = m.M43; data[o + 15] = m.M44;
    }

    private void DrawGrid(Gl gl, Matrix4x4 viewProjection)
    {
        gl.Enable(Gl.BLEND);
        gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        gl.UseProgram(_lineProgram);
        gl.UniformMatrix(gl.UniformLocation(_lineProgram, "uViewProjection"), viewProjection);
        gl.Uniform(gl.UniformLocation(_lineProgram, "uFadeCenter"), _gridCenter.X, _gridCenter.Y, _gridCenter.Z);
        gl.Uniform(gl.UniformLocation(_lineProgram, "uExtent"), _gridExtent);
        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(Gl.LINES, 0, _gridVertexCount);
        gl.BindVertexArray(0);
        gl.Disable(Gl.BLEND);
    }

    // Selection box (when there is more than one item) and the selected mesh's skeleton, on top of everything
    private void DrawOverlays(Gl gl, Matrix4x4 viewProjection)
    {
        var data = new List<float>();
        void Line(Vector3 a, Vector3 b, Vector3 color) =>
            data.AddRange([a.X, a.Y, a.Z, color.X, color.Y, color.Z, b.X, b.Y, b.Z, color.X, color.Y, color.Z]);

        if (Selected is { IsVisible: true } selected && Scene.Items.Contains(selected) && Scene.Items.Count(i => i.IsVisible) > 1)
        {
            Vector3 lo = selected.Min, hi = selected.Max;
            Vector3 Corner(int i) => new((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                Line(Corner(a), Corner(b), Accent);
        }

        if (ShowBones && Selected?.Single is { Model: { IsSkinned: true } model } obj)
        {
            var globals = obj.Pose?.Globals ?? AnimationPose.ReferenceGlobals(model);
            var placement = obj.Instances[0];
            var bones = model.Bones!;
            var color = new Vector3(0.98f, 0.8f, 0.35f);
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i].ParentIndex < 0) continue;
                Line(Vector3.Transform(globals[i].Translation, placement), Vector3.Transform(globals[bones[i].ParentIndex].Translation, placement), color);
            }
        }
        if (data.Count == 0) return;

        gl.Disable(Gl.DEPTH_TEST);
        gl.Enable(Gl.BLEND);
        gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        gl.UseProgram(_lineProgram);
        gl.UniformMatrix(gl.UniformLocation(_lineProgram, "uViewProjection"), viewProjection);
        gl.Uniform(gl.UniformLocation(_lineProgram, "uFadeCenter"), 0f, 0f, 0f);
        gl.Uniform(gl.UniformLocation(_lineProgram, "uExtent"), 1e12f);
        gl.BindVertexArray(_linesVao);
        gl.BindBuffer(Gl.ARRAY_BUFFER, _linesVbo);
        gl.BufferData<float>(Gl.ARRAY_BUFFER, data.ToArray());
        gl.VertexAttribPointer(0, 3, 6 * sizeof(float), 0);
        gl.VertexAttribPointer(1, 3, 6 * sizeof(float), 3 * sizeof(float));
        gl.DrawArrays(Gl.LINES, 0, data.Count / 6);
        gl.BindVertexArray(0);
        gl.Disable(Gl.BLEND);
        gl.Enable(Gl.DEPTH_TEST);
    }

    private static int CreateTexture(Gl gl, int width, int height, byte[] rgba)
    {
        var id = gl.GenTexture();
        gl.BindTexture(Gl.TEXTURE_2D, id);
        gl.PixelStore(Gl.UNPACK_ALIGNMENT, 1);
        gl.TexImage2D(width, height, rgba);
        gl.GenerateMipmap(Gl.TEXTURE_2D);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR_MIPMAP_LINEAR);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_ANISOTROPY, 8); // ground seen at a grazing angle stays sharp
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, Gl.REPEAT);
        gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, Gl.REPEAT);
        gl.BindTexture(Gl.TEXTURE_2D, 0);
        return id;
    }

    // Ground grid on y = 0 (UE's Z = 0) under the scene, with a round step a bit smaller than it
    private void BuildGrid(Gl gl)
    {
        _gridVertexCount = 0;
        if (Scene.Bounds() is not var (min, max)) return;

        var radius = Math.Max((max - min).Length() / 2, 1);
        var step = MathF.Pow(10, MathF.Floor(MathF.Log10(radius)));
        if (radius / step < 3) step /= 2;
        const int lines = 20;
        _gridExtent = step * lines;
        // Snap the grid to its step so the axis lines stay on the world origin
        var center = (min + max) / 2;
        _gridCenter = new Vector3(MathF.Round(center.X / step) * step, 0, MathF.Round(center.Z / step) * step);

        var data = new List<float>();
        void Line(Vector3 a, Vector3 b, Vector3 color) =>
            data.AddRange([a.X, a.Y, a.Z, color.X, color.Y, color.Z, b.X, b.Y, b.Z, color.X, color.Y, color.Z]);

        var minor = new Vector3(0.36f, 0.37f, 0.43f);
        for (var i = -lines; i <= lines; i++)
        {
            var x = _gridCenter.X + i * step;
            var z = _gridCenter.Z + i * step;
            var onAxisX = MathF.Abs(x) < step * 0.01f;
            var onAxisZ = MathF.Abs(z) < step * 0.01f;
            Line(new Vector3(x, 0, _gridCenter.Z - _gridExtent), new Vector3(x, 0, _gridCenter.Z + _gridExtent), onAxisX ? new Vector3(0.75f, 0.3f, 0.3f) : minor);
            Line(new Vector3(_gridCenter.X - _gridExtent, 0, z), new Vector3(_gridCenter.X + _gridExtent, 0, z), onAxisZ ? new Vector3(0.3f, 0.45f, 0.8f) : minor);
        }

        if (_gridVao == 0)
        {
            _gridVao = gl.GenVertexArray();
            _gridVbo = gl.GenBuffer();
        }
        gl.BindVertexArray(_gridVao);
        gl.BindBuffer(Gl.ARRAY_BUFFER, _gridVbo);
        gl.BufferData<float>(Gl.ARRAY_BUFFER, data.ToArray());
        gl.VertexAttribPointer(0, 3, 6 * sizeof(float), 0);
        gl.VertexAttribPointer(1, 3, 6 * sizeof(float), 3 * sizeof(float));
        gl.BindVertexArray(0);
        _gridVertexCount = data.Count / 6;
    }

    // Multisampled offscreen target, resolved into Avalonia's framebuffer every frame
    private void EnsureFramebuffer(Gl gl, int width, int height)
    {
        if (_samples <= 1 || (_fbo != 0 && width == _fboWidth && height == _fboHeight)) return;

        ReleaseFramebuffer(gl);
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        _colorRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(_colorRb);
        gl.RenderbufferStorageMultisample(_samples, Gl.RGBA8, width, height);
        gl.FramebufferRenderbuffer(Gl.COLOR_ATTACHMENT0, _colorRb);
        _depthRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(_depthRb);
        gl.RenderbufferStorageMultisample(_samples, Gl.DEPTH_COMPONENT24, width, height);
        gl.FramebufferRenderbuffer(Gl.DEPTH_ATTACHMENT, _depthRb);
        gl.BindRenderbuffer(0);

        if (gl.CheckFramebufferStatus() != Gl.FRAMEBUFFER_COMPLETE)
        {
            Serilog.Log.Warning("Multisampled framebuffer incomplete, rendering without MSAA");
            ReleaseFramebuffer(gl);
            _samples = 0;
            return;
        }
        _fboWidth = width;
        _fboHeight = height;
    }

    private void ReleaseFramebuffer(Gl gl)
    {
        gl.DeleteFramebuffer(_fbo);
        gl.DeleteRenderbuffer(_colorRb);
        gl.DeleteRenderbuffer(_depthRb);
        _fbo = _colorRb = _depthRb = _fboWidth = _fboHeight = 0;
    }

    // System.Numerics is row-vector, so this is the transpose of the usual GL projection (clip z in -w..w)
    private static Matrix4x4 Perspective(float fov, float aspect, float near, float far)
    {
        var f = 1 / MathF.Tan(fov / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }

    private static byte[] EncodePng(byte[] bottomUp, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var stride = width * 4;
        var topDown = new byte[bottomUp.Length];
        for (var y = 0; y < height; y++)
            bottomUp.AsSpan((height - 1 - y) * stride, stride).CopyTo(topDown.AsSpan(y * stride, stride));
        System.Runtime.InteropServices.Marshal.Copy(topDown, 0, bitmap.GetPixels(), topDown.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ---------- Input ----------
    // Snooper's controls: left-drag orbits (Orbit) or looks around (Fly), as do the arrow keys; WASD move and Q/E go
    // down/up while held (Shift faster, Option slower), each axis scaled by SpeedX/Y/Z; C/X zoom the lens; F frames.
    // Right/middle/Shift-drag pans; the wheel or a pinch zooms (Fly: moves forward, and with a button held changes
    // the speed, as do +/-). The window forwards its keys (HandleKeyDown/HandleKeyUp), so they work without clicking
    // the view first.

    // Without this the GL surface isn't hit-testable (nothing is drawn through the control's own render), so clicks,
    // drags and the wheel went to whatever was behind it
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private readonly HashSet<Key> _held = [];
    private KeyModifiers _modifiers;
    private TimeSpan? _lastStep;
    private bool _stepping;
    private float _flySpeed = 500; // units (cm) per second

    /// <summary>Raised when the camera mode, speed or lens changes (the window's hint line).</summary>
    public event EventHandler? NavigationChanged;

    // An arrow key held for a second turns as much as a 120-pixel drag (about 70°/s orbiting, 35°/s flying)
    private const float ArrowPixelsPerSecond = 120;

    /// <summary>Multipliers of the movement along each axis: X sideways (A/D), Y ahead (W/S), Z up (Q/E).</summary>
    public float SpeedX { get; set; } = 1;
    public float SpeedY { get; set; } = 1;
    public float SpeedZ { get; set; } = 1;

    /// <summary>Multiplier of how fast drags and the arrow keys turn the camera.</summary>
    public float RotationSpeed { get; set; } = 1;

    /// <summary>Fly mode's speed in m/s.</summary>
    public float FlySpeed => _flySpeed / 100;

    /// <summary>The lens' vertical field of view in degrees (C/X).</summary>
    public float FieldOfViewDegrees => _fov * 180 / MathF.PI;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointer = e.GetPosition(this);
        _dragging = true;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            Pan((float) delta.X, (float) delta.Y);
        else
            Rotate((float) delta.X, (float) delta.Y);
    }

    /// <summary>Moves the camera in its plane, so the point under the cursor follows it.</summary>
    public void Pan(float dx, float dy)
    {
        var unitsPerPixel = 2 * _distance * MathF.Tan(_fov / 2) / (float) Math.Max(Bounds.Height, 1);
        var (right, up) = CameraAxes();
        _target += (-dx * right + dy * up) * unitsPerPixel;
        RequestNextFrameRendering();
    }

    /// <summary>A mouse drag of (dx, dy) pixels: orbits around the target, or (Fly) turns the camera where it stands.</summary>
    public void Rotate(float dx, float dy)
    {
        var fly = CameraMode == CameraMode.Fly;
        var eye = _target + _distance * Forward;
        var sensitivity = (fly ? 0.005f : 0.01f) * RotationSpeed;
        _yaw -= dx * sensitivity;
        _pitch = Math.Clamp(_pitch + dy * sensitivity, -1.55f, 1.55f);
        if (fly) _target = eye - _distance * Forward;
        RequestNextFrameRendering();
    }

    private (Vector3 Right, Vector3 Up) CameraAxes()
    {
        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        return (right, Vector3.Cross(forward, right));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var notches = (float) e.Delta.Y;
        if (CameraMode == CameraMode.Fly)
        {
            if (_dragging) AdjustSpeed(MathF.Pow(1.25f, notches));
            else Fly(notches * _flySpeed * 0.15f);
        }
        else Zoom(MathF.Pow(0.9f, notches));
        e.Handled = true;
    }

    private void OnMagnify(float delta)
    {
        if (CameraMode == CameraMode.Fly) Fly(delta * _flySpeed * 1.5f);
        else Zoom(1 / (1 + delta));
    }

    // Moves eye and target together along the view
    private void Fly(float distance)
    {
        _target -= Forward * distance;
        RequestNextFrameRendering();
    }

    public void AdjustSpeed(float factor)
    {
        _flySpeed = Math.Clamp(_flySpeed * factor, 10, 100_000);
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A key pressed anywhere in the viewer window; true when the camera used it.</summary>
    public bool HandleKeyDown(Key key, KeyModifiers modifiers)
    {
        _modifiers = modifiers;
        if (modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control))
        {
            _held.Clear(); // ⌘ shortcuts belong to the menus, and macOS drops the key-ups of keys pressed with ⌘
            return false;
        }
        switch (key)
        {
            case Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.C or Key.X
                or Key.Left or Key.Right or Key.Up or Key.Down:
                _held.Add(key);
                StartStepping();
                return true;
            case Key.F:
                FrameModel();
                return true;
            case Key.OemPlus or Key.Add:
                AdjustSpeed(1.25f);
                return true;
            case Key.OemMinus or Key.Subtract:
                AdjustSpeed(0.8f);
                return true;
            default:
                return false;
        }
    }

    public void HandleKeyUp(Key key, KeyModifiers modifiers)
    {
        _modifiers = modifiers;
        _held.Remove(key);
    }

    /// <summary>Forgets held keys (the window lost focus: their key-ups won't arrive).</summary>
    public void ReleaseKeys() => _held.Clear();

    /// <summary>Holds <paramref name="key"/> for <paramref name="seconds"/> (test harness).</summary>
    public async Task HoldKeyAsync(Key key, double seconds, KeyModifiers modifiers = KeyModifiers.None)
    {
        HandleKeyDown(key, modifiers);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        HandleKeyUp(key, KeyModifiers.None);
    }

    /// <summary>Eye position and look target, in viewer space (test harness).</summary>
    public (Vector3 Eye, Vector3 Target) CameraPose => (_target + _distance * Forward, _target);

    // Held keys move the camera every frame, scaled by the frame time
    private void StartStepping()
    {
        if (_stepping || TopLevel.GetTopLevel(this) is not { } top) return;
        _stepping = true;
        _lastStep = null;
        top.RequestAnimationFrame(Step);
    }

    private void Step(TimeSpan now)
    {
        var dt = _lastStep is { } last ? (float) Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 1 / 60f;
        _lastStep = now;
        if (_held.Count == 0 || TopLevel.GetTopLevel(this) is not { } top)
        {
            _stepping = false;
            return;
        }
        Move(dt);
        top.RequestAnimationFrame(Step);
    }

    private void Move(float dt)
    {
        var boost = (_modifiers.HasFlag(KeyModifiers.Shift) ? 3f : 1f) * (_modifiers.HasFlag(KeyModifiers.Alt) ? 0.25f : 1f);
        var fly = CameraMode == CameraMode.Fly;
        // Orbit walks the target over the ground at a pace that follows the zoom; Fly goes where it looks
        var step = (fly ? _flySpeed : _distance * 0.75f) * boost * dt;
        var (right, _) = CameraAxes();
        var ahead = fly ? -Forward : Vector3.Normalize(new Vector3(-Forward.X, 0, -Forward.Z));
        var move = Vector3.Zero;
        if (_held.Contains(Key.W)) move += ahead * SpeedY;
        if (_held.Contains(Key.S)) move -= ahead * SpeedY;
        if (_held.Contains(Key.D)) move += right * SpeedX;
        if (_held.Contains(Key.A)) move -= right * SpeedX;
        if (_held.Contains(Key.E)) move += Vector3.UnitY * SpeedZ;
        if (_held.Contains(Key.Q)) move -= Vector3.UnitY * SpeedZ;
        _target += move * step;

        // Arrows turn the camera like a drag in their direction (Rotate applies the rotation speed)
        var turnX = (_held.Contains(Key.Right) ? 1 : 0) - (_held.Contains(Key.Left) ? 1 : 0);
        var turnY = (_held.Contains(Key.Down) ? 1 : 0) - (_held.Contains(Key.Up) ? 1 : 0);
        if (turnX != 0 || turnY != 0) Rotate(turnX * ArrowPixelsPerSecond * boost * dt, turnY * ArrowPixelsPerSecond * boost * dt);

        var lens = (_held.Contains(Key.X) ? 1 : 0) - (_held.Contains(Key.C) ? 1 : 0);
        if (lens != 0)
        {
            _fov = Math.Clamp(_fov + lens * 40 * MathF.PI / 180 * boost * dt, 10 * MathF.PI / 180, 100 * MathF.PI / 180);
            NavigationChanged?.Invoke(this, EventArgs.Empty);
        }
        RequestNextFrameRendering();
    }

    private void Zoom(float factor)
    {
        var radius = Scene.Bounds() is var (min, max) ? Math.Max((max - min).Length() / 2, 1) : 100;
        _distance = Math.Clamp(_distance * factor, 0.5f, radius * 200);
        RequestNextFrameRendering();
    }
}

/// <summary>FModel's camera modes: Arcball (orbit around a target) and Fly Cam (move and look like in a game).</summary>
public enum CameraMode
{
    Orbit,
    Fly
}
