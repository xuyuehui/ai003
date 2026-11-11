using UnityEngine;

namespace AI003.World
{
    /// <summary>
    /// Procedural stand-ins for the Ashkar set dressing: the hourglass wellspire,
    /// the sunken Well One plaza, the sail-sled and a scale figure. All meshes are
    /// generated in code (no imported assets), so the demo stays self-contained.
    /// </summary>
    public static class AshkarProps
    {
        public static Material Material(string name, Color color, float smoothness = 0.1f, float metallic = 0f)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(shader) { name = name };
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            return m;
        }

        public static Material Emissive(string name, Color color, Color emission)
        {
            var m = Material(name, color, 0.4f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            return m;
        }

        /// <summary>Open-ended cone/cylinder shell. rBottom at y=0, rTop at y=height.</summary>
        public static Mesh Frustum(float rBottom, float rTop, float height, int segments = 24, bool capTop = true, bool capBottom = false)
        {
            int ringVerts = segments + 1;
            int rings = 2;
            var verts = new Vector3[ringVerts * rings + (capTop ? 1 : 0) + (capBottom ? 1 : 0)];
            var normals = new Vector3[verts.Length];
            var uvs = new Vector2[verts.Length];
            var tris = new System.Collections.Generic.List<int>();

            float slope = (rBottom - rTop) / Mathf.Max(height, 0.0001f);
            for (int r = 0; r < rings; r++)
            {
                float t = r;
                float rad = Mathf.Lerp(rBottom, rTop, t);
                for (int i = 0; i <= segments; i++)
                {
                    float a = i / (float)segments * Mathf.PI * 2f;
                    int vi = r * ringVerts + i;
                    verts[vi] = new Vector3(Mathf.Cos(a) * rad, t * height, Mathf.Sin(a) * rad);
                    var n = new Vector3(Mathf.Cos(a), slope, Mathf.Sin(a)).normalized;
                    normals[vi] = n;
                    uvs[vi] = new Vector2(i / (float)segments, t);
                }
            }
            for (int i = 0; i < segments; i++)
            {
                int a = i;
                int b = i + 1;
                int c = ringVerts + i;
                int d = ringVerts + i + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }

            int next = ringVerts * rings;
            if (capTop)
            {
                verts[next] = new Vector3(0f, height, 0f);
                normals[next] = Vector3.up;
                uvs[next] = new Vector2(0.5f, 0.5f);
                for (int i = 0; i < segments; i++)
                {
                    tris.Add(ringVerts + i);
                    tris.Add(next);
                    tris.Add(ringVerts + i + 1);
                }
                next++;
            }
            if (capBottom)
            {
                verts[next] = Vector3.zero;
                normals[next] = Vector3.down;
                uvs[next] = new Vector2(0.5f, 0.5f);
                for (int i = 0; i < segments; i++)
                {
                    tris.Add(next);
                    tris.Add(i);
                    tris.Add(i + 1);
                }
            }

            var mesh = new Mesh { name = "Frustum" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Box(Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(go);
            mesh.name = "Box";
            var verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = Vector3.Scale(verts[i], size);
            }
            mesh.vertices = verts;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh Quad(float width, float height)
        {
            var mesh = new Mesh { name = "Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-width * 0.5f, 0f, 0f),
                new Vector3(width * 0.5f, 0f, 0f),
                new Vector3(-width * 0.5f, height, 0f),
                new Vector3(width * 0.5f, height, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        public static GameObject Part(string name, Transform parent, Mesh mesh, Material material,
            Vector3 localPos, Vector3 localEuler = default, Vector3 localScale = default)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = localEuler;
            go.transform.localScale = localScale == default ? Vector3.one : localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
            return go;
        }

        /// <summary>Inverted-cone wellspire: plinth, funnel shaft, top platform, antenna.</summary>
        public static GameObject Wellspire(Vector3 position, float height, Material body, Material trim)
        {
            var root = new GameObject("Wellspire");
            root.transform.position = position;

            float baseR = height * 0.035f;
            float topR = height * 0.155f;

            Part("Plinth", root.transform, Box(new Vector3(topR * 1.1f, height * 0.03f, topR * 1.1f)),
                body, new Vector3(0f, height * 0.015f, 0f));
            Part("Shaft", root.transform, Frustum(baseR, topR, height * 0.94f, 20, false, false),
                body, new Vector3(0f, height * 0.03f, 0f));
            Part("Platform", root.transform, Frustum(topR * 1.12f, topR * 1.12f, height * 0.02f, 20, true, true),
                trim, new Vector3(0f, height * 0.97f, 0f));
            Part("Spire", root.transform, Frustum(height * 0.012f, height * 0.004f, height * 0.08f, 8, false, false),
                trim, new Vector3(0f, height * 0.99f, 0f));

            // A few deep windows: thin emissive slabs let the light read at distance.
            var windowMat = Emissive("WellspireWindow", new Color(0.05f, 0.04f, 0.03f),
                new Color(1.0f, 0.62f, 0.28f) * 1.6f);
            for (int i = 0; i < 4; i++)
            {
                float t = 0.28f + i * 0.16f;
                float r = Mathf.Lerp(baseR, topR, t) * 0.98f;
                float w = height * 0.022f;
                var win = Part($"Window{i}", root.transform, Box(new Vector3(w, height * 0.012f, w * 0.4f)),
                    windowMat, new Vector3(0f, height * t, r), new Vector3(0f, 0f, 0f));
                win.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
            }

            return root;
        }

        /// <summary>Sunken circular plaza with a ring wall, four posts and a bell frame.</summary>
        public static GameObject WellOne(Vector3 position, float radius, Material saltBrick, Material wood, Material bell)
        {
            var root = new GameObject("WellOne");
            root.transform.position = position;

            Part("Floor", root.transform, Frustum(radius, radius, 0.4f, 32, true, false),
                saltBrick, new Vector3(0f, -0.4f, 0f));
            Part("Ring", root.transform, Frustum(radius, radius * 1.04f, 3.2f, 32, false, false),
                saltBrick, new Vector3(0f, 0f, 0f));

            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + 0.4f;
                var post = Part($"Post{i}", root.transform, Box(new Vector3(0.5f, 4.2f, 0.5f)),
                    wood, new Vector3(Mathf.Cos(a) * radius * 0.72f, 2.1f, Mathf.Sin(a) * radius * 0.72f));
                post.transform.localRotation = Quaternion.identity;
            }

            // Bell frame + bell.
            Part("BellBeam", root.transform, Box(new Vector3(4.2f, 0.35f, 0.35f)),
                wood, new Vector3(radius * 0.45f, 5.2f, radius * 0.45f));
            Part("BellPostA", root.transform, Box(new Vector3(0.3f, 5.2f, 0.3f)),
                wood, new Vector3(radius * 0.45f - 1.8f, 2.6f, radius * 0.45f));
            Part("BellPostB", root.transform, Box(new Vector3(0.3f, 5.2f, 0.3f)),
                wood, new Vector3(radius * 0.45f + 1.8f, 2.6f, radius * 0.45f));
            Part("Bell", root.transform, Frustum(0.75f, 0.35f, 1.1f, 12, true, false),
                bell, new Vector3(radius * 0.45f, 4.0f, radius * 0.45f));

            return root;
        }

        /// <summary>Three-skid sail-sled: skids, deck, mast, curved sail, cargo basket.</summary>
        public static GameObject SailSled(Vector3 position, float yaw, Material hull, Material sailMat, Material trim)
        {
            var root = new GameObject("SailSled");
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            for (int i = -1; i <= 1; i++)
            {
                Part($"Skid{i + 1}", root.transform, Box(new Vector3(0.26f, 0.22f, 4.2f)),
                    trim, new Vector3(i * 0.95f, 0.16f, 0f));
            }
            Part("Deck", root.transform, Box(new Vector3(2.4f, 0.16f, 3.6f)),
                hull, new Vector3(0f, 0.42f, -0.1f));
            Part("Basket", root.transform, Box(new Vector3(1.5f, 1.0f, 1.4f)),
                trim, new Vector3(0f, 1.0f, 1.1f));
            Part("Mast", root.transform, Frustum(0.16f, 0.10f, 5.2f, 8, false, false),
                trim, new Vector3(0f, 0.5f, -1.2f));
            Part("Yard", root.transform, Box(new Vector3(3.8f, 0.12f, 0.12f)),
                trim, new Vector3(0f, 5.0f, -1.2f));
            var sail = Part("Sail", root.transform, Quad(3.6f, 3.4f), sailMat, new Vector3(0f, 1.5f, -1.15f));
            sail.transform.localRotation = Quaternion.Euler(0f, 0f, 8f);

            return root;
        }

        /// <summary>Robed human silhouette: cone body + head. Height is the real height in metres.</summary>
        public static GameObject Figure(Vector3 position, float yaw, float height, Material robe)
        {
            var root = new GameObject("Figure");
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            float bodyH = height * 0.78f;
            Part("Body", root.transform, Frustum(height * 0.13f, height * 0.055f, bodyH, 10, true, true),
                robe, Vector3.zero);
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(head.GetComponent<Collider>());
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, bodyH + height * 0.09f, 0f);
            head.transform.localScale = Vector3.one * height * 0.18f;
            head.GetComponent<MeshRenderer>().sharedMaterial = robe;
            return root;
        }

        /// <summary>Sharp crystal shards (coldfire).</summary>
        public static GameObject CrystalCluster(Vector3 position, int count, float scale, Material crystal, int seed)
        {
            var root = new GameObject("Coldfire");
            root.transform.position = position;
            // A real point light under the cluster: it is what puts a cold pool of
            // light on the salt, instead of only a glowing object.
            var glow = new GameObject("ColdfireLight");
            glow.transform.SetParent(root.transform, false);
            glow.transform.localPosition = new Vector3(0f, scale * 0.9f, 0f);
            var light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.38f, 0.95f, 0.86f);
            light.intensity = 5.5f * scale;
            light.range = 34f;
            light.shadows = LightShadows.None;

            var rng = new System.Random(seed);
            for (int i = 0; i < count; i++)
            {
                float cx = (float)(rng.NextDouble() - 0.5) * scale * 1.6f;
                float cz = (float)(rng.NextDouble() - 0.5) * scale * 1.6f;
                float h = scale * (0.45f + (float)rng.NextDouble() * 1.1f);
                float r = scale * (0.05f + (float)rng.NextDouble() * 0.07f);
                var shard = Part($"Shard{i}", root.transform, Frustum(r, r * 0.05f, h, 5, false, false),
                    crystal, new Vector3(cx, 0f, cz),
                    new Vector3((float)rng.NextDouble() * 18f - 9f, 0f, (float)rng.NextDouble() * 18f - 9f));
                shard.transform.localRotation = Quaternion.Euler(
                    (float)rng.NextDouble() * 10f - 5f,
                    (float)rng.NextDouble() * 360f,
                    (float)rng.NextDouble() * 10f - 5f);
            }
            return root;
        }
    }
}
