off
using HarmonyLib;
using Data.Shapes;
using Logic.Shapes;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

public static class TetraRenderer
{
    static readonly HarmonyLib.Harmony _h = new HarmonyLib.Harmony("tetra-renderer");

    public static void OnLoad()
    {
        _h.UnpatchSelf();
        _h.PatchAll(typeof(TetraRenderer).Assembly);
        ClearShapeMeshCaches();
    }

    public static void OnUnload()
    {
        _h.UnpatchSelf();
    }

    static void ClearShapeMeshCaches()
    {
        foreach (var library in Resources.FindObjectsOfTypeAll<ShapeMeshLibrary>())
        {
            var meshes = Traverse.Create(library)
                .Field("_meshes")
                .GetValue<Dictionary<ShapeHashPair, Mesh>>();

            meshes?.Clear();
        }
    }
}

// Replace the static mesh-builder so all ShapeMeshLibrary instances use tetra geometry.
[HarmonyPatch(typeof(ShapeMeshLibrary), nameof(ShapeMeshLibrary.CreateMesh), new[] { typeof(Shape) })]
class ShapeMeshLibrary_CreateMesh_Patch
{
    static bool Prefix(Shape shape, ref Mesh __result)
    {
        __result = TetraMeshBuilder.Build(shape);
        return false; // skip original
    }
}

static class TetraMeshBuilder
{
    // Grid constants — same world scale as the original cube grid.
    const float S = 0.1f;           // X-spacing between atom columns
    const float H = 0.08660254f;    // Z-spacing between rows = S * sqrt(3)/2

    // Regular tetrahedron circumradius — slightly under half-cell for a small gap.
    const float R     = 0.046f;
    const float R3    = R / 3f;                        // base Y offset from centroid
    const float RR    = R * 0.94281f;                  // base circumradius = R * 2√2/3
    const float SQ3_2 = 0.86603f;                      // √3/2

    // Y-up: apex at +Y, equilateral base at −Y.
    // Viewed from the side a column looks like stacked triangles; top is always a point.
    static readonly Vector3[] VUp =
    {
        new Vector3(0,          R,   0),           // 0 — apex
        new Vector3(RR,        -R3,  0),           // 1 — base 0°
        new Vector3(-RR*0.5f,  -R3,  RR * SQ3_2), // 2 — base 120°
        new Vector3(-RR*0.5f,  -R3, -RR * SQ3_2), // 3 — base 240°
    };

    // Y-down: apex at −Y, equilateral base at +Y.
    // VUp rotated 180° around X → same winding, top is a flat triangle face.
    static readonly Vector3[] VDn =
    {
        new Vector3(0,          -R,   0),
        new Vector3(RR,          R3,  0),
        new Vector3(-RR*0.5f,    R3, -RR * SQ3_2),
        new Vector3(-RR*0.5f,    R3,  RR * SQ3_2),
    };

    // Same outward-facing winding for both types (proper rotation preserves handedness).
    // 3 side faces: (apex,b2,b1) (apex,b3,b2) (apex,b1,b3) — base: (b1,b2,b3)
    static readonly int[] FI = { 0,2,1,  0,3,2,  0,1,3,  1,2,3 };

    static Mesh BuildAtom(Vector3[] v, int[] fi, Color color)
    {
        // 4 faces × 3 vertices = 12, no vertex sharing (flat normals per face).
        var verts = new Vector3[12];
        var norms = new Vector3[12];
        var cols  = new Color[12];
        var uvs   = new Vector2[12];

        for (int f = 0; f < 4; f++)
        {
            int b = f * 3;
            var a  = v[fi[b]];
            var bv = v[fi[b + 1]];
            var c  = v[fi[b + 2]];
            var n  = Vector3.Cross(bv - a, c - a).normalized;

            verts[b] = a;  verts[b + 1] = bv;  verts[b + 2] = c;
            norms[b] = norms[b + 1] = norms[b + 2] = n;
            cols[b]  = cols[b + 1]  = cols[b + 2]  = color;
            // UV 0.5 = "not an edge" in the original shader's edge-detection UVs.
            uvs[b]   = uvs[b + 1]   = uvs[b + 2]   = new Vector2(0.5f, 0.5f);
        }

        int[] tris = { 0,1,2, 3,4,5, 6,7,8, 9,10,11 };
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.SetNormals(norms);
        m.SetColors(cols);
        m.SetUVs(0, uvs);
        m.SetUVs(1, uvs);
        return m;
    }

    // Map integer voxel grid coords → triangular-grid world position.
    // X rows are staggered by half a cell per Z-row, Z rows are compressed
    // to triangle-height spacing.  Y is unchanged (layers stack normally).
    static Vector3 TriPos(Vector3Int g, Vector3Int bounds)
    {
        float wx = (g.x + g.z * 0.5f) * S;
        float wy = g.y * S;
        float wz = g.z * H;

        // Centre in XZ (not Y), matching the original cube-grid centring.
        float cx = ((bounds.x - 1) + (bounds.z - 1) * 0.5f) * S * 0.5f;
        float cz = (bounds.z - 1) * H * 0.5f;

        return new Vector3(wx - cx, wy, wz - cz);
    }

    public static Mesh Build(Shape shape)
    {
        var bounds   = shape.GetBounds();
        var combines = new List<CombineInstance>(shape.OccupiedVoxels.Count);

        foreach (var voxel in shape.OccupiedVoxels)
        {
            var g    = voxel.Position;
            // XZ checkerboard: even cells point up, odd cells point down.
            // A 1×1×N column always hits even parity → all point up → top is a vertex.
            bool even = (g.x + g.z) % 2 == 0;
            var atom = BuildAtom(even ? VUp : VDn, FI, voxel.Color);

            combines.Add(new CombineInstance
            {
                mesh      = atom,
                transform = Matrix4x4.Translate(TriPos(g, bounds)),
            });
        }

        var result = new Mesh { indexFormat = IndexFormat.UInt32 };
        result.CombineMeshes(combines.ToArray());

        foreach (var ci in combines)
            Object.DestroyImmediate(ci.mesh);

        return result;
    }
}
