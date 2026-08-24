using UnityEngine;

public static class VegetationMeshUtility
{
    // Two crossed vertical quads. UV.y == 0 at the root (unaffected by wind), UV.y == 1 at the tip.
    public static Mesh CreateCrossQuad(float width = 0.5f, float height = 0.8f)
    {
        var mesh = new Mesh { name = "VegetationCrossQuad" };

        var vertices = new Vector3[8];
        var normals = new Vector3[8];
        var uvs = new Vector2[8];
        var triangles = new int[12];

        float hw = width * 0.5f;

        // Quad A: along X axis
        vertices[0] = new Vector3(-hw, 0f, 0f);
        vertices[1] = new Vector3(hw, 0f, 0f);
        vertices[2] = new Vector3(-hw, height, 0f);
        vertices[3] = new Vector3(hw, height, 0f);

        // Quad B: along Z axis
        vertices[4] = new Vector3(0f, 0f, -hw);
        vertices[5] = new Vector3(0f, 0f, hw);
        vertices[6] = new Vector3(0f, height, -hw);
        vertices[7] = new Vector3(0f, height, hw);

        uvs[0] = new Vector2(0f, 0f);
        uvs[1] = new Vector2(1f, 0f);
        uvs[2] = new Vector2(0f, 1f);
        uvs[3] = new Vector2(1f, 1f);
        uvs[4] = new Vector2(0f, 0f);
        uvs[5] = new Vector2(1f, 0f);
        uvs[6] = new Vector2(0f, 1f);
        uvs[7] = new Vector2(1f, 1f);

        for (int i = 0; i < 4; i++) normals[i] = Vector3.forward;
        for (int i = 4; i < 8; i++) normals[i] = Vector3.right;

        triangles[0] = 0; triangles[1] = 2; triangles[2] = 1;
        triangles[3] = 1; triangles[4] = 2; triangles[5] = 3;

        triangles[6] = 4; triangles[7] = 6; triangles[8] = 5;
        triangles[9] = 5; triangles[10] = 6; triangles[11] = 7;

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        return mesh;
    }
}
