using SkiaSharp;
using System.Text.Json;

namespace KAMERA
{
    public sealed class FaceEmbeddingTemplate
    {
        public string Version { get; set; } = "v13-datasheet-local-face-embedding";
        public string Username { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public double[] Embedding { get; set; } = Array.Empty<double>();
        public double[] ColorEmbedding { get; set; } = Array.Empty<double>();
        public double Quality { get; set; }
    }

    public sealed class FaceEmbeddingMatchResult
    {
        public bool IsMatch { get; set; }
        public double FinalScore { get; set; }
        public double EmbeddingScore { get; set; }
        public double ColorScore { get; set; }
        public double DistanceScore { get; set; }
        public double LiveQuality { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Face recognition lokal berbasis descriptor wajah.
    /// Catatan: ini lebih aman dari sekadar Haar/LBPH satu label, tetapi tetap bukan pengganti model ArcFace/FaceNet.
    /// </summary>
    public static class FaceEmbeddingService
    {
        private const int Size = 128;
        public const double FinalThreshold = 70.0;
        public const double EmbeddingThreshold = 68.0;
        public const double QualityThreshold = 5.0;

        public static string GetTemplatePath(string username)
            => Path.Combine(FileSystem.AppDataDirectory, $"{username}_face_embedding.json");

        public static Task<FaceEmbeddingTemplate> CreateTemplateAsync(byte[] photoBytes, string username)
        {
            if (photoBytes == null || photoBytes.Length == 0)
                throw new InvalidOperationException("Foto wajah kosong.");

            using var bitmap = Decode(photoBytes);
            using var normalized = NormalizeFace(bitmap);
            var embedding = BuildLocalEmbedding(normalized);
            var colorEmbedding = BuildColorEmbedding(normalized);
            double quality = EstimateQuality(normalized);

            if (quality < QualityThreshold)
                throw new InvalidOperationException("Kualitas foto wajah terlalu rendah. Ambil ulang dengan cahaya cukup dan wajah berada di tengah kamera.");

            var template = new FaceEmbeddingTemplate
            {
                Username = username,
                CreatedAt = DateTime.Now,
                Embedding = embedding,
                ColorEmbedding = colorEmbedding,
                Quality = quality
            };

            return Task.FromResult(template);
        }

        public static async Task SaveTemplateAsync(FaceEmbeddingTemplate template, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        public static async Task<FaceEmbeddingTemplate> LoadTemplateAsync(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Template embedding wajah tidak ditemukan.", path);

            var json = await File.ReadAllTextAsync(path);
            var template = JsonSerializer.Deserialize<FaceEmbeddingTemplate>(json);
            if (template == null || template.Embedding.Length == 0)
                throw new InvalidOperationException("Template embedding wajah rusak atau kosong.");

            return template;
        }

        public static async Task<FaceEmbeddingMatchResult> VerifyAsync(byte[] livePhotoBytes, string templatePath)
        {
            var saved = await LoadTemplateAsync(templatePath);
            var live = await CreateTemplateAsync(livePhotoBytes, saved.Username);

            double cosine = CosineSimilarity(live.Embedding, saved.Embedding);
            double embeddingScore = ToPercent(cosine);

            double colorCosine = CosineSimilarity(live.ColorEmbedding, saved.ColorEmbedding);
            double colorScore = ToPercent(colorCosine);

            double distance = EuclideanDistance(live.Embedding, saved.Embedding);
            double distanceScore = Math.Clamp(100.0 * Math.Exp(-distance * 2.8), 0, 100);

            double final = (embeddingScore * 0.72) + (distanceScore * 0.20) + (colorScore * 0.08);
            bool match = final >= FinalThreshold && embeddingScore >= EmbeddingThreshold && live.Quality >= QualityThreshold;

            return new FaceEmbeddingMatchResult
            {
                IsMatch = match,
                FinalScore = Math.Clamp(final, 0, 100),
                EmbeddingScore = Math.Clamp(embeddingScore, 0, 100),
                ColorScore = Math.Clamp(colorScore, 0, 100),
                DistanceScore = Math.Clamp(distanceScore, 0, 100),
                LiveQuality = live.Quality,
                Message = match
                    ? "Identitas wajah cocok dengan template biometrik tersimpan."
                    : "Identitas wajah tidak cocok dengan template biometrik tersimpan."
            };
        }

        private static SKBitmap Decode(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var sk = new SKManagedStream(ms);
            var bmp = SKBitmap.Decode(sk);
            if (bmp == null)
                throw new InvalidOperationException("Foto gagal dibaca.");
            return bmp;
        }

        private static SKBitmap NormalizeFace(SKBitmap source)
        {
            int cropW = (int)(source.Width * 0.72);
            int cropH = (int)(source.Height * 0.78);
            int left = Math.Max(0, (source.Width - cropW) / 2);
            int top = Math.Max(0, (source.Height - cropH) / 2);

            var cropRect = new SKRectI(left, top, Math.Min(source.Width, left + cropW), Math.Min(source.Height, top + cropH));
            using var cropped = new SKBitmap(cropRect.Width, cropRect.Height, source.ColorType, source.AlphaType);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(source, cropRect, new SKRect(0, 0, cropped.Width, cropped.Height));
            }

            var resized = cropped.Resize(new SKImageInfo(Size, Size), SKFilterQuality.High);
            if (resized == null)
                throw new InvalidOperationException("Normalisasi wajah gagal.");
            return resized;
        }

        private static double[] BuildLocalEmbedding(SKBitmap bmp)
        {
            var features = new List<double>();
            const int cells = 4;
            int cellSize = Size / cells;

            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    int x0 = cx * cellSize;
                    int y0 = cy * cellSize;
                    features.AddRange(BuildLbpHistogram(bmp, x0, y0, cellSize, cellSize));
                    features.AddRange(BuildHogHistogram(bmp, x0, y0, cellSize, cellSize));
                }
            }

            features.AddRange(BuildSymmetryFeatures(bmp));
            return L2Normalize(features.ToArray());
        }

