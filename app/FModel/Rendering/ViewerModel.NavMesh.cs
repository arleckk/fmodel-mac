using System.Numerics;

namespace FModel.Rendering;

public sealed partial class ViewerModel
{
    public const string NavMeshKind = "Navigation Mesh";
    private const int NavShades = 8;
    private const byte WalkableArea = 63; // RC_WALKABLE_AREA: UE's default nav area

    /// <summary>
    /// A navigation mesh as Detour's debug draw shows it: each ground polygon's detail triangles, one section per
    /// area (UE's default walkable area in green), polygons told apart by shade, and off-mesh links as arcs.
    /// </summary>
    public static ViewerModel FromNavMesh(NavMesh nav)
    {
        var areas = new SortedDictionary<int, List<float>>();
        var links = new List<float>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        // Recast is Y-up with UE = (-x, -z, y); the viewer swaps UE's Y and Z
        static Vector3 ToViewer(Vector3 r) => new(-r.X, r.Y, -r.Z);

        void Triangle(List<float> target, Vector3 a, Vector3 b, Vector3 c, float u)
        {
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.Y < 0) (b, c, normal) = (c, b, -normal); // walkable surfaces face up
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            foreach (var p in (ReadOnlySpan<Vector3>) [a, b, c])
            {
                target.AddRange([p.X, p.Y, p.Z, normal.X, normal.Y, normal.Z, u, 0.5f, 1, 0, 0, 1]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        for (var t = 0; t < nav.Tiles.Length; t++)
        {
            var tile = nav.Tiles[t];
            for (var i = 0; i < tile.Polys.Length; i++)
            {
                var poly = tile.Polys[i];
                if (poly.IsOffMeshLink)
                {
                    if (poly.Verts.Length >= 2 && poly.Verts.Max() < tile.Verts.Length)
                        AddLink(links, ToViewer(tile.Verts[poly.Verts[0]]), ToViewer(tile.Verts[poly.Verts[1]]), ref min, ref max);
                    continue;
                }
                if (poly.Verts.Length < 3 || poly.Verts.Max() >= tile.Verts.Length) continue;
                if (!areas.TryGetValue(poly.Area, out var target)) areas[poly.Area] = target = [];
                // A shade per polygon, so neighbors stand apart
                var u = (((uint) (t * 73856093) ^ (uint) (i * 19349663)) % NavShades + 0.5f) / NavShades;

                Vector3 Vertex(int index, NavPolyDetail detail) => index < poly.Verts.Length
                    ? ToViewer(tile.Verts[poly.Verts[index]])
                    : ToViewer(tile.DetailVerts[detail.VertBase + index - poly.Verts.Length]);

                if (i < tile.Details.Length && tile.Details[i] is { TriCount: > 0 } detail &&
                    (detail.TriBase + detail.TriCount) * 4 <= tile.DetailTris.Length && detail.VertBase + detail.VertCount <= tile.DetailVerts.Length)
                {
                    for (var k = 0; k < detail.TriCount; k++)
                    {
                        var o = (detail.TriBase + k) * 4;
                        int i0 = tile.DetailTris[o], i1 = tile.DetailTris[o + 1], i2 = tile.DetailTris[o + 2];
                        var limit = poly.Verts.Length + detail.VertCount;
                        if (i0 >= limit || i1 >= limit || i2 >= limit) continue;
                        Triangle(target, Vertex(i0, detail), Vertex(i1, detail), Vertex(i2, detail), u);
                    }
                }
                else
                {
                    // No detail mesh: a fan over the polygon itself
                    for (var k = 2; k < poly.Verts.Length; k++)
                        Triangle(target, ToViewer(tile.Verts[poly.Verts[0]]), ToViewer(tile.Verts[poly.Verts[k - 1]]), ToViewer(tile.Verts[poly.Verts[k]]), u);
                }
            }
        }

        var vertices = new List<float>();
        var sections = new List<ViewerSection>();
        void AddSection(List<float> data, string name, Vector3 color, ViewerTexture? texture)
        {
            if (data.Count == 0) return;
            var first = vertices.Count / FloatsPerVertex;
            vertices.AddRange(data);
            sections.Add(new ViewerSection
            {
                FirstIndex = first, IndexCount = data.Count / FloatsPerVertex, MaterialName = name, Color = color, Texture = texture
            });
        }

        foreach (var (area, data) in areas)
        {
            var color = AreaColor(area);
            var name = area switch { WalkableArea => $"Area {area} (walkable)", 0 => "Area 0 (null)", _ => $"Area {area}" };
            AddSection(data, name, color, Shades($"{nav.Name}_Area{area}", color));
        }
        AddSection(links, "Off-mesh links", new Vector3(1f, 0.55f, 0.1f), null);
        if (vertices.Count == 0) throw new InvalidOperationException($"{nav.Name} has no polygons");

        return new ViewerModel
        {
            Name = nav.Name,
            Kind = NavMeshKind,
            Vertices = vertices.ToArray(),
            Indices = Enumerable.Range(0, vertices.Count / FloatsPerVertex).Select(i => (uint) i).ToArray(),
            Sections = sections.ToArray(),
            LodCount = 1,
            Min = min,
            Max = max
        };
    }

    // An off-mesh link (jump, ladder…) as a ribbon arcing from one end to the other
    private static void AddLink(List<float> target, Vector3 a, Vector3 b, ref Vector3 min, ref Vector3 max)
    {
        const int segments = 12;
        var span = b - a;
        var length = span.Length();
        if (length < 1e-3f) return;
        var side = Vector3.Cross(span / length, Vector3.UnitY);
        side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) * 6 : Vector3.UnitX * 6;
        var height = Math.Max(length * 0.25f, 30);
        Vector3 At(float s) => a + span * s + Vector3.UnitY * (height * 4 * s * (1 - s));
        for (var k = 0; k < segments; k++)
        {
            Vector3 p0 = At(k / (float) segments), p1 = At((k + 1) / (float) segments);
            foreach (var p in (ReadOnlySpan<Vector3>) [p0 - side, p0 + side, p1 + side, p0 - side, p1 + side, p1 - side])
            {
                target.AddRange([p.X, p.Y, p.Z, 0, 1, 0, 0, 0, 1, 0, 0, 1]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
    }

    private static Vector3 AreaColor(int area)
    {
        if (area == WalkableArea) return new Vector3(0.30f, 0.78f, 0.25f);
        if (area == 0) return new Vector3(0.55f, 0.15f, 0.15f);
        // Spread the other areas around the hue circle
        var hue = area * 0.618034f % 1f * 6;
        var x = 1 - Math.Abs(hue % 2 - 1);
        var (r, g, b) = (int) hue switch { 0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x) };
        return new Vector3(0.2f + 0.65f * r, 0.2f + 0.65f * g, 0.2f + 0.65f * b);
    }

    // A strip of shades of the area's color; each polygon samples one texel
    private static ViewerTexture Shades(string name, Vector3 color)
    {
        var rgba = new byte[NavShades * 4];
        for (var k = 0; k < NavShades; k++)
        {
            var c = Vector3.Clamp(color * (0.72f + 0.08f * k), Vector3.Zero, Vector3.One) * 255;
            rgba[k * 4] = (byte) c.X;
            rgba[k * 4 + 1] = (byte) c.Y;
            rgba[k * 4 + 2] = (byte) c.Z;
            rgba[k * 4 + 3] = 255;
        }
        return new ViewerTexture(name, NavShades, 1, rgba);
    }
}
