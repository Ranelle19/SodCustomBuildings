using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SD = System.Drawing;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using M3 = System.Windows.Media.Media3D;

namespace BuildingEditor
{
    /// <summary>
    /// Small pictures of furniture for the catalog: the exported model drawn from the front-right and a bit above
    /// (WPF 3D, off screen), kept as thumb.png next to the model so it's only drawn once.
    /// </summary>
    public static class FurnitureThumbs
    {
        public const string ThumbFile = "thumb.png";

        public static SD.Image Get(string modelFolder, int size)
        {
            if (modelFolder == null || !Directory.Exists(modelFolder)) return null;
            var path = Path.Combine(modelFolder, ThumbFile);
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(Path.Combine(modelFolder, "model.obj")))
                    using (var fs = File.OpenRead(path)) return new SD.Bitmap(SD.Image.FromStream(fs));
                var model = ModelLoader.Load(modelFolder, out _);
                if (model == null) return null;
                var png = Render(model, size);
                if (png == null) return null;
                File.WriteAllBytes(path, png);
                using var ms = new MemoryStream(png);
                return new SD.Bitmap(SD.Image.FromStream(ms));
            }
            catch { return null; }
        }

        private static byte[] Render(LoadedModel model, int size)
        {
            var group = new M3.Model3DGroup();
            group.Children.Add(new M3.AmbientLight(WM.Color.FromRgb(120, 120, 125)));
            group.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(200, 196, 188), new M3.Vector3D(-0.6, -1, -0.8)));
            group.Children.Add(new M3.DirectionalLight(WM.Color.FromRgb(70, 70, 80), new M3.Vector3D(0.7, -0.3, 0.6)));
            var brushes = new Dictionary<string, WM.Brush>();
            foreach (var m in model.Meshes)
            {
                var mesh = new M3.MeshGeometry3D
                {
                    Positions = new M3.Point3DCollection(m.Pos.Select(p => new M3.Point3D(p.X, p.Y, p.Z))),
                    TriangleIndices = new WM.Int32Collection(m.Idx),
                };
                if (m.HasNormals) mesh.Normals = new M3.Vector3DCollection(m.Nrm.Select(n => new M3.Vector3D(n.X, n.Y, n.Z)));
                if (m.HasUv) mesh.TextureCoordinates = new WM.PointCollection(m.Uv.Select(t => new W.Point(t.X, 1 - t.Y)));
                WM.Brush brush = null;
                if (m.TexturePath != null && m.HasUv && !brushes.TryGetValue(m.TexturePath, out brush))
                {
                    try
                    {
                        var img = new WMI.BitmapImage();
                        img.BeginInit();
                        img.UriSource = new Uri(Path.GetFullPath(m.TexturePath));
                        img.CacheOption = WMI.BitmapCacheOption.OnLoad;
                        img.EndInit();
                        img.Freeze();
                        brush = new WM.ImageBrush(img) { ViewportUnits = WM.BrushMappingMode.Absolute, TileMode = WM.TileMode.Tile, Viewport = new W.Rect(0, 0, 1, 1) };
                    }
                    catch { brush = null; }
                    brushes[m.TexturePath] = brush;
                }
                brush ??= new WM.SolidColorBrush(WM.Color.FromRgb((byte)Math.Clamp(m.Kd.X * 255, 0, 255), (byte)Math.Clamp(m.Kd.Y * 255, 0, 255), (byte)Math.Clamp(m.Kd.Z * 255, 0, 255)));
                var mat = new M3.DiffuseMaterial(brush);
                group.Children.Add(new M3.GeometryModel3D(mesh, mat) { BackMaterial = mat });
            }

            var c = (model.Min + model.Max) / 2;
            float r = Math.Max(0.05f, (model.Max - model.Min).Length() / 2);
            // the front of a game piece is Unity +Z; the export mirrors X, so look from +Z, a little to the side and above
            var dir = new M3.Vector3D(0.55, 0.5, 1);
            dir.Normalize();
            double dist = r / Math.Sin(30 * Math.PI / 180) * 1.05;
            var camera = new M3.PerspectiveCamera(new M3.Point3D(c.X + dir.X * dist, c.Y + dir.Y * dist, c.Z + dir.Z * dist),
                                                  new M3.Vector3D(-dir.X, -dir.Y, -dir.Z), new M3.Vector3D(0, 1, 0), 60)
            { NearPlaneDistance = dist * 0.05, FarPlaneDistance = dist * 10 };
            var vp = new WC.Viewport3D { Width = size, Height = size, Camera = camera };
            vp.Children.Add(new M3.ModelVisual3D { Content = group });
            var host = new WC.Border { Width = size, Height = size, Background = new WM.SolidColorBrush(WM.Color.FromRgb(22, 22, 40)), Child = vp };
            host.Measure(new W.Size(size, size));
            host.Arrange(new W.Rect(0, 0, size, size));
            host.UpdateLayout();
            var rtb = new WMI.RenderTargetBitmap(size, size, 96, 96, WM.PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new WMI.PngBitmapEncoder();
            enc.Frames.Add(WMI.BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }
}
