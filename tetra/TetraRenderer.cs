using System.Collections.Generic;
using Data.Shapes;
using HarmonyLib;
using Logic.Shapes;
using ScriptEngine;
using UnityEngine;
using UnityEngine.Rendering;

[ScriptEntry]
public sealed class TetraRenderer : ScriptMod
{
    protected override void OnEnable()
    {
        ClearShapeMeshCaches();
    }

    protected override void OnDisable()
    {
        ClearShapeMeshCaches();
    }

    public static void ClearShapeMeshCaches()
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

[HarmonyPatch(typeof(ShapeMeshLibrary), nameof(ShapeMeshLibrary.CreateMesh), new[] { typeof(Shape) })]
class ShapeMeshLibrary_CreateMesh_Patch
{
    static bool Prefix(Shape shape, ref Mesh __result)
    {
        __result = TetraMeshBuilder.Build(shape);
        return false;
    }
}

static class TetraMeshBuilder
{
    const float S = 0.1f;
    const float H = 0.08660254f;
    const float R = 0.046f;
    const float R3 = R / 3f;
    const float RR = R * 0.94281f;
    const float SQ3_2 = 0.86603f;

    static readonly Vector3[] VUp =
    {
        new Vector3(0, R, 0),
        new Vector3(RR, -R3, 0),
        new Vector3(-RR * 0.5f, -R3, RR * SQ3_2),
        new Vector3(-RR * 0.5f, -R3, -RR * SQ3_2),
    };

    static readonly Vector3[] VDn =
    {
        new Vector3(0, -R, 0),
        new Vector3(RR, R3, 0),
        new Vector3(-RR * 0.5f, R3, -RR * SQ3_2),
        new Vector3(-RR * 0.5f, R3, RR * SQ3_2),
    };

    static readonly int[] FI = { 0, 2, 1, 0, 3, 2, 0, 1, 3, 1, 2, 3 };

    static Mesh BuildAtom(Vector3[] v, int[] fi, Color color)
    {
        var verts = new Vector3[12];
        var norms = new Vector3[12];
        var cols = new Color[12];
        var uvs = new Vector2[12];

        for (int f = 0; f < 4; f++)
        {
            int b = f * 3;
            var a = v[fi[b]];
            var bv = v[fi[b + 1]];
            var c = v[fi[b + 2]];
            var n = Vector3.Cross(bv - a, c - a).normalized;

            verts[b] = a;
            verts[b + 1] = bv;
            verts[b + 2] = c;
            norms[b] = norms[b + 1] = norms[b + 2] = n;
            cols[b] = cols[b + 1] = cols[b + 2] = color;
            uvs[b] = uvs[b + 1] = uvs[b + 2] = new Vector2(0.5f, 0.5f);
        }

        int[] tris = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        var mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetNormals(norms);
        mesh.SetColors(cols);
        mesh.SetUVs(0, uvs);
        mesh.SetUVs(1, uvs);
        return mesh;
    }

    static Vector3 TriPos(Vector3Int g, Vector3Int bounds)
    {
        float wx = (g.x + g.z * 0.5f) * S;
        float wy = g.y * S;
        float wz = g.z * H;

        float cx = ((bounds.x - 1) + (bounds.z - 1) * 0.5f) * S * 0.5f;
        float cz = (bounds.z - 1) * H * 0.5f;

        return new Vector3(wx - cx, wy, wz - cz);
    }

    public static Mesh Build(Shape shape)
    {
        var bounds = shape.GetBounds();
        var combines = new List<CombineInstance>(shape.OccupiedVoxels.Count);

        foreach (var voxel in shape.OccupiedVoxels)
        {
            var g = voxel.Position;
            bool even = (g.x + g.z) % 2 == 0;
            var atom = BuildAtom(even ? VUp : VDn, FI, voxel.Color);

            combines.Add(new CombineInstance
            {
                mesh = atom,
                transform = Matrix4x4.Translate(TriPos(g, bounds)),
            });
        }

        var result = new Mesh { indexFormat = IndexFormat.UInt32 };
        result.CombineMeshes(combines.ToArray());

        foreach (var combine in combines)
            Object.DestroyImmediate(combine.mesh);

        return result;
    }
}
