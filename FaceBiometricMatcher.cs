using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace KAMERA
{
    public sealed class FaceMatchResult
    {
        public double Similarity { get; set; }
        public double ColorScore { get; set; }
        public double TextureScore { get; set; }
        public double EdgeScore { get; set; }
        public bool IsMatch { get; set; }
    }

    /// <summary>
    /// Matcher biometrik ringan untuk MAUI tanpa model eksternal.
    /// Tidak lagi memakai perbandingan pixel grayscale 16x16 yang sangat bergantung pose.
    /// Sistem ini memakai kombinasi fitur warna wajah, tekstur lokal, dan pola tepi wajah.
    /// </summary>
    public static class FaceBiometricMatcher
    {
        private const int ProfileSize = 96;
        private const double MatchThreshold = 62.0;

        public static Task<FaceMatchResult> CompareAsync(byte[] livePhotoBytes, string enrolledPhotoPath)
        {
            if (livePhotoBytes == null || livePhotoBytes.Length == 0)
                throw new InvalidOperationException("Foto verifikasi kosong.");

            if (!File.Exists(enrolledPhotoPath))
                throw new FileNotFoundException("Foto pembanding tidak ditemukan.", enrolledPhotoPath);

            byte[] enrolledBytes = File.ReadAllBytes(enrolledPhotoPath);

            using var liveBitmap = DecodeBitmap(livePhotoBytes);
            using var enrolledBitmap = DecodeBitmap(enrolledBytes);

            var liveProfile = BuildProfile(liveBitmap);
            var enrolledProfile = BuildProfile(enrolledBitmap);

            double colorScore = HistogramIntersection(liveProfile.ColorHistogram, enrolledProfile.ColorHistogram) * 100.0;
            double textureScore = HistogramIntersection(liveProfile.TextureHistogram, enrolledProfile.TextureHistogram) * 100.0;
            double edgeScore = HistogramIntersection(liveProfile.EdgeHistogram, enrolledProfile.EdgeHistogram) * 100.0;

            // Warna + tekstur + edge digabung agar tidak hanya cocok karena pose/foto mirip.
            double finalScore = (colorScore * 0.42) + (textureScore * 0.36) + (edgeScore * 0.22);

            return Task.FromResult(new FaceMatchResult
            {
                Similarity = Math.Clamp(finalScore, 0, 100),
                ColorScore = Math.Clamp(colorScore, 0, 100),
                TextureScore = Math.Clamp(textureScore, 0, 100),
                EdgeScore = Math.Clamp(edgeScore, 0, 100),
                IsMatch = finalScore >= MatchThreshold
            });
        }

        private sealed class FaceProfile
        {
            public double[] ColorHistogram { get; init; } = Array.Empty<double>();
            public double[] TextureHistogram { get; init; } = Array.Empty<double>();
            public double[] EdgeHistogram { get; init; } = Array.Empty<double>();
        }

        private static SKBitmap DecodeBitmap(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var skStream = new SKManagedStream(stream);
            var bitmap = SKBitmap.Decode(skStream);

            if (bitmap == null)
                throw new InvalidOperationException("Foto gagal dibaca. Coba ambil foto ulang dengan pencahayaan lebih baik.");

            return bitmap;
        }

        private static FaceProfile BuildProfile(SKBitmap bitmap)
        {
            using var normalized = NormalizeFaceArea(bitmap);

            return new FaceProfile
            {
                ColorHistogram = BuildColorHistogram(normalized),
                TextureHistogram = BuildTextureHistogram(normalized),
                EdgeHistogram = BuildEdgeHistogram(normalized)
            };
        }

        private static SKBitmap NormalizeFaceArea(SKBitmap source)
        {
            int size = Math.Min(source.Width, source.Height);
            int left = Math.Max(0, (source.Width - size) / 2);
            int top = Math.Max(0, (source.Height - size) / 2);

            using var cropped = new SKBitmap(size, size, source.ColorType, source.AlphaType);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(
                    source,
                    new SKRectI(left, top, left + size, top + size),
                    new SKRect(0, 0, size, size));
            }

            var resized = cropped.Resize(new SKImageInfo(ProfileSize, ProfileSize), SKFilterQuality.High);
            if (resized == null)
                throw new InvalidOperationException("Foto gagal dinormalisasi.");

            return resized;
        }

        private static double[] BuildColorHistogram(SKBitmap bitmap)
        {
            const int binsPerChannel = 5;
            int totalBins = binsPerChannel * binsPerChannel * binsPerChannel;
            double[] hist = new double[totalBins];

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    int rBin = Math.Min(binsPerChannel - 1, c.Red * binsPerChannel / 256);
                    int gBin = Math.Min(binsPerChannel - 1, c.Green * binsPerChannel / 256);
                    int bBin = Math.Min(binsPerChannel - 1, c.Blue * binsPerChannel / 256);
                    int index = (rBin * binsPerChannel * binsPerChannel) + (gBin * binsPerChannel) + bBin;
                    hist[index]++;
                }
            }

            return Normalize(hist);
        }

        private static double[] BuildTextureHistogram(SKBitmap bitmap)
        {
            // Local Binary Pattern: membaca tekstur wajah lokal, bukan sekadar bentuk pose foto.
            double[] hist = new double[16];

            for (int y = 1; y < bitmap.Height - 1; y++)
            {
                for (int x = 1; x < bitmap.Width - 1; x++)
                {
                    int center = Luma(bitmap.GetPixel(x, y));
                    int code = 0;

                    if (Luma(bitmap.GetPixel(x - 1, y - 1)) >= center) code |= 1;
                    if (Luma(bitmap.GetPixel(x, y - 1)) >= center) code |= 2;
                    if (Luma(bitmap.GetPixel(x + 1, y - 1)) >= center) code |= 4;
                    if (Luma(bitmap.GetPixel(x + 1, y)) >= center) code |= 8;
                    if (Luma(bitmap.GetPixel(x + 1, y + 1)) >= center) code |= 16;
                    if (Luma(bitmap.GetPixel(x, y + 1)) >= center) code |= 32;
                    if (Luma(bitmap.GetPixel(x - 1, y + 1)) >= center) code |= 64;
                    if (Luma(bitmap.GetPixel(x - 1, y)) >= center) code |= 128;

                    hist[Math.Min(15, code / 16)]++;
                }
            }

            return Normalize(hist);
        }

        private static double[] BuildEdgeHistogram(SKBitmap bitmap)
        {
            double[] hist = new double[8];

            for (int y = 1; y < bitmap.Height - 1; y++)
            {
                for (int x = 1; x < bitmap.Width - 1; x++)
                {
                    int gx =
                        -Luma(bitmap.GetPixel(x - 1, y - 1)) + Luma(bitmap.GetPixel(x + 1, y - 1))
                        -2 * Luma(bitmap.GetPixel(x - 1, y)) + 2 * Luma(bitmap.GetPixel(x + 1, y))
                        -Luma(bitmap.GetPixel(x - 1, y + 1)) + Luma(bitmap.GetPixel(x + 1, y + 1));

                    int gy =
                        -Luma(bitmap.GetPixel(x - 1, y - 1)) - 2 * Luma(bitmap.GetPixel(x, y - 1)) - Luma(bitmap.GetPixel(x + 1, y - 1))
                        + Luma(bitmap.GetPixel(x - 1, y + 1)) + 2 * Luma(bitmap.GetPixel(x, y + 1)) + Luma(bitmap.GetPixel(x + 1, y + 1));

                    double magnitude = Math.Sqrt((gx * gx) + (gy * gy));
                    if (magnitude < 18) continue;

                    double angle = Math.Atan2(gy, gx) + Math.PI;
                    int bin = Math.Min(7, (int)(angle / (2 * Math.PI) * 8));
                    hist[bin] += magnitude;
                }
            }

            return Normalize(hist);
        }

        private static int Luma(SKColor c)
            => (int)((0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue));

        private static double[] Normalize(double[] values)
        {
            double sum = values.Sum();
            if (sum <= 0) return values;
            return values.Select(v => v / sum).ToArray();
        }

        private static double HistogramIntersection(double[] a, double[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            double score = 0;
            for (int i = 0; i < len; i++)
                score += Math.Min(a[i], b[i]);
            return Math.Clamp(score, 0, 1);
        }
    }
}
