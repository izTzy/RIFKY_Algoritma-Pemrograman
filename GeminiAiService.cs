using System.Text;
using System.Text.Json;

namespace KAMERA;

public class GeminiAiService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Model Gemini yang ringan dan cocok untuk chatbot/NLP.
    private const string ModelName = "gemini-3.1-flash-lite";

    public async Task<string> AskAsync(string userQuestion)
    {
        string apiKey = await LoadApiKeyAsync();

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("ISI_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            return GetMissingApiKeyMessage();
        }

        string prompt = BuildProjectPrompt(userQuestion);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.25
            }
        };

        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";
        string json = JsonSerializer.Serialize(requestBody);

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(url, content);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"Gemini API error: {(int)response.StatusCode} {response.StatusCode}\n\n{SimplifyError(responseText)}";
            }

            string? answer = ExtractAnswer(responseText);
            return string.IsNullOrWhiteSpace(answer)
                ? "Gemini berhasil dipanggil, tetapi format jawaban tidak terbaca. Coba pertanyaan lain."
                : CleanAnswer(answer);
        }
        catch (Exception ex)
        {
            return $"Gagal menghubungi Gemini API: {ex.Message}\nPastikan koneksi internet aktif dan API key benar.";
        }
    }

    private static async Task<string> LoadApiKeyAsync()
    {
        // Prioritas 1: file di AppDataDirectory, bisa dibuat otomatis saat app pertama kali jalan.
        string appDataKeyPath = Path.Combine(FileSystem.AppDataDirectory, "gemini_api_key.txt");

        if (File.Exists(appDataKeyPath))
        {
            string key = (await File.ReadAllTextAsync(appDataKeyPath)).Trim();
            if (!string.IsNullOrWhiteSpace(key))
                return key;
        }

        // Prioritas 2: file Raw bawaan project. Cocok kalau kamu mau mengganti file sebelum build.
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("gemini_api_key.txt");
            using var reader = new StreamReader(stream);
            string key = (await reader.ReadToEndAsync()).Trim();

            if (!string.IsNullOrWhiteSpace(key))
                return key;
        }
        catch
        {
            // File belum ada / belum dibundle. Akan dibuatkan contoh di AppDataDirectory.
        }

        try
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            await File.WriteAllTextAsync(
                appDataKeyPath,
                "ISI_API_KEY_GEMINI_KAMU_DI_SINI");
        }
        catch
        {
            // Abaikan jika gagal membuat file contoh.
        }

        return string.Empty;
    }

    public static string GetApiKeyFilePath()
    {
        return Path.Combine(FileSystem.AppDataDirectory, "gemini_api_key.txt");
    }

    private static string BuildProjectPrompt(string userQuestion)
    {
        return $$"""
        Kamu adalah AI Project Assistant untuk aplikasi .NET MAUI bernama StrokeAI / Face Security App.

        Konteks project:
        1. Modul keamanan login wajah:
           - Login memakai kamera laptop.
           - Haar Cascade dipakai untuk mendeteksi wajah.
           - Grayscale dan equalize histogram dipakai untuk preprocessing.
           - LBPH dipakai untuk verifikasi wajah.
           - Sample wajah user lain dapat dipakai sebagai negative sample agar orang berbeda tidak mudah diterima.
           - Keputusan akses login tetap dilakukan sistem lokal, bukan oleh AI.

        2. Modul prediksi risiko stroke dini:
           - Sistem menggunakan Decision Tree untuk klasifikasi risiko rendah, sedang, atau tinggi.
           - Input sensor heart rate memakai pulse sensor analog.
           - Sinyal pulse sensor masuk ke Low Pass Filter (LPF), lalu penguat/buffer LM358, lalu ADC mikrokontroler.
           - BMI dihitung dari berat badan menggunakan load cell + HX711 dan tinggi badan.
           - Parameter tambahan seperti usia, tekanan darah, gula darah, dan riwayat kesehatan dapat diinput manual.
           - Sistem bukan alat diagnosis medis, hanya alat bantu pemantauan/prediksi risiko awal.

        Aturan menjawab:
        - Jawab dalam bahasa Indonesia yang jelas dan mudah dipahami mahasiswa.
        - Langsung jawab pertanyaan pengguna tanpa perkenalan, tanpa sapaan pembuka, dan tanpa menyebut "saya adalah AI".
        - Jangan memakai format markdown tebal seperti **teks**. Gunakan teks polos.
        - Boleh menjawab pertanyaan umum di luar project, tetapi jika relevan hubungkan ke project.
        - Jangan mengklaim dapat mendiagnosis stroke.
        - Jangan memberikan keputusan akses login.
        - Jika membahas kesehatan, selalu tekankan bahwa hasil sistem bukan pengganti pemeriksaan dokter.
        - Jika pertanyaan teknis, berikan langkah atau contoh yang praktis.

        Pertanyaan pengguna:
        {{userQuestion}}
        """;
    }

    private static string CleanAnswer(string answer)
    {
        return answer
            .Replace("**", "")
            .Replace("__", "")
            .Trim();
    }

    private static string? ExtractAnswer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return null;

        var firstCandidate = candidates[0];

        if (!firstCandidate.TryGetProperty("content", out var content))
            return null;

        if (!content.TryGetProperty("parts", out var parts) || parts.GetArrayLength() == 0)
            return null;

        var sb = new StringBuilder();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textElement))
            {
                sb.AppendLine(textElement.GetString());
            }
        }

        return sb.ToString();
    }

    private static string SimplifyError(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                string message = error.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? responseText
                    : responseText;

                return message;
            }
        }
        catch
        {
            // Jika bukan JSON valid, tampilkan mentah.
        }

        return responseText;
    }

    private static string GetMissingApiKeyMessage()
    {
        return $"API key Gemini belum diisi.\n\n" +
               $"Buat file bernama gemini_api_key.txt, isi hanya dengan API key Gemini kamu, lalu taruh di salah satu tempat ini:\n\n" +
               $"1. Untuk langsung run tanpa rebuild:\n{GetApiKeyFilePath()}\n\n" +
               $"2. Untuk dibundle saat build project:\nResources/Raw/gemini_api_key.txt\n\n" +
               $"Setelah itu jalankan ulang aplikasi.";
    }
}
