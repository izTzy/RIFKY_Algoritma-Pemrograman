using System.Globalization;
using System.Text;

namespace KAMERA
{
    public sealed class FaceDataSheetRecord
    {
        public string Username { get; set; } = "";
        public string Role { get; set; } = "";
        public int SampleId { get; set; }
        public string PhotoPath { get; set; } = "";
        public string TemplatePath { get; set; } = "";
        public double QualityScore { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string Source { get; set; } = "camera";
    }

    /// <summary>
    /// Datasheet sederhana berbasis CSV untuk menyimpan banyak sample wajah per user.
    /// Haar tetap hanya mendeteksi wajah; datasheet ini dipakai sebagai kumpulan template pembanding.
    /// </summary>
    public static class FaceDataSheetService
    {
        public static string SamplesFolder => Path.Combine(FileSystem.AppDataDirectory, "FaceSamples");
        public static string DataSheetPath => Path.Combine(FileSystem.AppDataDirectory, "face_datasheet.csv");

        public static async Task EnsureInitializedAsync()
        {
            Directory.CreateDirectory(SamplesFolder);
            if (!File.Exists(DataSheetPath))
            {
                await File.WriteAllTextAsync(
                    DataSheetPath,
                    "username,role,sample_id,photo_path,template_path,quality_score,created_at,source" + Environment.NewLine,
                    Encoding.UTF8);
            }
        }

        public static async Task<IReadOnlyList<FaceDataSheetRecord>> LoadAsync()
        {
            await EnsureInitializedAsync();
            var lines = await File.ReadAllLinesAsync(DataSheetPath, Encoding.UTF8);
            var result = new List<FaceDataSheetRecord>();

            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var c = SplitCsv(line);
                if (c.Count < 8) continue;

                if (!int.TryParse(c[2], out int sampleId)) sampleId = 0;
                if (!double.TryParse(c[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double quality)) quality = 0;
                if (!DateTime.TryParse(c[6], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime createdAt)) createdAt = DateTime.Now;

                result.Add(new FaceDataSheetRecord
                {
                    Username = c[0],
                    Role = c[1],
                    SampleId = sampleId,
                    PhotoPath = c[3],
                    TemplatePath = c[4],
                    QualityScore = quality,
                    CreatedAt = createdAt,
                    Source = c[7]
                });
            }

            try
            {
                var databaseSamples = await AppDatabase.Instance.GetOtherFaceSamplesAsync("");
                var knownPhotos = new HashSet<string>(
                    result.Select(x => x.PhotoPath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var sample in databaseSamples)
                {
                    if (!knownPhotos.Add(sample.PhotoPath))
                        continue;

                    result.Add(new FaceDataSheetRecord
                    {
                        Username = sample.Username,
                        Role = sample.Role,
                        SampleId = sample.SampleId,
                        PhotoPath = sample.PhotoPath,
                        TemplatePath = sample.EmbeddingPath,
                        QualityScore = sample.QualityScore,
                        CreatedAt = sample.CreatedAt,
                        Source = sample.Source
                    });
                }
            }
            catch
            {
                // Database SQLite bersifat pendamping; jika belum siap, CSV tetap bisa dipakai.
            }

            return result;
        }

        public static async Task<IReadOnlyList<FaceDataSheetRecord>> GetSamplesForUserAsync(string username)
        {
            var all = await LoadAsync();
            return all
                .Where(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                .Where(x => File.Exists(x.PhotoPath) && File.Exists(x.TemplatePath))
                .OrderByDescending(x => x.CreatedAt)
                .ToList();
        }

        public static async Task<IReadOnlyList<FaceDataSheetRecord>> GetOtherUserSamplesAsync(string username)
        {
            var all = await LoadAsync();
            return all
                .Where(x => !x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                .Where(x => File.Exists(x.TemplatePath))
                .OrderByDescending(x => x.CreatedAt)
                .ToList();
        }

        public static async Task<FaceDataSheetRecord> SaveNewSampleAsync(string username, string role, byte[] faceBytes, string source = "camera")
        {
            await EnsureInitializedAsync();
            var all = await LoadAsync();
            int nextSampleId = all
                .Where(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SampleId)
                .DefaultIfEmpty(0)
                .Max() + 1;

            string safeUser = SanitizeFileName(username);
            string samplePrefix = $"{safeUser}_sample_{nextSampleId:D3}";
            string photoPath = Path.Combine(SamplesFolder, samplePrefix + ".jpg");
            string templatePath = Path.Combine(SamplesFolder, samplePrefix + "_embedding.json");

            await File.WriteAllBytesAsync(photoPath, faceBytes);
            var template = await FaceEmbeddingService.CreateTemplateAsync(faceBytes, username);
            await FaceEmbeddingService.SaveTemplateAsync(template, templatePath);

            var record = new FaceDataSheetRecord
            {
                Username = username,
                Role = role,
                SampleId = nextSampleId,
                PhotoPath = photoPath,
                TemplatePath = templatePath,
                QualityScore = template.Quality,
                CreatedAt = DateTime.Now,
                Source = source
            };

            await File.AppendAllTextAsync(DataSheetPath, ToCsv(record) + Environment.NewLine, Encoding.UTF8);

            try
            {
                await AppDatabase.Instance.AddFaceSampleAsync(
                    username,
                    role,
                    nextSampleId,
                    photoPath,
                    templatePath,
                    template.Quality,
                    source,
                    record.CreatedAt);
            }
            catch
            {
                // CSV tetap menjadi fallback bila SQLite gagal disimpan.
            }

            return record;
        }

        private static string ToCsv(FaceDataSheetRecord r)
        {
            string[] c =
            {
                r.Username,
                r.Role,
                r.SampleId.ToString(CultureInfo.InvariantCulture),
                r.PhotoPath,
                r.TemplatePath,
                r.QualityScore.ToString("F2", CultureInfo.InvariantCulture),
                r.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                r.Source
            };
            return string.Join(",", c.Select(EscapeCsv));
        }

        private static string EscapeCsv(string value)
        {
            value ??= "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else quoted = false;
                    }
                    else current.Append(ch);
                }
                else
                {
                    if (ch == '"') quoted = true;
                    else if (ch == ',')
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    else current.Append(ch);
                }
            }

            result.Add(current.ToString());
            return result;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char ch in Path.GetInvalidFileNameChars())
                value = value.Replace(ch, '_');
            return string.IsNullOrWhiteSpace(value) ? "user" : value.Trim();
        }
    }
}
