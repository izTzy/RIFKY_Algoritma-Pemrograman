using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace KAMERA;

public partial class MainDashboardPage : ContentPage
{
    private readonly string _role;
    private readonly string _username;
    private readonly ObservableCollection<HistoryItem> _history = new();
    private readonly ObservableCollection<UserDbItem> _users = new();
    private readonly StringBuilder _chatTranscript = new();
    private bool _isChatBusy;
    private bool _isChatMinimized;
    private Label? _lastAiLoadingLabel;
    private double _chatStartTranslationX;
    private double _chatStartTranslationY;
    private double _chatStartWidth = 460;
    private double _chatStartHeight = 620;

    public MainDashboardPage() : this(Preferences.Get("session_role", "Publik")) { }

    public MainDashboardPage(string role)
    {
        InitializeComponent();

        _role = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "Publik";
        _username = Preferences.Get("session_username", "Pengguna");

        LabelWelcome.Text = $"Selamat datang, {_username}! 👋";
        LabelRoleBadge.Text = _role == "Admin" ? "Admin Dashboard • Database" : "Public Dashboard • Screening";

        HistoryList.ItemsSource = _history;
        UserDatabaseList.ItemsSource = _users;
        SetupPredictionFormPickers();

        bool isAdmin = _role == "Admin";

        // Untuk role Publik: hapus/sembunyikan sidebar kiri agar dashboard publik lebih fokus ke form prediksi.
        SidebarFrame.IsVisible = isAdmin;
        RootGrid.ColumnDefinitions[0].Width = isAdmin ? new GridLength(260) : new GridLength(0);

        BtnDatabase.IsVisible = isAdmin;
        BtnHistory.IsVisible = isAdmin;
        BtnMenuDashboard.IsVisible = isAdmin;
        BtnPublicLogout.IsVisible = !isAdmin;

        LoadHistory();
        LoadUserDatabase();
        UpdateRealtimeDate();
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            UpdateRealtimeDate();
            return true;
        });

        if (isAdmin)
            ShowSection("dashboard");
        else
            ShowSection("prediction");
    }

    private void UpdateRealtimeDate()
    {
        LabelRealtimeDate.Text = $"📅  {DateTime.Now:dd MMMM yyyy  •  HH:mm:ss}  •  Secure Session";
    }

    private void SetupPredictionFormPickers()
    {
        PickerGender.ItemsSource = new List<string> { "Laki-laki", "Perempuan" };
        PickerHypertension.ItemsSource = new List<string> { "Ya", "Tidak" };
        PickerHeartDisease.ItemsSource = new List<string> { "Ya", "Tidak" };
        PickerSmoking.ItemsSource = new List<string> { "Ya", "Tidak" };
        PickerPreviousStroke.ItemsSource = new List<string> { "Ya", "Tidak" };
        PickerActivity.ItemsSource = new List<string> { "Rutin", "Kadang", "Jarang" };
    }

    private async void LoadHistory()
    {
        _history.Clear();
        int high = 0;

        var rows = await AppDatabase.Instance.GetScreeningHistoryAsync();
        foreach (var row in rows)
        {
            if (row.Score >= 70)
                high++;

            _history.Add(new HistoryItem
            {
                User = row.Username,
                Score = row.Score + "%",
                Mode = string.IsNullOrWhiteSpace(row.Method) ? row.Mode : $"{row.Mode} • {row.Method}",
                Detail = row.LevelText + " • " + row.Detail,
                RawKey = row.Id.ToString()
            });
        }

        LabelTotal.Text = _history.Count.ToString();
        LabelHighRisk.Text = high.ToString();
        LabelDashboardHistory.Text = _history.Count == 0 ? "Belum ada riwayat screening." : $"{_history.Count} screening tersimpan, {high} risiko tinggi.";
        LabelRiskRatio.Text = _history.Count == 0 ? "0%" : $"{(high * 100.0 / _history.Count):F0}%";
    }

    private async void LoadUserDatabase()
    {
        _users.Clear();

        var users = await AppDatabase.Instance.GetUsersAsync();

        foreach (var account in users)
        {
            string user = account.Username;
            string role = account.Role;
            string facePath = Path.Combine(FileSystem.AppDataDirectory, $"{user}_face.png");
            string embeddingPath = FaceEmbeddingService.GetTemplatePath(user);
            bool faceExists = File.Exists(facePath);
            bool embeddingExists = File.Exists(embeddingPath);

            _users.Add(new UserDbItem
            {
                Username = user,
                Role = role,
                FaceFile = embeddingExists ? $"{user}_face_embedding.json" : (faceExists ? $"{user}_face.png" : "Belum ada foto"),
                FacePath = embeddingExists ? embeddingPath : facePath,
                Status = embeddingExists ? "Embedding biometrik aktif" : (faceExists ? "Foto lama, perlu register ulang" : "Foto tidak ditemukan"),
                CanDelete = !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase),
                CanCreateEmbedding = faceExists && !embeddingExists
            });
        }

        LabelTotalUsers.Text = _users.Count.ToString();
        LabelFaceFolder.Text = $"DB: {AppDatabase.DatabasePath}\nAppData: {FileSystem.AppDataDirectory}";
    }

    // ─── TAB SWITCHING (Cara 1 / Cara 2) ────────────────────────────────────
    private bool _isTab1Active = true;

    private async void OnTab1Clicked(object sender, EventArgs e)
    {
        if (_isTab1Active) return;
        _isTab1Active = true;
        await SwitchToTab1();
    }

    private async void OnTab2Clicked(object sender, EventArgs e)
    {
        if (!_isTab1Active) return;
        _isTab1Active = false;
        await SwitchToTab2();
    }

    private async Task SwitchToTab1()
    {
        await Task.WhenAll(
            TabContent2.FadeTo(0, 120, Easing.CubicIn),
            TabContent2.TranslateTo(-20, 0, 120, Easing.CubicIn));
        TabContent2.IsVisible = false;
        TabContent2.TranslationX = 0;

        TabIndicator1.IsVisible = true;
        TabIndicator2.IsVisible = false;
        BtnTab1.TextColor = Color.FromArgb("#4F46E5");
        BtnTab1.FontAttributes = FontAttributes.Bold;
        BtnTab2.TextColor = Color.FromArgb("#94A3B8");
        BtnTab2.FontAttributes = FontAttributes.None;

        TabContent1.Opacity = 0;
        TabContent1.TranslationX = -20;
        TabContent1.IsVisible = true;
        await Task.WhenAll(
            TabContent1.FadeTo(1, 180, Easing.CubicOut),
            TabContent1.TranslateTo(0, 0, 180, Easing.CubicOut));
    }

    private async Task SwitchToTab2()
    {
        await Task.WhenAll(
            TabContent1.FadeTo(0, 120, Easing.CubicIn),
            TabContent1.TranslateTo(20, 0, 120, Easing.CubicIn));
        TabContent1.IsVisible = false;
        TabContent1.TranslationX = 0;

        TabIndicator2.IsVisible = true;
        TabIndicator1.IsVisible = false;
        BtnTab2.TextColor = Color.FromArgb("#10B981");
        BtnTab2.FontAttributes = FontAttributes.Bold;
        BtnTab1.TextColor = Color.FromArgb("#94A3B8");
        BtnTab1.FontAttributes = FontAttributes.None;

        TabContent2.Opacity = 0;
        TabContent2.TranslationX = 20;
        TabContent2.IsVisible = true;
        await Task.WhenAll(
            TabContent2.FadeTo(1, 180, Easing.CubicOut),
            TabContent2.TranslateTo(0, 0, 180, Easing.CubicOut));
    }
    // ─────────────────────────────────────────────────────────────────────────

    private async void OnAnalyzeTextClicked(object sender, EventArgs e)
    {
        string rawInput = EditorSymptoms.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            await DisplayAlert("Peringatan", "Tulis data/gejala pasien terlebih dahulu.", "OK");
            return;
        }

        var data = PatientRiskData.FromFreeText(rawInput);
        SavePredictionResult(data, rawInput, "Teks Bebas");
    }

    private async void OnAnalyzeFormClicked(object sender, EventArgs e)
    {
        var data = PatientRiskData.FromForm(
            EntryAge.Text,
            PickerGender.SelectedItem?.ToString(),
            PickerHypertension.SelectedItem?.ToString(),
            PickerHeartDisease.SelectedItem?.ToString(),
            EntryGlucose.Text,
            EntryBmi.Text,
            PickerSmoking.SelectedItem?.ToString(),
            PickerPreviousStroke.SelectedItem?.ToString(),
            PickerActivity.SelectedItem?.ToString());

        if (data.Age <= 0 || data.Glucose <= 0 || data.Bmi <= 0 ||
            data.Hypertension is null || data.HeartDisease is null ||
            data.Smoking is null || data.PreviousStroke is null || data.LowActivity is null)
        {
            await DisplayAlert("Data Belum Lengkap", "Isi minimal usia, gula darah, BMI, hipertensi, penyakit jantung, merokok, riwayat stroke, dan aktivitas fisik.", "OK");
            return;
        }

        string detail = $"Usia {data.Age:0}; Gender {PickerGender.SelectedItem}; Hipertensi {PickerHypertension.SelectedItem}; Jantung {PickerHeartDisease.SelectedItem}; Gula {data.Glucose:0.##}; BMI {data.Bmi:0.##}; Merokok {PickerSmoking.SelectedItem}; Riwayat Stroke {PickerPreviousStroke.SelectedItem}; Aktivitas {PickerActivity.SelectedItem}";
        SavePredictionResult(data, detail, "Form Terarah");
    }

    private async void SavePredictionResult(PatientRiskData data, string rawInput, string mode)
    {
        string input = NormalizeText(rawInput);
        string aiMethod = "Hybrid AI Otomatis";
        var result = HybridPredict(data, input);

        LabelPrediction.Text = result.LevelText;
        LabelRecommendation.Text = result.Recommendation;
        LabelScoreCircle.Text = $"{result.Score}%";
        RiskProgress.Progress = result.Score / 100.0;

        string history = Preferences.Get("stroke_history", "");
        string detail = rawInput.Length > 110 ? rawInput[..110] + "..." : rawInput;
        detail = detail.Replace("|", " ").Replace("\r", " ").Replace("\n", " ");
        string safeLevel = result.LevelText.Replace("|", " ");
        string item = $"{_username}|{result.Score}|{safeLevel}|{mode} • {aiMethod}|{detail}";
        Preferences.Set("stroke_history", string.IsNullOrWhiteSpace(history) ? item : history + "||" + item);
        await AppDatabase.Instance.AddScreeningHistoryAsync(_username, result.Score, result.LevelText, mode, aiMethod, detail);

        LoadHistory();
        _ = AnimatePredictionResultAsync();
    }

    private async Task AnimatePredictionResultAsync()
    {
        ResultPanel.Opacity = 0.88;
        await Task.WhenAll(
            ResultPanel.FadeTo(1, 160, Easing.CubicOut),
            LabelScoreCircle.ScaleTo(1.10, 100, Easing.CubicOut));
        await LabelScoreCircle.ScaleTo(1.00, 120, Easing.CubicInOut);
    }

    // Decision Tree manual: membaca input bebas, lalu membuat cabang keputusan berdasarkan
    // parameter stroke yang umum dipakai: gejala FAST, usia, hipertensi, penyakit jantung,
    // gula darah, BMI, merokok, riwayat stroke, dan aktivitas fisik.
    private static PredictionResult DecisionTreePredict(PatientRiskData d, string input)
    {
        var reasons = new List<string>();
        int score = 0;

        bool fastSymptom = ContainsAny(input,
            "wajah menurun", "wajah turun", "mulut mencong", "bicara pelo",
            "sulit bicara", "kesulitan bicara", "lengan lemah", "tangan lemah",
            "kaki lemah", "mati rasa sebelah", "lumpuh", "kelumpuhan",
            "penglihatan kabur mendadak", "pusing berat mendadak");

        if (fastSymptom)
        {
            score += 35;
            reasons.Add("gejala FAST/neurologis mendadak");
        }

        // Cabang 1: riwayat stroke atau gejala FAST adalah indikator paling kuat.
        if (d.PreviousStroke == true)
        {
            score += 26;
            reasons.Add("riwayat stroke");
        }

        // Cabang 2: usia.
        if (d.Age >= 65)
        {
            score += 20;
            reasons.Add("usia ≥ 65 tahun");
        }
        else if (d.Age >= 55)
        {
            score += 14;
            reasons.Add("usia 55-64 tahun");
        }
        else if (d.Age >= 45)
        {
            score += 8;
            reasons.Add("usia 45-54 tahun");
        }

        // Cabang 3: hipertensi dan penyakit jantung.
        if (d.Hypertension == true)
        {
            score += 22;
            reasons.Add("hipertensi");
        }

        if (d.HeartDisease == true)
        {
            score += 20;
            reasons.Add("penyakit jantung");
        }

        // Cabang 4: gula darah.
        if (d.Glucose >= 200)
        {
            score += 20;
            reasons.Add("gula darah sangat tinggi");
        }
        else if (d.Glucose >= 140)
        {
            score += 12;
            reasons.Add("gula darah tinggi");
        }

        // Cabang 5: BMI.
        if (d.Bmi >= 30)
        {
            score += 12;
            reasons.Add("BMI obesitas");
        }
        else if (d.Bmi >= 25)
        {
            score += 7;
            reasons.Add("BMI overweight");
        }

        // Cabang 6: gaya hidup.
        if (d.Smoking == true)
        {
            score += 10;
            reasons.Add("merokok");
        }

        if (d.LowActivity == true)
        {
            score += 8;
            reasons.Add("aktivitas fisik rendah");
        }

        if (d.Male == true)
        {
            score += 4;
            reasons.Add("jenis kelamin laki-laki");
        }

        if (ContainsAny(input, "diabetes", "kolesterol", "kesemutan", "sakit kepala berat", "mual", "obesitas"))
        {
            score += 6;
            reasons.Add("gejala/faktor tambahan");
        }

        // Kombinasi decision tree agar bukan sekadar penjumlahan.
        if ((d.Age >= 60 && d.Hypertension == true && d.HeartDisease == true) ||
            (d.Age >= 55 && d.Hypertension == true && d.Glucose >= 200) ||
            (fastSymptom && (d.Hypertension == true || d.HeartDisease == true || d.Age >= 50)) ||
            (d.PreviousStroke == true && (d.Hypertension == true || d.Glucose >= 140 || d.Age >= 50)))
        {
            score = Math.Max(score, 78);
        }
        else if ((d.Age >= 50 && (d.Hypertension == true || d.HeartDisease == true || d.Glucose >= 140)) ||
                 (d.Bmi >= 30 && (d.Smoking == true || d.LowActivity == true)) ||
                 (d.Hypertension == true && d.Glucose >= 140))
        {
            score = Math.Max(score, 48);
        }

        score = Math.Clamp(score, 0, 100);

        string missing = BuildMissingInfoText(d);
        string level;
        string recommendation;

        if (score >= 70)
        {
            level = "🔴 Risiko Tinggi Stroke";
            recommendation = "Hasil decision tree menunjukkan risiko tinggi. Segera lakukan pemeriksaan ke fasilitas kesehatan, terutama bila gejala muncul mendadak. Faktor dominan: " + FormatReasons(reasons) + ".";
        }
        else if (score >= 40)
        {
            level = "🟠 Risiko Sedang";
            recommendation = "Hasil decision tree menunjukkan risiko sedang. Disarankan kontrol tekanan darah/gula darah dan konsultasi lanjutan. Faktor terkait: " + FormatReasons(reasons) + ".";
        }
        else
        {
            level = "🟢 Risiko Rendah";
            recommendation = reasons.Count > 0
                ? "Hasil decision tree menunjukkan risiko rendah, tetapi tetap perhatikan faktor: " + FormatReasons(reasons) + "."
                : "Hasil decision tree menunjukkan risiko rendah dari data yang terbaca. Tetap jaga pola hidup sehat dan lakukan pemeriksaan rutin.";
        }

        if (!string.IsNullOrWhiteSpace(missing))
            recommendation += " Data yang belum terbaca: " + missing + ".";

        return new PredictionResult(score, level, recommendation);
    }


    private static PredictionResult PredictBySelectedModel(PatientRiskData d, string input, string method)
    {
        method = method?.Trim() ?? "Decision Tree";
        if (method.Equals("Neural Network", StringComparison.OrdinalIgnoreCase))
            return NeuralNetworkPredict(d, input);
        if (method.Equals("Fuzzy Logic", StringComparison.OrdinalIgnoreCase))
            return FuzzyPredict(d, input);
        if (method.Equals("Hybrid AI", StringComparison.OrdinalIgnoreCase))
            return HybridPredict(d, input);
        return DecisionTreePredict(d, input);
    }

    // Simulasi model Neural Network lokal berbasis bobot numerik.
    // Catatan: ini fondasi supervised learning di aplikasi offline. Nantinya bobot bisa diganti dari model hasil training dataset.
    private static PredictionResult NeuralNetworkPredict(PatientRiskData d, string input)
    {
        double age = Math.Clamp(d.Age / 90.0, 0, 1);
        double glucose = Math.Clamp(d.Glucose / 300.0, 0, 1);
        double bmi = Math.Clamp(d.Bmi / 45.0, 0, 1);
        double hypertension = d.Hypertension == true ? 1 : 0;
        double heart = d.HeartDisease == true ? 1 : 0;
        double previous = d.PreviousStroke == true ? 1 : 0;
        double smoke = d.Smoking == true ? 1 : 0;
        double lowActivity = d.LowActivity == true ? 1 : 0;
        double male = d.Male == true ? 1 : 0;
        double symptom = ContainsAny(input, "wajah turun", "bicara pelo", "lumpuh", "mati rasa", "lengan lemah", "pusing mendadak") ? 1 : 0;

        // 2 hidden neuron sederhana dengan aktivasi sigmoid.
        double h1 = Sigmoid(-1.8 + 2.3 * age + 1.8 * hypertension + 1.7 * heart + 1.5 * glucose + 1.1 * bmi + 1.3 * smoke);
        double h2 = Sigmoid(-1.2 + 2.5 * previous + 2.2 * symptom + 1.1 * lowActivity + 0.5 * male + 1.1 * glucose);
        double output = Sigmoid(-2.0 + 2.8 * h1 + 3.0 * h2);
        int score = Math.Clamp((int)Math.Round(output * 100), 0, 100);

        string level = score >= 70 ? "🔴 Risiko Tinggi Stroke" : score >= 40 ? "🟠 Risiko Sedang" : "🟢 Risiko Rendah";
        string rec = score >= 70
            ? "Model Neural Network lokal membaca pola risiko tinggi. Disarankan pemeriksaan medis lanjutan, terutama jika ada gejala mendadak."
            : score >= 40
                ? "Model Neural Network lokal membaca risiko sedang. Pantau tekanan darah, gula darah, BMI, dan kebiasaan merokok."
                : "Model Neural Network lokal membaca risiko rendah dari data yang tersedia. Tetap lakukan pencegahan dan pemeriksaan rutin.";
        return new PredictionResult(score, level, rec + " Metode: supervised learning berbasis bobot awal.");
    }

    private static PredictionResult FuzzyPredict(PatientRiskData d, string input)
    {
        double ageRisk = Math.Max(Triangle(d.Age, 45, 60, 80), ShoulderHigh(d.Age, 60, 80));
        double glucoseRisk = Math.Max(Triangle(d.Glucose, 120, 170, 230), ShoulderHigh(d.Glucose, 180, 260));
        double bmiRisk = Math.Max(Triangle(d.Bmi, 24, 29, 34), ShoulderHigh(d.Bmi, 30, 42));
        double diseaseRisk = Math.Max(d.Hypertension == true ? 0.85 : 0.0, d.HeartDisease == true ? 0.9 : 0.0);
        double lifestyleRisk = Math.Max(d.Smoking == true ? 0.7 : 0.0, d.LowActivity == true ? 0.55 : 0.0);
        double symptomRisk = ContainsAny(input, "wajah turun", "bicara pelo", "lumpuh", "mati rasa", "lengan lemah", "pusing mendadak") ? 0.95 : 0.0;
        double previousRisk = d.PreviousStroke == true ? 0.95 : 0.0;

        double low = Math.Max(0, 1 - Math.Max(ageRisk, Math.Max(glucoseRisk, diseaseRisk)));
        double medium = Math.Max(Math.Min(ageRisk, Math.Max(glucoseRisk, bmiRisk)), Math.Min(lifestyleRisk, 0.75));
        double high = Math.Max(Math.Max(previousRisk, symptomRisk), Math.Max(Math.Min(ageRisk, diseaseRisk), Math.Min(glucoseRisk, diseaseRisk)));

        // Defuzzifikasi metode weighted average: rendah=25, sedang=55, tinggi=85.
        double denominator = low + medium + high;
        int score = denominator <= 0.001 ? 20 : (int)Math.Round(((low * 25) + (medium * 55) + (high * 85)) / denominator);
        score = Math.Clamp(score, 0, 100);

        string level = score >= 70 ? "🔴 Risiko Tinggi Stroke" : score >= 40 ? "🟠 Risiko Sedang" : "🟢 Risiko Rendah";
        string rec = $"Fuzzy Logic membaca derajat risiko: rendah {low:P0}, sedang {medium:P0}, tinggi {high:P0}. Hasil sudah didefuzzifikasi menjadi skor crisp {score}%.";
        return new PredictionResult(score, level, rec);
    }

    private static PredictionResult HybridPredict(PatientRiskData d, string input)
    {
        var dt = DecisionTreePredict(d, input);
        var nn = NeuralNetworkPredict(d, input);
        var fuzzy = FuzzyPredict(d, input);
        int score = (int)Math.Round((dt.Score * 0.40) + (nn.Score * 0.35) + (fuzzy.Score * 0.25));
        score = Math.Clamp(score, 0, 100);
        string level = score >= 70 ? "🔴 Risiko Tinggi Stroke" : score >= 40 ? "🟠 Risiko Sedang" : "🟢 Risiko Rendah";
        string rec = $"Hybrid AI menggabungkan Decision Tree ({dt.Score}%), Neural Network ({nn.Score}%), dan Fuzzy Logic ({fuzzy.Score}%). Skor final: {score}%.";
        return new PredictionResult(score, level, rec);
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    private static double Triangle(double x, double a, double b, double c)
    {
        if (x <= a || x >= c) return 0;
        if (Math.Abs(x - b) < 0.0001) return 1;
        return x < b ? (x - a) / (b - a) : (c - x) / (c - b);
    }

    private static double ShoulderHigh(double x, double start, double full)
    {
        if (x <= start) return 0;
        if (x >= full) return 1;
        return (x - start) / (full - start);
    }

    private static string NormalizeText(string value)
    {
        return (value ?? "")
            .ToLowerInvariant()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("=", ":");
    }

    private static bool ContainsAny(string input, params string[] keywords)
        => keywords.Any(k => input.Contains(k.ToLowerInvariant()));

    private static bool? ExtractBoolean(string input, params string[] labels)
    {
        foreach (var label in labels)
        {
            string l = label.ToLowerInvariant();
            var pattern = $@"{System.Text.RegularExpressions.Regex.Escape(l)}\s*:?\s*(ya|iya|yes|y|true|ada|pernah|positif|tidak|no|n|false|nggak|ga|gak|bukan|belum)";
            var match = System.Text.RegularExpressions.Regex.Match(input, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string v = match.Groups[1].Value.ToLowerInvariant();
                if (v is "ya" or "iya" or "yes" or "y" or "true" or "ada" or "pernah" or "positif")
                    return true;
                return false;
            }
        }

        return null;
    }

    private static double ExtractNumber(string input, params string[] labels)
    {
        foreach (var label in labels)
        {
            string l = label.ToLowerInvariant();
            int index = input.IndexOf(l, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            string tail = input[index..Math.Min(input.Length, index + 80)];
            var match = System.Text.RegularExpressions.Regex.Match(tail, @"\d+([\.,]\d+)?");
            if (match.Success && double.TryParse(match.Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double value))
                return value;
        }

        return 0;
    }

    private static string FormatReasons(List<string> reasons)
    {
        var unique = reasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(7)
            .ToList();

        return unique.Count == 0 ? "belum ada faktor dominan" : string.Join(", ", unique);
    }

    private static string BuildMissingInfoText(PatientRiskData d)
    {
        var missing = new List<string>();
        if (d.Age <= 0) missing.Add("usia");
        if (d.Hypertension is null) missing.Add("hipertensi");
        if (d.HeartDisease is null) missing.Add("penyakit jantung");
        if (d.Glucose <= 0) missing.Add("gula darah");
        if (d.Bmi <= 0) missing.Add("BMI");
        if (d.Smoking is null) missing.Add("status merokok");
        if (d.LowActivity is null) missing.Add("aktivitas fisik");
        return missing.Count == 0 ? "" : string.Join(", ", missing.Take(5));
    }

    private record PredictionResult(int Score, string LevelText, string Recommendation);

    private class PatientRiskData
    {
        public double Age { get; set; }
        public double Glucose { get; set; }
        public double Bmi { get; set; }
        public bool? Hypertension { get; set; }
        public bool? HeartDisease { get; set; }
        public bool? PreviousStroke { get; set; }
        public bool? Smoking { get; set; }
        public bool? LowActivity { get; set; }
        public bool? Male { get; set; }

        public static PatientRiskData FromForm(
            string? age,
            string? gender,
            string? hypertension,
            string? heartDisease,
            string? glucose,
            string? bmi,
            string? smoking,
            string? previousStroke,
            string? activity)
        {
            bool? lowActivity = null;
            if (!string.IsNullOrWhiteSpace(activity))
            {
                string a = activity.ToLowerInvariant();
                lowActivity = a.Contains("jarang") || a.Contains("kadang");
            }

            return new PatientRiskData
            {
                Age = ParseNumber(age),
                Glucose = ParseNumber(glucose),
                Bmi = ParseNumber(bmi),
                Male = ParseGender(gender),
                Hypertension = ParseYesNo(hypertension),
                HeartDisease = ParseYesNo(heartDisease),
                PreviousStroke = ParseYesNo(previousStroke),
                Smoking = ParseYesNo(smoking),
                LowActivity = lowActivity
            };
        }

        public static PatientRiskData FromFreeText(string rawText)
        {
            string input = NormalizeText(rawText);

            bool? smoking = ExtractBoolean(input, "merokok", "rokok", "perokok", "smoking");
            if (smoking is null && ContainsAny(input, "perokok aktif", "sering merokok", "suka merokok", "masih merokok")) smoking = true;
            if (smoking is null && ContainsAny(input, "tidak merokok", "bukan perokok", "berhenti merokok")) smoking = false;

            bool? lowActivity = ExtractBoolean(input, "aktivitas fisik", "olahraga", "aktifitas fisik");
            if (ContainsAny(input, "jarang olahraga", "jarang berolahraga", "kurang aktivitas", "tidak olahraga", "malas gerak", "sedentary")) lowActivity = true;
            if (ContainsAny(input, "rutin olahraga", "olahraga rutin", "aktif bergerak", "sering olahraga")) lowActivity = false;

            bool? male = ParseGender(input);

            double age = ExtractNumber(input, "usia", "umur", "age");
            if (age <= 0)
            {
                var m = System.Text.RegularExpressions.Regex.Match(input, @"\b(\d{1,3})\s*(tahun|thn|th)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) age = ParseNumber(m.Groups[1].Value);
            }

            double glucose = ExtractNumber(input, "rata-rata gula", "rata rata gula", "gula darah", "gula", "glukosa", "glucose");
            if (glucose <= 0 && ContainsAny(input, "gula tinggi", "diabetes")) glucose = 160;

            double bmi = ExtractNumber(input, "bmi", "body mass index", "indeks massa tubuh");
            if (bmi <= 0 && ContainsAny(input, "obesitas", "gemuk sekali")) bmi = 31;
            else if (bmi <= 0 && ContainsAny(input, "overweight", "kelebihan berat")) bmi = 27;

            bool? hypertension = ExtractBoolean(input, "hipertensi", "tekanan darah tinggi", "darah tinggi");
            if (hypertension is null && ContainsAny(input, "hipertensi", "darah tinggi", "tekanan darah tinggi"))
                hypertension = !ContainsAny(input, "tidak hipertensi", "tidak darah tinggi", "bukan hipertensi", "tidak punya hipertensi", "tidak punya darah tinggi");

            bool? heartDisease = ExtractBoolean(input, "penyakit jantung", "sakit jantung", "jantung", "heart disease");
            if (heartDisease is null && ContainsAny(input, "penyakit jantung", "sakit jantung", "jantung bermasalah"))
                heartDisease = !ContainsAny(input, "tidak penyakit jantung", "tidak sakit jantung", "tidak punya penyakit jantung", "jantung normal");

            bool? previousStroke = ExtractBoolean(input, "riwayat stroke", "pernah stroke", "stroke sebelumnya");
            if (previousStroke is null && ContainsAny(input, "pernah stroke", "riwayat stroke", "stroke sebelumnya"))
                previousStroke = !ContainsAny(input, "tidak pernah stroke", "belum pernah stroke", "tidak ada riwayat stroke");

            return new PatientRiskData
            {
                Age = age,
                Glucose = glucose,
                Bmi = bmi,
                Hypertension = hypertension,
                HeartDisease = heartDisease,
                PreviousStroke = previousStroke,
                Smoking = smoking,
                LowActivity = lowActivity,
                Male = male
            };
        }

        private static double ParseNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+([\.,]\d+)?");
            if (!match.Success) return 0;
            return double.TryParse(match.Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
        }

        private static bool? ParseYesNo(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v is "ya" or "iya" or "yes" or "y" or "true" or "ada" or "pernah") return true;
            if (v is "tidak" or "no" or "n" or "false" or "nggak" or "ga" or "gak" or "bukan" or "belum") return false;
            return null;
        }

        private static bool? ParseGender(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.ToLowerInvariant();
            if (ContainsAny(v, "laki-laki", "laki laki", "pria", "cowok", "male")) return true;
            if (ContainsAny(v, "perempuan", "wanita", "female", "cewek")) return false;
            return null;
        }
    }

    private async Task OpenChatPopupAsync()
    {
        ChatPopup.Opacity = 0;
        ChatPopup.Scale = 0.96;
        ChatPopup.IsVisible = true;
        ChatFab.IsVisible = false;
        await Task.WhenAll(
            ChatPopup.FadeTo(1, 150, Easing.CubicOut),
            ChatPopup.ScaleTo(1, 150, Easing.CubicOut));
    }

    private async Task CloseChatPopupAsync()
    {
        await Task.WhenAll(
            ChatPopup.FadeTo(0, 100, Easing.CubicIn),
            ChatPopup.ScaleTo(0.96, 100, Easing.CubicIn));
        ChatPopup.IsVisible = false;
        ChatPopup.Opacity = 1;
        ChatPopup.Scale = 1;
        ChatFab.IsVisible = true;
    }

    private async void OnChatFabClicked(object sender, EventArgs e)
    {
        await OpenChatPopupAsync();
    }

    private async void OnCloseChatClicked(object sender, EventArgs e)
    {
        await CloseChatPopupAsync();
    }

    private async void OnCloseChatBackdropTapped(object sender, TappedEventArgs e)
    {
        await CloseChatPopupAsync();
    }

    private void OnMinimizeChatClicked(object sender, EventArgs e)
    {
        _isChatMinimized = !_isChatMinimized;
        ChatBody.IsVisible = !_isChatMinimized;
        ChatInputBar.IsVisible = !_isChatMinimized;
        BtnMinimizeChat.Text = _isChatMinimized ? "□" : "_";
        ChatWindowFrame.HeightRequest = _isChatMinimized ? 78 : Math.Max(360, _chatStartHeight);
    }

    private void OnChatHeaderPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _chatStartTranslationX = ChatWindowFrame.TranslationX;
                _chatStartTranslationY = ChatWindowFrame.TranslationY;
                break;
            case GestureStatus.Running:
                ChatWindowFrame.TranslationX = _chatStartTranslationX + e.TotalX;
                ChatWindowFrame.TranslationY = _chatStartTranslationY + e.TotalY;
                break;
        }
    }

    private void OnChatResizePanUpdated(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _chatStartWidth = ChatWindowFrame.WidthRequest > 0 ? ChatWindowFrame.WidthRequest : ChatWindowFrame.Width;
                _chatStartHeight = ChatWindowFrame.HeightRequest > 0 ? ChatWindowFrame.HeightRequest : ChatWindowFrame.Height;
                break;
            case GestureStatus.Running:
                if (_isChatMinimized)
                    return;

                ChatWindowFrame.WidthRequest = Math.Clamp(_chatStartWidth + e.TotalX, 340, 760);
                ChatWindowFrame.HeightRequest = Math.Clamp(_chatStartHeight + e.TotalY, 420, 820);
                break;
        }
    }

    private async void OnChatSuggestionClicked(object sender, EventArgs e)
    {
        if (_isChatBusy)
            return;

        string question = (sender as Button)?.Text ?? "";
        EntryChat.Text = question;
        await SetChatAnswerAsync(question);
    }

    private async void OnSendChatClicked(object sender, EventArgs e)
    {
        if (_isChatBusy)
            return;

        string question = EntryChat.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(question))
        {
            AddChatBubble("Silakan ketik pertanyaan terlebih dahulu.", isUser: false);
            return;
        }

        EntryChat.Text = string.Empty;
        await SetChatAnswerAsync(question);
    }

    private async Task SetChatAnswerAsync(string question)
    {
        SetChatInputEnabled(false);

        AppendChatLine("User", question);
        AppendChatLine("AI", "Gemini AI sedang memproses jawaban...");

        try
        {
            var aiService = new GeminiAiService();
            string answer = await aiService.AskAsync(question);
            ReplaceLastAiLoading(answer);
        }
        catch (Exception ex)
        {
            ReplaceLastAiLoading($"NLP Assistant gagal dijalankan: {ex.Message}");
        }
        finally
        {
            SetChatInputEnabled(true);
            EntryChat.Focus();
            await ScrollChatToBottomAsync();
        }
    }

    private void SetChatInputEnabled(bool enabled)
    {
        _isChatBusy = !enabled;
        EntryChat.IsEnabled = enabled;
        BtnSendChat.IsEnabled = enabled;
        BtnSuggestionStroke.IsEnabled = enabled;
        BtnSuggestionSensor.IsEnabled = enabled;
        BtnSuggestionFace.IsEnabled = enabled;
        BtnSendChat.Text = enabled ? "➤" : "...";
    }

    private void AppendChatLine(string role, string text)
    {
        if (_chatTranscript.Length > 0)
            _chatTranscript.AppendLine().AppendLine();

        _chatTranscript.Append(role).Append(": ").AppendLine(text);
        bool isUser = role.Equals("User", StringComparison.OrdinalIgnoreCase);
        var label = AddChatBubble(text, isUser);
        if (!isUser && text.Contains("sedang memproses", StringComparison.OrdinalIgnoreCase))
            _lastAiLoadingLabel = label;
        _ = ScrollChatToBottomAsync();
    }

    private void ReplaceLastAiLoading(string answer)
    {
        const string loading = "AI: Gemini AI sedang memproses jawaban...";
        string transcript = _chatTranscript.ToString();

        int index = transcript.LastIndexOf(loading, StringComparison.Ordinal);
        if (index >= 0)
        {
            _chatTranscript.Clear();
            _chatTranscript.Append(transcript[..index]);
            string cleanAnswer = CleanChatText(answer);
            _chatTranscript.Append("AI: ").AppendLine(cleanAnswer);
            if (_lastAiLoadingLabel != null)
            {
                _lastAiLoadingLabel.Text = cleanAnswer;
                _lastAiLoadingLabel = null;
            }
        }
        else
        {
            AppendChatLine("AI", CleanChatText(answer));
            return;
        }

        _ = ScrollChatToBottomAsync();
    }

    private Label AddChatBubble(string text, bool isUser)
    {
        var row = new Grid
        {
            ColumnDefinitions = isUser
                ? new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
                : new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star }
                },
            ColumnSpacing = 8,
            HorizontalOptions = LayoutOptions.Fill
        };

        var label = new Label
        {
            Text = CleanChatText(text),
            TextColor = isUser ? Colors.White : Color.FromArgb("#0F172A"),
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap
        };

        var bubble = new Frame
        {
            Padding = new Thickness(14, 9),
            CornerRadius = 16,
            HasShadow = false,
            BorderColor = isUser ? Color.FromArgb("#4F46E5") : Color.FromArgb("#E2E8F0"),
            BackgroundColor = isUser ? Color.FromArgb("#4F46E5") : Color.FromArgb("#F1F5F9"),
            MaximumWidthRequest = 340,
            Content = label,
            HorizontalOptions = isUser ? LayoutOptions.End : LayoutOptions.Start
        };

        if (isUser)
        {
            row.Add(bubble, 1);
        }
        else
        {
            var avatar = new Frame
            {
                WidthRequest = 28,
                HeightRequest = 28,
                CornerRadius = 14,
                Padding = 0,
                HasShadow = false,
                BorderColor = Color.FromArgb("#CBD5E1"),
                BackgroundColor = Color.FromArgb("#EEF2FF"),
                Content = new Label
                {
                    Text = "AI",
                    TextColor = Color.FromArgb("#4F46E5"),
                    FontSize = 10,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            };

            row.Add(avatar, 0);
            row.Add(bubble, 1);
        }

        ChatMessages.Children.Add(row);
        return label;
    }

    private static string CleanChatText(string text)
    {
        return (text ?? "")
            .Replace("**", "")
            .Replace("__", "")
            .Trim();
    }

    private Task ScrollChatToBottomAsync()
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await Task.Delay(50);
            await ChatAnswerScroll.ScrollToAsync(0, ChatMessages.Height, false);
        });
    }

    private async void OnMenuDashboardClicked(object sender, EventArgs e)
    {
        await AnimateMenuButton(BtnMenuDashboard);
        ShowSection("dashboard");
    }


    private async void OnMenuHistoryClicked(object sender, EventArgs e)
    {
        await AnimateMenuButton(BtnHistory);
        ShowSection("history");
    }

    private async void OnMenuDatabaseClicked(object sender, EventArgs e)
    {
        await AnimateMenuButton(BtnDatabase);
        LoadUserDatabase();
        ShowSection("database");
    }

    private async void OnMenuChatClicked(object sender, EventArgs e)
    {
        await AnimateMenuButton(BtnMenuChat);
        await OpenChatPopupAsync();
    }

    private async void OnShowFaceFolderClicked(object sender, EventArgs e)
    {
        await DisplayAlert("Folder Foto Wajah", FileSystem.AppDataDirectory, "OK");
    }

    private async void OnRefreshDatabaseClicked(object sender, EventArgs e)
    {
        LoadUserDatabase();
        await DisplayAlert("Database", "Data akun dan foto wajah berhasil dimuat ulang.", "OK");
    }

    private async void OnResetSelectedFaceClicked(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string username)
            return;

        string facePath = Path.Combine(FileSystem.AppDataDirectory, $"{username}_face.png");
        string embeddingPath = FaceEmbeddingService.GetTemplatePath(username);
        bool deleted = false;

        if (File.Exists(facePath))
        {
            File.Delete(facePath);
            deleted = true;
        }

        if (File.Exists(embeddingPath))
        {
            File.Delete(embeddingPath);
            deleted = true;
        }

        LoadUserDatabase();

        if (deleted)
            await DisplayAlert("Berhasil", $"Data wajah/embedding user '{username}' berhasil dihapus.", "OK");
        else
            await DisplayAlert("Info", $"Data wajah user '{username}' tidak ditemukan.", "OK");
    }


    private async void OnCreateEmbeddingSelectedFaceClicked(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string username)
            return;

        string facePath = Path.Combine(FileSystem.AppDataDirectory, $"{username}_face.png");
        string embeddingPath = FaceEmbeddingService.GetTemplatePath(username);

        if (!File.Exists(facePath))
        {
            await DisplayAlert("Gagal", $"Foto lama user '{username}' tidak ditemukan.", "OK");
            return;
        }

        try
        {
            byte[] faceBytes = File.ReadAllBytes(facePath);
            var template = await FaceEmbeddingService.CreateTemplateAsync(faceBytes, username);
            await FaceEmbeddingService.SaveTemplateAsync(template, embeddingPath);
            LoadUserDatabase();

            await DisplayAlert("Berhasil", $"Template face embedding user '{username}' berhasil dibuat.\n\n{embeddingPath}", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Gagal Membuat Embedding", ex.Message, "OK");
        }
    }

    private async void OnDeleteSelectedUserClicked(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string username)
            return;

        var account = await AppDatabase.Instance.GetUserAsync(username);
        string role = account?.Role ?? Preferences.Get($"user_role_{username}", "Publik");
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlert("Ditolak", "Akun Admin tidak boleh dihapus dari menu ini.", "OK");
            return;
        }

        bool confirm = await DisplayAlert(
            "Hapus Akun Publik",
            $"Yakin ingin menghapus akun publik '{username}' beserta foto wajahnya?",
            "Hapus",
            "Batal");

        if (!confirm)
            return;

        await AppDatabase.Instance.DeleteUserAsync(username);

        string facePath = Path.Combine(FileSystem.AppDataDirectory, $"{username}_face.png");
        string embeddingPath = FaceEmbeddingService.GetTemplatePath(username);
        if (File.Exists(facePath))
            File.Delete(facePath);
        if (File.Exists(embeddingPath))
            File.Delete(embeddingPath);

        LoadUserDatabase();
        LoadHistory();
        await DisplayAlert("Berhasil", $"Akun publik '{username}' sudah dihapus.", "OK");
    }


    private async void OnDeleteHistoryClicked(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string rawKey || string.IsNullOrWhiteSpace(rawKey))
            return;

        bool confirm = await DisplayAlert(
            "Hapus Riwayat",
            "Yakin ingin menghapus data riwayat screening ini?",
            "Hapus",
            "Batal");

        if (!confirm)
            return;

        if (int.TryParse(rawKey, out int historyId))
            await AppDatabase.Instance.DeleteScreeningHistoryAsync(historyId);
        LoadHistory();
        await AnimatePanel(AdminPanel);
    }

    private async void OnClearHistoryClicked(object sender, EventArgs e)
    {
        bool confirm = await DisplayAlert(
            "Hapus Semua Riwayat",
            "Yakin ingin menghapus SEMUA data riwayat screening? Akun dan foto wajah tidak ikut terhapus.",
            "Hapus Semua",
            "Batal");

        if (!confirm)
            return;

        await AppDatabase.Instance.ClearScreeningHistoryAsync();
        LoadHistory();
        await DisplayAlert("Berhasil", "Semua riwayat screening berhasil dihapus.", "OK");
        await AnimatePanel(AdminPanel);
    }

    private async void ShowSection(string section)
    {
        DashboardPanel.IsVisible = section == "dashboard";
        PublicPanel.IsVisible = section == "prediction" && _role != "Admin";
        ResultPanel.IsVisible = section == "prediction" && _role != "Admin";
        AdminPanel.IsVisible = section == "history" && _role == "Admin";
        DatabasePanel.IsVisible = section == "database" && _role == "Admin";

        SetActiveMenu(section);

        VisualElement? activePanel = section switch
        {
            "dashboard" => DashboardPanel,
            "prediction" => PublicPanel,
            "history" => AdminPanel,
            "database" => DatabasePanel,
            _ => DashboardPanel
        };

        if (activePanel?.IsVisible == true)
            await AnimatePanel(activePanel);
    }

    private void SetActiveMenu(string section)
    {
        if (_role != "Admin")
            return;

        ResetMenuButton(BtnMenuDashboard);
        ResetMenuButton(BtnHistory);
        ResetMenuButton(BtnDatabase);
        ResetMenuButton(BtnMenuChat);

        Button activeButton = section switch
        {
            "dashboard" => BtnMenuDashboard,
            "history" => BtnHistory,
            "database" => BtnDatabase,
            _ => BtnMenuDashboard
        };

        activeButton.BackgroundColor = Color.FromArgb("#4F46E5");
        activeButton.TextColor = Colors.White;
    }

    private static async Task AnimatePanel(VisualElement panel)
    {
        panel.Opacity = 0;
        panel.TranslationY = 12;
        await Task.WhenAll(
            panel.FadeTo(1, 170, Easing.CubicOut),
            panel.TranslateTo(0, 0, 170, Easing.CubicOut));
    }

    private static void ResetMenuButton(Button button)
    {
        button.BackgroundColor = Colors.Transparent;
        button.TextColor = Color.FromArgb("#334155");
    }

    private static async Task AnimateMenuButton(Button button)
    {
        button.BackgroundColor = Color.FromArgb("#EEF2FF");
        button.TextColor = Color.FromArgb("#4F46E5");
        await button.ScaleTo(0.97, 55, Easing.CubicOut);
        await button.ScaleTo(1.00, 85, Easing.CubicInOut);
    }

    private void OnLogoutClicked(object sender, EventArgs e)
    {
        Preferences.Remove("session_username");
        Preferences.Remove("session_role");
        Application.Current!.MainPage = new MainPage();
    }

    public class HistoryItem
    {
        public string User { get; set; } = "";
        public string Score { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Detail { get; set; } = "";
        public string RawKey { get; set; } = "";
    }

    public class UserDbItem
    {
        public string Username { get; set; } = "";
        public string Role { get; set; } = "";
        public string FaceFile { get; set; } = "";
        public string FacePath { get; set; } = "";
        public string Status { get; set; } = "";
        public bool CanDelete { get; set; }
        public bool CanCreateEmbedding { get; set; }
    }
}
