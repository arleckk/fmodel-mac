using System.Buffers.Binary;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.NavigationSystem;
using CUE4Parse.UE4.Assets.Exports.NavigationSystem.Detour;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.NavigationSystem.NavMesh;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Rendering;

/// <summary>A Detour polygon: up to 6 vertices of its tile, its area (0–63) and whether it's an off-mesh link (2 vertices).</summary>
public readonly record struct NavPoly(ushort[] Verts, byte Area, bool IsOffMeshLink, ushort Flags);

/// <summary>The triangles of one ground polygon in its tile's detail mesh (indices past the polygon's own vertices are detail vertices).</summary>
public readonly record struct NavPolyDetail(int VertBase, int TriBase, int VertCount, int TriCount);

/// <summary>One tile of a Detour navigation mesh, in Recast coordinates (Y up; UE = (-x, -z, y)).</summary>
public sealed class NavTile
{
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Layer { get; init; }
    public required Vector3[] Verts { get; init; }
    public required NavPoly[] Polys { get; init; }
    public required NavPolyDetail[] Details { get; init; }
    public required Vector3[] DetailVerts { get; init; }
    /// <summary>4 bytes per triangle: three vertex indices and edge flags.</summary>
    public required byte[] DetailTris { get; init; }
    public Vector3 BMin { get; init; }
    public Vector3 BMax { get; init; }
}

/// <summary>
/// A Detour (Recast) navigation mesh for the 3D viewer: UE's own (<see cref="URecastNavMeshDataChunk"/> and
/// <see cref="ARecastNavMesh"/> tiles, read by CUE4Parse) or a server-side "MSET" dump (<see cref="TryRead"/>).
/// </summary>
public sealed class NavMesh
{
    public required string Name { get; init; }
    public required NavTile[] Tiles { get; init; }
    /// <summary>Agent and tiling parameters, when the source has them (for the summary).</summary>
    public IReadOnlyDictionary<string, object>? Parameters { get; init; }

    public int PolyCount => Tiles.Sum(t => t.Polys.Length);

    private const uint SetMagic = 'M' << 24 | 'S' << 16 | 'E' << 8 | 'T'; // "TESM" on disk

    public static bool IsNavMeshSet(ReadOnlySpan<byte> bytes) => bytes.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == SetMagic;

    /// <summary>
    /// RecastDemo's navmesh set ("MSET", version 1) as UE5 builds with double coordinates write it, e.g. the server
    /// navmeshes of Digimon Super Rumble: the memory image of <c>dtNavMeshParams</c> and of each tile's data (header,
    /// vertices, polygons, links, detail meshes/vertices/triangles, BV tree…), every array 8-byte aligned.
    /// </summary>
    public static NavMesh? TryRead(byte[] bytes, string name)
    {
        if (!IsNavMeshSet(bytes)) return null;
        var data = bytes.AsSpan();
        var version = I32(data, 4);
        if (version != 1) throw new NotSupportedException($"{name}: navmesh set version {version} isn't supported");
        var tileCount = I32(data, 8);

        // dtNavMeshParams with UE5's agent fields first: walkable height/radius/climb, 3 resolution quantization
        // factors, origin, tile width/height (doubles), then max tiles/polys
        var parameters = new Dictionary<string, object>
        {
            ["WalkableHeight"] = F64(data, 16), ["WalkableRadius"] = F64(data, 24), ["WalkableClimb"] = F64(data, 32),
            ["Origin"] = new[] { F64(data, 64), F64(data, 72), F64(data, 80) },
            ["TileWidth"] = F64(data, 88), ["TileHeight"] = F64(data, 96),
            ["MaxTiles"] = I32(data, 104), ["MaxPolys"] = I32(data, 108)
        };

        var tiles = new List<NavTile>(tileCount);
        var offset = 112;
        for (var i = 0; i < tileCount && offset + 16 <= data.Length; i++)
        {
            var tileRef = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            var size = I32(data, offset + 8);
            offset += 16; // tile ref, data size, padding
            if (tileRef == 0 || size <= 0) break;
            if (offset + size > data.Length) throw new InvalidDataException($"{name}: tile {i} runs past the end of the file");
            tiles.Add(ReadTile(data.Slice(offset, size)));
            offset += size;
        }
        return new NavMesh { Name = name, Tiles = tiles.ToArray(), Parameters = parameters };
    }