        private static double[] BuildLbpHistogram(SKBitmap bmp, int x0, int y0, int w, int h)
        {
            double[] hist = new double[32];
            for (int y = y0 + 1; y < y0 + h - 1; y++)
            {
                for (int x = x0 + 1; x < x0 + w - 1; x++)
                {
                    int center = Luma(bmp.GetPixel(x, y));
                    int code = 0;
                    if (Luma(bmp.GetPixel(x - 1, y - 1)) >= center) code |= 1;
                    if (Luma(bmp.GetPixel(x, y - 1)) >= center) code |= 2;
                    if (Luma(bmp.GetPixel(x + 1, y - 1)) >= center) code |= 4;
                    if (Luma(bmp.GetPixel(x + 1, y)) >= center) code |= 8;
                    if (Luma(bmp.GetPixel(x + 1, y + 1)) >= center) code |= 16;
                    if (Luma(bmp.GetPixel(x, y + 1)) >= center) code |= 32;
                    if (Luma(bmp.GetPixel(x - 1, y + 1)) >= center) code |= 64;
                    if (Luma(bmp.GetPixel(x - 1, y)) >= center) code |= 128;
                    hist[Math.Min(31, code / 8)]++;
                }
            }
            return L1Normalize(hist);
        }

        private static double[] BuildHogHistogram(SKBitmap bmp, int x0, int y0, int w, int h)
        {
            double[] hist = new double[12];
            for (int y = y0 + 1; y < y0 + h - 1; y++)
            {
                for (int x = x0 + 1; x < x0 + w - 1; x++)
                {
                    int gx = -Luma(bmp.GetPixel(x - 1, y - 1)) + Luma(bmp.GetPixel(x + 1, y - 1))
                             - 2 * Luma(bmp.GetPixel(x - 1, y)) + 2 * Luma(bmp.GetPixel(x + 1, y))
                             - Luma(bmp.GetPixel(x - 1, y + 1)) + Luma(bmp.GetPixel(x + 1, y + 1));

                    int gy = -Luma(bmp.GetPixel(x - 1, y - 1)) - 2 * Luma(bmp.GetPixel(x, y - 1)) - Luma(bmp.GetPixel(x + 1, y - 1))
                             + Luma(bmp.GetPixel(x - 1, y + 1)) + 2 * Luma(bmp.GetPixel(x, y + 1)) + Luma(bmp.GetPixel(x + 1, y + 1));

                    double mag = Math.Sqrt(gx * gx + gy * gy);
                    if (mag < 20) continue;
                    double angle = Math.Atan2(gy, gx) + Math.PI;
                    int bin = Math.Min(11, (int)(angle / (2 * Math.PI) * 12));
                    hist[bin] += mag;
                }
            }
            return L1Normalize(hist);
        }

