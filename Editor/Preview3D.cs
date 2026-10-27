using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using M3 = System.Windows.Media.Media3D;
using NV = System.Numerics;

namespace BuildingEditor
{
    public enum ModelLook { Solid, SeeThrough }

    /// <summary>
    /// GPU-drawn 3D view (WPF 3D hosted in WinForms): the floor plans as a dollhouse, optionally inside the game's real
    /// exterior model with its textures, lit windows at night, and water and a pier around it.
    /// Left-drag rotates, right/middle-drag pans, wheel zooms, double-click resets.
    /// </summary>
    public sealed class Preview3D : WF.UserControl
    {
        private readonly WF.Integration.ElementHost _host = new() { Dock = WF.DockStyle.Fill };
        private readonly WC.Border _border = new();
        private readonly WC.Viewport3D _viewport = new();
        private readonly M3.PerspectiveCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = 0.1, FarPlaneDistance = 10000 };
        private readonly M3.Model3DGroup _lights = new();
        private readonly M3.Model3DGroup _floors = new();
        private readonly M3.Model3DGroup _model = new();
        private readonly M3.Model3DGroup _env = new();
        private readonly Dictionary<string, WM.ImageBrush> _textures = new(StringComparer.OrdinalIgnoreCase);

        private List<Face> _faces = new();
        private LoadedModel _loaded;
        private NV.Matrix4x4 _modelXf = NV.Matrix4x4.Identity;

        private float _yaw = -0.75f, _pitch = 0.55f, _dist = 60f;
        private NV.Vector3 _target, _sceneCenter;
        private float _sceneRadius = 30f;
        private W.Point _last;
        private WI.MouseButton? _drag;

        /// <summary>Storeys above / below these levels are hidden (cutaway).</summary>
        public int MaxLevel = int.MaxValue;
        public int MinLevel = int.MinValue;
        /// <summary>Model triangles above this height are cut away, so the cutaway shows the inside.</summary>
        public float ModelCutY = float.MaxValue;
        public bool ShowModel = true;
        public ModelLook Look = ModelLook.Solid;
        public bool Night;
        public bool Environment = true;
        /// <summary>Extent of the floor plans (world X/Z) for the pier; set by the form.</summary>
        public NV.Vector2 FootprintMin, FootprintMax;

        public Preview3D()
        {
            var grid = new WC.Grid();
            grid.Children.Add(_viewport);
            _border.Child = grid;
            _host.Child = _border;
            Controls.Add(_host);

            _viewport.Camera = _camera;
            var root = new M3.Model3DGroup();
            root.Children.Add(_lights);
            root.Children.Add(_env);
            root.Children.Add(_floors);
            root.Children.Add(_model);   // after floors so a see-through model blends over them
            _viewport.Children.Add(new M3.ModelVisual3D { Content = root });

            _border.MouseDown += OnDown;
            _border.MouseMove += OnMove;
            _border.MouseUp += OnUp;
            _border.MouseWheel += OnWheel;

            ApplyLighting();
        }

        public int FaceCount => _faces.Count;
        public int ModelTriangles => _loaded?.TriangleCount ?? 0;

        // ------------------------------------------------------------------ scene input

        public void SetFaces(List<Face> faces, bool keepCamera)
        {
            _faces = faces ?? new List<Face>();
            UpdateSceneBounds();
            RebuildFloors();
            RebuildEnvironment();
            if (!keepCamera) ResetView(); else UpdateCamera();
        }

        public void SetModel(LoadedModel model, NV.Matrix4x4 transform, bool keepCamera)
        {
            _loaded = model;
            _modelXf = transform;
            UpdateSceneBounds();
            RebuildModel();
            RebuildEnvironment();
            if (!keepCamera) ResetView(); else UpdateCamera();
        }

        /// <summary>Re-applies cutaway, look, night and environment settings.</summary>
        public void RefreshScene()
        {
            ApplyLighting();
            RebuildFloors();
            RebuildModel();
            RebuildEnvironment();
        }