    // Tile header (88 bytes): version, layer, poly/vert counts, x, y, link/detail/BV/off-mesh counts, then bmin/bmax
    private static NavTile ReadTile(ReadOnlySpan<byte> d)
    {
        var polyCount = U16(d, 4);
        var vertCount = U16(d, 6);
        var linkCount = U16(d, 16);
        var detailMeshCount = U16(d, 18);
        var detailVertCount = U16(d, 20);
        var detailTriCount = U16(d, 22);

        var o = 88;
        var verts = ReadVectors(d, ref o, vertCount);
        var polys = new NavPoly[polyCount];
        for (var i = 0; i < polyCount; i++, o += 32)
        {
            // firstLink, verts[6], neis[6], flags, vertCount, areaAndType
            var count = Math.Min((int) d[o + 30], 6);
            var indices = new ushort[count];
            for (var k = 0; k < count; k++) indices[k] = BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 4 + k * 2)..]);
            polys[i] = new NavPoly(indices, (byte) (d[o + 31] & 0x3F), d[o + 31] >> 6 == 1, (ushort) U16(d, o + 28));
        }
        o += linkCount * 16; // dtLink: 64-bit poly ref, next, edge, side, bmin, bmax

        var details = new NavPolyDetail[detailMeshCount];
        for (var i = 0; i < detailMeshCount; i++)
            details[i] = new NavPolyDetail(U16(d, o + i * 6), U16(d, o + i * 6 + 2), d[o + i * 6 + 4], d[o + i * 6 + 5]);
        o += Align8(detailMeshCount * 6);
        var detailVerts = ReadVectors(d, ref o, detailVertCount);
        var tris = d.Slice(o, detailTriCount * 4).ToArray();

        return new NavTile
        {
            X = I32(d, 8), Y = I32(d, 12), Layer = U16(d, 2), Verts = verts, Polys = polys, Details = details, DetailVerts = detailVerts,
            DetailTris = tris,
            BMin = new Vector3((float) F64(d, 40), (float) F64(d, 48), (float) F64(d, 56)),
            BMax = new Vector3((float) F64(d, 64), (float) F64(d, 72), (float) F64(d, 80))
        };
    }

    private static Vector3[] ReadVectors(ReadOnlySpan<byte> d, ref int offset, int count)
    {
        var result = new Vector3[count];
        for (var i = 0; i < count; i++, offset += 24)
            result[i] = new Vector3((float) F64(d, offset), (float) F64(d, offset + 8), (float) F64(d, offset + 16));
        return result;
    }

    private static int Align8(int size) => (size + 7) & ~7;
    private static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
    private static double F64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadDoubleLittleEndian(d[o..]);

    /// <summary>UE's cooked navmesh tiles (as CUE4Parse reads them) in a package's exports, one mesh per navigation data (agent).</summary>
    public static List<NavMesh> FromExports(IEnumerable<UObject> exports)
    {
        var byAgent = new Dictionary<string, List<NavTile>>(StringComparer.OrdinalIgnoreCase);
        void Add(string agent, IEnumerable<FRecastTileData?>? tiles)
        {
            var converted = (tiles ?? []).Where(t => t is { IsValid: true }).Select(t => FromDetour(t!.Tile!)).ToList();
            if (converted.Count == 0) return;
            if (!byAgent.TryGetValue(agent, out var list)) byAgent[agent] = list = [];
            list.AddRange(converted);
        }

        foreach (var export in exports)
        {
            switch (export)
            {
                case URecastNavMeshDataChunk chunk:
                    Add(chunk.GetOrDefault("NavigationDataName", new FName("RecastNavMesh")).Text, chunk.Tiles);
                    break;
                case ARecastNavMesh actor when actor.RecastNavMeshImpl is { } impl:
                    Add(actor.Name, impl.DetourMeshTiles);
                    break;
            }
        }
        return byAgent.Select(p => new NavMesh { Name = p.Key, Tiles = p.Value.ToArray() }).ToList();
    }

    /// <summary>
    /// A level's cooked navmeshes: its navigation data chunks (static navmeshes built into streaming levels) and
    /// RecastNavMesh actors that keep their tiles, one mesh per navigation data (agent).
    /// </summary>
    public static List<NavMesh> FromLevel(ULevel level)
    {
        var objects = new List<UObject>();
        foreach (var chunk in level.GetOrDefault<FPackageIndex[]>("NavDataChunks", []))
            if (chunk.TryLoad<UObject>(out var loaded)) objects.Add(loaded);
        foreach (var actor in level.Actors)
            if (actor?.ResolvedObject?.Class?.Name.Text.Contains("RecastNavMesh", StringComparison.OrdinalIgnoreCase) == true &&
                actor.TryLoad<UObject>(out var loaded))
                objects.Add(loaded);
        return FromExports(objects);
    }

    private static NavTile FromDetour(DetourMeshTile tile)
    {
        static Vector3 V(CUE4Parse.UE4.Objects.Core.Math.FVector v) => new(v.X, v.Y, v.Z);
        return new NavTile
        {
            X = tile.Header.X, Y = tile.Header.Y, Layer = tile.Header.Layer,
            Verts = tile.Vertices.Select(V).ToArray(),
            Polys = tile.Polys.Select(p => new NavPoly(p.Verts.Take(Math.Min((int) p.VertCount, p.Verts.Length)).ToArray(),
                (byte) (p.AreaAndType & 0x3F), p.AreaAndType >> 6 == 1, p.Flags)).ToArray(),
            Details = tile.DetailMeshes.Select(m => new NavPolyDetail((int) m.VertBase, (int) m.TriBase, m.VertCount, m.TriCount)).ToArray(),
            DetailVerts = tile.DetailVertices.Select(V).ToArray(),
            DetailTris = tile.DetailTris.SelectMany(t => t.Take(4)).ToArray(),
            BMin = V(tile.Header.BMin), BMax = V(tile.Header.BMax)
        };
    }

    /// <summary>The navmesh as JSON-friendly data for the asset tab: parameters, then each tile's position and counts.</summary>
    public object Summary() => new
    {
        Type = "NavMesh",
        Name,
        Tiles = Tiles.Length,
        Polygons = PolyCount,
        OffMeshLinks = Tiles.Sum(t => t.Polys.Count(p => p.IsOffMeshLink)),
        Areas = Tiles.SelectMany(t => t.Polys).Where(p => !p.IsOffMeshLink).GroupBy(p => p.Area).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count()),
        Parameters,
        TileList = Tiles.Select(t => new
        {
            t.X, t.Y, t.Layer, Polygons = t.Polys.Length, Vertices = t.Verts.Length, DetailTriangles = t.DetailTris.Length / 4,
            BMin = new[] { t.BMin.X, t.BMin.Y, t.BMin.Z }, BMax = new[] { t.BMax.X, t.BMax.Y, t.BMax.Z }
        })
    };
}