        private static double[] BuildColorEmbedding(SKBitmap bmp)
        {
            double[] hist = new double[36];
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    double sum = c.Red + c.Green + c.Blue + 1.0;
                    int r = Math.Min(5, (int)((c.Red / sum) * 6));
                    int g = Math.Min(5, (int)((c.Green / sum) * 6));
                    hist[r * 6 + g]++;
                }
            }
            return L2Normalize(hist);
        }

        private static double[] BuildSymmetryFeatures(SKBitmap bmp)
        {
            double left = 0, right = 0, diff = 0, count = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size / 2; x++)
                {
                    int l = Luma(bmp.GetPixel(x, y));
                    int r = Luma(bmp.GetPixel(Size - 1 - x, y));
                    left += l;
                    right += r;
                    diff += Math.Abs(l - r);
                    count++;
                }
            }

            return new[]
            {
                left / (count * 255.0),
                right / (count * 255.0),
                diff / (count * 255.0)
            };
        }

        private static double EstimateQuality(SKBitmap bmp)
        {
            double mean = 0, edges = 0, count = 0;
            for (int y = 1; y < bmp.Height - 1; y++)
            {
                for (int x = 1; x < bmp.Width - 1; x++)
                {
                    int l = Luma(bmp.GetPixel(x, y));
                    mean += l;
                    int dx = Math.Abs(Luma(bmp.GetPixel(x + 1, y)) - Luma(bmp.GetPixel(x - 1, y)));
                    int dy = Math.Abs(Luma(bmp.GetPixel(x, y + 1)) - Luma(bmp.GetPixel(x, y - 1)));
                    edges += dx + dy;
                    count++;
                }
            }
            double brightness = mean / count;
            double sharpness = edges / count;
            double brightnessScore = 100.0 - Math.Abs(brightness - 128.0) * 0.65;
            return Math.Clamp((brightnessScore * 0.45) + (sharpness * 1.15), 0, 100);
        }

        private static int Luma(SKColor c) => (int)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);

        private static double[] L1Normalize(double[] v)
        {
            double sum = v.Sum();
            if (sum <= 0) return v;
            return v.Select(x => x / sum).ToArray();
        }

        private static double[] L2Normalize(double[] v)
        {
            double norm = Math.Sqrt(v.Sum(x => x * x));
            if (norm <= 0) return v;
            return v.Select(x => x / norm).ToArray();
        }

        private static double CosineSimilarity(double[] a, double[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na <= 0 || nb <= 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        private static double EuclideanDistance(double[] a, double[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            double sum = 0;
            for (int i = 0; i < len; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return Math.Sqrt(sum / Math.Max(1, len));
        }

        private static double ToPercent(double cosine)
            => Math.Clamp(((cosine + 1.0) / 2.0) * 100.0, 0, 100);
    }
}