        private void UpdateSceneBounds()
        {
            var min = new NV.Vector3(float.MaxValue);
            var max = new NV.Vector3(float.MinValue);
            bool any = false;
            foreach (var f in _faces)
                foreach (var p in f.P) { min = NV.Vector3.Min(min, p); max = NV.Vector3.Max(max, p); any = true; }
            if (_loaded != null && ShowModel)
            {
                foreach (var corner in Corners(_loaded.Min, _loaded.Max))
                {
                    var p = NV.Vector3.Transform(corner, _modelXf);
                    min = NV.Vector3.Min(min, p); max = NV.Vector3.Max(max, p); any = true;
                }
            }
            if (!any) return;
            _sceneCenter = (min + max) / 2f;
            _sceneRadius = Math.Max(5f, (max - min).Length() / 2f);
        }

        private static IEnumerable<NV.Vector3> Corners(NV.Vector3 a, NV.Vector3 b)
        {
            for (int i = 0; i < 8; i++)
                yield return new NV.Vector3((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        }

        // ------------------------------------------------------------------ building geometry

        private static M3.Point3D P3(NV.Vector3 v) => new(v.X, v.Y, v.Z);
        private static WM.Color C(SD.Color c) => WM.Color.FromArgb(c.A, c.R, c.G, c.B);

        private void RebuildFloors()
        {
            _floors.Children.Clear();
            var visible = _faces.Where(f => f.Level <= MaxLevel && f.Level >= MinLevel);
            var groups = visible.GroupBy(f => f.Color.ToArgb()).OrderBy(g => SD.Color.FromArgb(g.Key).A < 255 ? 1 : 0);
            foreach (var g in groups)
            {
                var mesh = new M3.MeshGeometry3D();
                var pos = new M3.Point3DCollection();
                var idx = new WM.Int32Collection();
                foreach (var f in g)
                {
                    int b = pos.Count;
                    foreach (var p in f.P) pos.Add(P3(p));
                    for (int i = 1; i + 1 < f.P.Length; i++) { idx.Add(b); idx.Add(b + i); idx.Add(b + i + 1); }
                }
                mesh.Positions = pos;
                mesh.TriangleIndices = idx;
                mesh.Freeze();
                var brush = new WM.SolidColorBrush(C(SD.Color.FromArgb(g.Key)));
                brush.Freeze();
                var mat = new M3.DiffuseMaterial(brush);
                mat.Freeze();
                _floors.Children.Add(new M3.GeometryModel3D(mesh, mat) { BackMaterial = mat });
            }
        }

        private void RebuildModel()
        {
            _model.Children.Clear();
            if (_loaded == null || !ShowModel) return;
            double opacity = Look == ModelLook.SeeThrough ? 0.28 : 1.0;

            foreach (var m in _loaded.Meshes)
            {
                var pos = new M3.Point3DCollection(m.Pos.Count);
                var world = new NV.Vector3[m.Pos.Count];
                for (int i = 0; i < m.Pos.Count; i++)
                {
                    world[i] = NV.Vector3.Transform(m.Pos[i], _modelXf);
                    pos.Add(P3(world[i]));
                }
                var idx = new WM.Int32Collection(m.Idx.Count);
                for (int i = 0; i + 2 < m.Idx.Count; i += 3)
                {
                    int a = m.Idx[i], b = m.Idx[i + 1], c = m.Idx[i + 2];
                    if (world[a].Y > ModelCutY + 0.01f || world[b].Y > ModelCutY + 0.01f || world[c].Y > ModelCutY + 0.01f) continue;
                    idx.Add(a); idx.Add(b); idx.Add(c);
                }
                if (idx.Count == 0) continue;

                var mesh = new M3.MeshGeometry3D { Positions = pos, TriangleIndices = idx };
                if (m.HasNormals)
                {
                    var normalXf = NV.Matrix4x4.Invert(_modelXf, out var inv) ? NV.Matrix4x4.Transpose(inv) : _modelXf;
                    var nrm = new M3.Vector3DCollection(m.Nrm.Count);
                    foreach (var n in m.Nrm)
                    {
                        var t = NV.Vector3.Normalize(NV.Vector3.TransformNormal(n, normalXf));
                        nrm.Add(new M3.Vector3D(t.X, t.Y, t.Z));
                    }
                    mesh.Normals = nrm;
                }
                if (m.HasUv)
                {
                    var uv = new WM.PointCollection(m.Uv.Count);
                    foreach (var t in m.Uv) uv.Add(new W.Point(t.X, 1 - t.Y)); // OBJ v goes up, WPF goes down
                    mesh.TextureCoordinates = uv;
                }
                mesh.Freeze();

                var group = new M3.MaterialGroup();
                WM.Brush diffuse = m.TexturePath != null && m.HasUv ? Texture(m.TexturePath, opacity)
                                                                   : Solid(WM.Color.FromRgb(150, 150, 140), opacity);
                group.Children.Add(new M3.DiffuseMaterial(diffuse));
                if (Night && m.EmissionPath != null && m.HasUv)
                    group.Children.Add(new M3.EmissiveMaterial(Texture(m.EmissionPath, opacity)));
                group.Freeze();
                _model.Children.Add(new M3.GeometryModel3D(mesh, group) { BackMaterial = group });
            }
        }

        /// <summary>Forget loaded textures (after the exterior was painted).</summary>
        public void ClearTextureCache() => _textures.Clear();

        private WM.Brush Texture(string path, double opacity)
        {
            var key = path + "|" + opacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_textures.TryGetValue(key, out var b)) return b;
            try
            {
                var img = new WMI.BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(Path.GetFullPath(path));
                img.CacheOption = WMI.BitmapCacheOption.OnLoad;
                img.CreateOptions = WMI.BitmapCreateOptions.IgnoreImageCache;   // pick up repainted PNGs
                img.EndInit();
                img.Freeze();
                b = new WM.ImageBrush(img)
                {
                    ViewportUnits = WM.BrushMappingMode.Absolute,
                    Viewport = new W.Rect(0, 0, 1, 1),
                    TileMode = WM.TileMode.Tile,
                    Opacity = opacity,
                };
                b.Freeze();
            }
            catch
            {
                return Solid(WM.Color.FromRgb(150, 150, 140), opacity);
            }
            _textures[key] = b;
            return b;
        }

        private static WM.Brush Solid(WM.Color c, double opacity)
        {
            var b = new WM.SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }

        private void RebuildEnvironment()
        {
            _env.Children.Clear();
            if (!Environment) return;
            float r = Math.Max(_sceneRadius * 6f, 80f);
            var c = _sceneCenter;

            // water
            AddFlat(_env, c.X - r, c.Z - r, c.X + r, c.Z + r, -0.6f,
                Night ? WM.Color.FromRgb(16, 26, 34) : WM.Color.FromRgb(64, 84, 88), specular: true);

            // pier around the building's footprint
            if (FootprintMax.X > FootprintMin.X)
            {
                float m = 4f;
                AddBox(_env, FootprintMin.X - m, FootprintMin.Y - m, FootprintMax.X + m, FootprintMax.Y + m, -0.6f, -0.04f,
                    Night ? WM.Color.FromRgb(70, 64, 58) : WM.Color.FromRgb(158, 146, 128));
            }
        }

        private static void AddFlat(M3.Model3DGroup g, float x0, float z0, float x1, float z1, float y, WM.Color col, bool specular)
        {
            var mesh = new M3.MeshGeometry3D
            {
                Positions = new M3.Point3DCollection { new(x0, y, z0), new(x1, y, z0), new(x1, y, z1), new(x0, y, z1) },
                TriangleIndices = new WM.Int32Collection { 0, 2, 1, 0, 3, 2 },
            };
            mesh.Freeze();
            var mat = new M3.MaterialGroup();
            mat.Children.Add(new M3.DiffuseMaterial(Solid(col, 1)));
            if (specular) mat.Children.Add(new M3.SpecularMaterial(Solid(WM.Color.FromRgb(70, 80, 85), 1), 30));
            mat.Freeze();
            g.Children.Add(new M3.GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        private static void AddBox(M3.Model3DGroup g, float x0, float z0, float x1, float z1, float y0, float y1, WM.Color col)
        {
            var p = new M3.Point3DCollection
            {
                new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1),
                new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1),
            };
            var idx = new WM.Int32Collection
            {
                4, 6, 5, 4, 7, 6,   // top
                0, 1, 5, 0, 5, 4,   1, 2, 6, 1, 6, 5,   2, 3, 7, 2, 7, 6,   3, 0, 4, 3, 4, 7,
            };
            var mesh = new M3.MeshGeometry3D { Positions = p, TriangleIndices = idx };
            mesh.Freeze();
            var mat = new M3.DiffuseMaterial(Solid(col, 1));
            mat.Freeze();
            g.Children.Add(new M3.GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        private void ApplyLighting()
        {
            _lights.Children.Clear();
            if (Night)
            {
                _lights.Children.Add(new M3.AmbientLight(WM.Color.FromRgb(46, 50, 70)));
                _lights.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(70, 80, 110), new M3.Vector3D(-0.4, -1, -0.3)));
                _border.Background = new WM.LinearGradientBrush(WM.Color.FromRgb(8, 10, 20), WM.Color.FromRgb(28, 34, 52), 90);
            }
            else
            {
                _lights.Children.Add(new M3.AmbientLight(WM.Color.FromRgb(96, 96, 100)));
                _lights.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(190, 186, 176), new M3.Vector3D(-0.45, -1, -0.3)));
                _lights.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(60, 64, 72), new M3.Vector3D(0.6, -0.3, 0.8)));
                _border.Background = new WM.LinearGradientBrush(WM.Color.FromRgb(70, 76, 80), WM.Color.FromRgb(132, 138, 140), 90);
            }
        }

        // ------------------------------------------------------------------ camera

        public void ResetView()
        {
            _target = _sceneCenter;
            _dist = _sceneRadius * 2.4f;
            _yaw = -0.75f;
            _pitch = 0.55f;
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            var cam = _target + _dist * new NV.Vector3(
                MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
            var look = _target - cam;
            _camera.Position = P3(cam);
            _camera.LookDirection = new M3.Vector3D(look.X, look.Y, look.Z);
            _camera.UpDirection = new M3.Vector3D(0, 1, 0);
            _camera.NearPlaneDistance = Math.Max(0.05, _dist * 0.01);
            _camera.FarPlaneDistance = Math.Max(1000, _sceneRadius * 40);
        }

        private void OnDown(object sender, WI.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ResetView(); return; }
            _drag = e.ChangedButton;
            _last = e.GetPosition(_border);
            _border.CaptureMouse();
        }

        private void OnMove(object sender, WI.MouseEventArgs e)
        {
            if (_drag == null) return;
            var p = e.GetPosition(_border);
            float dx = (float)(p.X - _last.X), dy = (float)(p.Y - _last.Y);
            _last = p;
            if (_drag == WI.MouseButton.Left)
            {
                _yaw -= dx * 0.01f;
                _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.45f, 1.45f);
            }
            else
            {
                var cam = _target + _dist * new NV.Vector3(
                    MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
                var forward = NV.Vector3.Normalize(_target - cam);
                var right = NV.Vector3.Normalize(NV.Vector3.Cross(forward, NV.Vector3.UnitY));
                var up = NV.Vector3.Cross(right, forward);
                _target += (-dx * right + dy * up) * (_dist * 0.0016f);
            }
            UpdateCamera();
        }

        private void OnUp(object sender, WI.MouseButtonEventArgs e)
        {
            _drag = null;
            _border.ReleaseMouseCapture();
        }

        private void OnWheel(object sender, WI.MouseWheelEventArgs e)
        {
            _dist = Math.Clamp(_dist * (e.Delta > 0 ? 0.88f : 1.14f), 1f, _sceneRadius * 12f);
            UpdateCamera();
        }
    }
}
