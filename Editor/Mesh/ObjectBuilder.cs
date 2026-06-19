using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Mesh
{
    /// <summary>
    /// Builds static GameObjects from WoT .primitives_processed meshes.
    /// This is a simplified implementation that handles the basic vertex
    /// format (xyznuv) and the standard transform layout. For full
    /// atlas / tiled shader support, extend MeshDecoder + per-material logic.
    /// </summary>
    public static class ObjectBuilder
    {
        public class BuildResult
        {
            public GameObject Root;
            public List<GameObject> CreatedObjects = new List<GameObject>();
            public List<string> Warnings = new List<string>();
        }

        public struct ObjectTransform
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        public static BuildResult Build(
            string outputPath,
            string primsName,
            string vertsName,
            List<ObjectTransform> transforms,
            List<string> texturePaths,
            WoTPackageManager resMgr)
        {
            var result = new BuildResult();
            byte[] data = resMgr.ReadBytes(primsName);
            if (data == null)
            {
                result.Warnings.Add($"prims not found: {primsName}");
                return result;
            }

            MeshDataDecoder.DecodedMesh decoded;
            try
            {
                decoded = MeshDataDecoder.Decode(data);
            }
            catch (Exception e)
            {
                result.Warnings.Add($"Failed to decode {primsName}: {e.Message}");
                return result;
            }

            // Build Unity mesh
            var umesh = new UnityEngine.Mesh
            {
                name = PathName(primsName),
                indexFormat = decoded.Positions.Length > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            umesh.vertices = decoded.Positions;
            umesh.uv = decoded.Uv;
            umesh.triangles = decoded.Indices;
            umesh.RecalculateNormals();
            umesh.RecalculateBounds();

            // Apply XZY -> XYZ flip (Blender addon uses flip_mat; we apply same)
            var flip = new Vector3[decoded.Positions.Length];
            for (int i = 0; i < decoded.Positions.Length; i++)
                flip[i] = new Vector3(decoded.Positions[i].x, decoded.Positions[i].z, decoded.Positions[i].y);
            umesh.vertices = flip;

            // Material
            Material mat = null;
            if (texturePaths != null && texturePaths.Count > 0 && !string.IsNullOrEmpty(texturePaths[0]))
            {
                byte[] texData = resMgr.ReadBytes(texturePaths[0]);
                if (texData != null)
                {
                    var tex = LoadTex(texData, texturePaths[0]);
                    if (tex != null)
                    {
                        Shader sh = Shader.Find("Standard");
                        mat = new Material(sh) { name = PathName(texturePaths[0]) + "_mat" };
                        mat.mainTexture = tex;
                    }
                }
            }
            if (mat == null)
            {
                Shader sh = Shader.Find("Standard");
                mat = new Material(sh) { name = "WoT_DefaultMat" };
                mat.color = Color.gray;
            }

            // Create root
            result.Root = new GameObject(PathName(primsName));
            var mf = result.Root.AddComponent<MeshFilter>();
            mf.sharedMesh = umesh;
            var mr = result.Root.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;

            // For each transform, create a child instance. (Or batch into one
            // big mesh with combined transforms for performance - skipped here.)
            if (transforms != null)
            {
                foreach (var t in transforms)
                {
                    var inst = new GameObject(PathName(primsName) + "_inst");
                    inst.transform.SetParent(result.Root.transform, false);

                    var mfi = inst.AddComponent<MeshFilter>();
                    mfi.sharedMesh = umesh;
                    var mri = inst.AddComponent<MeshRenderer>();
                    mri.sharedMaterial = mat;

                    // Build matrix from rows (WoT format). Apply XZY -> XYZ flip.
                    var mat4 = new Matrix4x4();
                    mat4.SetColumn(0, new Vector4(t.Row0.x, t.Row0.z, t.Row0.y, t.Row0.w));
                    mat4.SetColumn(1, new Vector4(t.Row1.x, t.Row1.z, t.Row1.y, t.Row1.w));
                    mat4.SetColumn(2, new Vector4(t.Row2.x, t.Row2.z, t.Row2.y, t.Row2.w));
                    mat4.SetColumn(3, new Vector4(t.Row3.x, t.Row3.z, t.Row3.y, 1f));

                    inst.transform.position = mat4.GetColumn(3);
                    // For rotation/scale, use TRS decomposition if matrix is uniform;
                    // otherwise just use matrix (which Unity supports via transform)
                    inst.transform.localScale = new Vector3(
                        mat4.GetColumn(0).magnitude,
                        mat4.GetColumn(1).magnitude,
                        mat4.GetColumn(2).magnitude);
                    Quaternion q = Quaternion.LookRotation(
                        new Vector3(mat4.GetColumn(2).x, mat4.GetColumn(2).y, mat4.GetColumn(2).z),
                        new Vector3(mat4.GetColumn(1).x, mat4.GetColumn(1).y, mat4.GetColumn(1).z));
                    inst.transform.rotation = q;
                    result.CreatedObjects.Add(inst);
                }
            }
            else
            {
                result.CreatedObjects.Add(result.Root);
            }

            // Save mesh
            string meshPath = $"{outputPath}/Meshes/{PathName(primsName)}.asset";
            EnsureFolder(Path.GetDirectoryName(meshPath).Replace('\\', '/'));
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(meshPath) != null)
                AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(umesh, meshPath);

            return result;
        }

        private static Texture2D LoadTex(byte[] data, string name)
        {
            if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == Image.DdsDecoder.MAGIC)
                return Image.DdsDecoder.Read(data, name);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
            {
                name = System.IO.Path.GetFileNameWithoutExtension(name),
                wrapMode = TextureWrapMode.Repeat,
            };
            if (tex.LoadImage(data, false)) return tex;
            UnityEngine.Object.DestroyImmediate(tex);
            return null;
        }

        private static string PathName(string s)
        {
            string n = s.Replace('\\', '/');
            int idx = n.LastIndexOf('/');
            string baseN = idx >= 0 ? n.Substring(idx + 1) : n;
            return System.IO.Path.GetFileNameWithoutExtension(baseN);
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = System.IO.Path.GetDirectoryName(folderPath).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
