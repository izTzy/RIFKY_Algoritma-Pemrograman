using Microsoft.Maui.Media;
using System.IO;
using System.Threading;

#if WINDOWS
using OpenCvSharp;
using OpenCvSharp.Face;
#endif

namespace KAMERA
{
    public partial class FaceVerificationPage : ContentPage
    {
        private readonly string _username;
        private readonly string _role;
        private readonly string _dbPhotoPath;
        private readonly string _embeddingPath;

        private CancellationTokenSource? _scanCts;
        private bool _isNavigating;

        // Semakin kecil confidence/distance, semakin mirip wajahnya.
        // Revisi multi-lokasi: sample registration/update user yang sama ikut dilatih sebagai positive sample.
        // Ambang dibuat cukup toleran untuk cahaya/lokasi berbeda, tetapi tetap ditahan oleh negative sample user lain.
        private const double DirectFaceMatchThreshold = 76.0;

        // Jika belum ada sample wajah user lain sebagai pembanding negatif, sistem dibuat sedikit lebih hati-hati.
        private const double NoNegativeSampleThreshold = 70.0;

        // Cukup 3 frame berturut-turut supaya user asli tidak terlalu sulit masuk,
        // tetapi tetap tidak langsung diterima karena salah deteksi 1 frame.
        private const int RequiredSuccessFrames = 3;
        private const int FaceVerificationTimeoutSeconds = 18;

        public FaceVerificationPage(string username, string role)
        {
            InitializeComponent();

            _username = username;
            _role = role;

            _dbPhotoPath = Path.Combine(FileSystem.AppDataDirectory, $"{username}_face.png");
            _embeddingPath = FaceEmbeddingService.GetTemplatePath(username);

            LabelRole.Text = $"Live Face Lock | User: {username} | Role: {role}";
            LabelStatus.Text = "Arahkan wajah ke kamera. Sistem memakai LBPH seimbang + pembanding wajah user lain jika tersedia.";
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            StartLiveFaceScan();
        }

        protected override void OnDisappearing()
        {
            StopLiveFaceScan();
            base.OnDisappearing();
        }

        private void OnStartScanClicked(object sender, EventArgs e)
        {
            StartLiveFaceScan();
        }

        private void StartLiveFaceScan()
        {
            StopLiveFaceScan();

            _isNavigating = false;
            _scanCts = new CancellationTokenSource();

            BtnLanjut.IsVisible = false;
            ScanIndicator.IsVisible = true;
            ScanIndicator.IsRunning = true;

            LabelSimilarity.Text = "Status: membuka kamera laptop...";
            LabelStatus.Text = "Mode seimbang aktif: wajah live dibandingkan dengan user login dan ditolak jika lebih cocok ke user lain.";

#if WINDOWS
            _ = Task.Run(() => RunWindowsLiveScanAsync(_scanCts.Token));
#else
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                ScanIndicator.IsRunning = false;
                ScanIndicator.IsVisible = false;
                LabelSimilarity.Text = "Live scan kamera laptop hanya aktif untuk Windows.";
                LabelStatus.Text = "Jalankan project dengan target Windows Machine.";

                await DisplayAlert(
                    "Target Belum Didukung",
                    "Mode live face lock ini dibuat untuk Windows laptop. Jalankan project dengan target Windows Machine.",
                    "OK");
            });
#endif
        }

        private void StopLiveFaceScan()
        {
            try
            {
                _scanCts?.Cancel();
                _scanCts?.Dispose();
                _scanCts = null;
            }
            catch
            {
                // Abaikan error saat menghentikan kamera.
            }
        }

#if WINDOWS
        private async Task RunWindowsLiveScanAsync(CancellationToken token)
        {
            try
            {
                if (!File.Exists(_dbPhotoPath))
                {
                    await ShowMessageOnUiAsync(
                        "Error Database",
                        "Foto wajah untuk user ini tidak ditemukan. Silakan register ulang dan ambil foto wajah.");
                    return;
                }

                string cascadePath = await EnsureCascadeFileAsync();
                using var faceCascade = new CascadeClassifier(cascadePath);

                if (faceCascade.Empty())
                {
                    await ShowMessageOnUiAsync("Error", "File Haar Cascade tidak bisa dibaca.");
                    return;
                }

                using var referenceImage = Cv2.ImRead(_dbPhotoPath, ImreadModes.Color);
                if (referenceImage.Empty())
                {
                    await ShowMessageOnUiAsync("Error", "Foto wajah database tidak bisa dibaca.");
                    return;
                }

                using var referenceGray = new Mat();
                Cv2.CvtColor(referenceImage, referenceGray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(referenceGray, referenceGray);

                var referenceFaces = faceCascade.DetectMultiScale(
                    referenceGray,
                    scaleFactor: 1.1,
                    minNeighbors: 5,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new OpenCvSharp.Size(80, 80));

                if (referenceFaces.Length == 0)
                {
                    await ShowMessageOnUiAsync(
                        "Wajah Tidak Terdeteksi",
                        "Foto registrasi tidak memiliki wajah yang jelas. Silakan update wajah dengan posisi lurus dan pencahayaan cukup.");
                    return;
                }

                var referenceRect = referenceFaces
                    .OrderByDescending(r => r.Width * r.Height)
                    .First();

                using var referenceFace = NormalizeFaceFromGray(referenceGray, referenceRect);

                using var trainedRecognizer = await CreateBalancedUserRecognizerAsync(referenceFace, faceCascade);
                var referenceRecognizer = trainedRecognizer.Recognizer;
                double activeThreshold = trainedRecognizer.HasNegativeSamples
                    ? DirectFaceMatchThreshold
                    : NoNegativeSampleThreshold;

                using var capture = new VideoCapture(0);
                if (!capture.IsOpened())
                {
                    await ShowMessageOnUiAsync(
                        "Kamera Tidak Terbuka",
                        "Kamera laptop tidak bisa dibuka. Pastikan kamera tidak sedang dipakai aplikasi lain dan izin kamera aktif.");
                    return;
                }

                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);
                capture.Set(VideoCaptureProperties.Fps, 30);

                int successFrames = 0;
                DateTime verificationStartTime = DateTime.Now;

                using var frame = new Mat();
                using var gray = new Mat();

                while (!token.IsCancellationRequested && !_isNavigating)
                {
                    capture.Read(frame);

                    if (frame.Empty())
                    {
                        await Task.Delay(80, token);
                        continue;
                    }

                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.EqualizeHist(gray, gray);

                    var faces = faceCascade.DetectMultiScale(
                        gray,
                        scaleFactor: 1.1,
                        minNeighbors: 5,
                        flags: HaarDetectionTypes.ScaleImage,
                        minSize: new OpenCvSharp.Size(80, 80));

                    int predictedLabel = -1;
                    double confidence = double.MaxValue;
                    double similarity = 0;
                    bool isMatch = false;
                    string statusText;

                    if (faces.Length == 1)
                    {
                        var faceRect = faces[0];

                        using var liveFace = NormalizeFaceFromGray(gray, faceRect);

                        referenceRecognizer.Predict(
                            liveFace,
                            out predictedLabel,
                            out confidence);

                        similarity = ConvertLbphConfidenceToSimilarity(confidence);

                        // Label 1 = user yang sedang login.
                        // Label 2 = wajah user lain sebagai pembanding negatif.
                        // Jadi orang lain tidak cukup hanya "mirip sedikit" ke user utama.
                        isMatch = predictedLabel == 1 && confidence <= activeThreshold;
                        successFrames = isMatch ? successFrames + 1 : 0;

                        Cv2.Rectangle(frame, faceRect, isMatch ? Scalar.LimeGreen : Scalar.Red, 2);

                        string labelInfo = predictedLabel switch
                        {
                            1 => "user",
                            2 => "user-lain",
                            _ => predictedLabel.ToString()
                        };

                        statusText = isMatch
                            ? $"Wajah cocok {successFrames}/{RequiredSuccessFrames} | label={labelInfo} | dist={confidence:F1}/{activeThreshold:F0}"
                            : $"Belum cocok | label={labelInfo} | dist={confidence:F1}/{activeThreshold:F0} | Pos={trainedRecognizer.PositiveSampleCount} | Neg={trainedRecognizer.NegativeSampleCount}";

                        if (successFrames >= RequiredSuccessFrames)
                        {
                            _isNavigating = true;
                            await NavigateToDashboardAsync();
                            break;
                        }
                    }
                    else
                    {
                        successFrames = 0;
                        statusText = faces.Length switch
                        {
                            0 => "Wajah tidak terdeteksi.",
                            _ => $"{faces.Length} wajah terdeteksi. Pastikan hanya satu wajah di kamera."
                        };

                        foreach (var faceRect in faces)
                            Cv2.Rectangle(frame, faceRect, Scalar.Orange, 2);
                    }

                    if ((DateTime.Now - verificationStartTime).TotalSeconds >= FaceVerificationTimeoutSeconds)
                    {
                        await ShowMessageOnUiAsync(
                            "Autentikasi Gagal",
                            "Wajah tidak cocok dengan akun ini. Coba update wajah dengan cahaya terang dan posisi lurus. Jika belum ada user lain sebagai pembanding, daftarkan minimal 1 user lain agar validasi lebih akurat.");
                        break;
                    }

                    await UpdatePreviewOnUiAsync(frame, statusText);
                    await Task.Delay(60, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal saat halaman ditutup / scan dihentikan.
            }
            catch (ObjectDisposedException)
            {
                // Normal saat OpenCV sedang melepas resource native.
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    await ShowMessageOnUiAsync("Error Live Face Lock", ex.Message);
            }
        }

        private sealed class TrainedFaceRecognizer : IDisposable
        {
            public LBPHFaceRecognizer Recognizer { get; init; } = null!;
            public bool HasNegativeSamples => NegativeSampleCount > 0;
            public int PositiveSampleCount { get; init; }
            public int NegativeSampleCount { get; init; }

            public void Dispose() => Recognizer.Dispose();
        }

        private async Task<TrainedFaceRecognizer> CreateBalancedUserRecognizerAsync(
            Mat referenceFace,
            CascadeClassifier faceCascade)
        {
            if (referenceFace.Empty())
                throw new InvalidOperationException("Foto wajah registrasi gagal dinormalisasi.");

            var trainingFaces = new List<Mat>();
            var trainingLabels = new List<int>();

            try
            {
                AddTrainingVariants(trainingFaces, trainingLabels, referenceFace, label: 1);

                foreach (var userFacePath in await GetCurrentUserRegisteredFacePhotoPathsAsync())
                {
                    try
                    {
                        using var userImage = Cv2.ImRead(userFacePath, ImreadModes.Color);
                        if (userImage.Empty())
                            continue;

                        using var userFace = ExtractNormalizedFace(userImage, faceCascade);
                        if (userFace.Empty())
                            continue;

                        AddTrainingVariants(trainingFaces, trainingLabels, userFace, label: 1);
                    }
                    catch
                    {
                        // Abaikan sample user saat ini yang rusak/tidak terbaca.
                    }
                }

                foreach (var otherFacePath in await GetOtherRegisteredFacePhotoPathsAsync())
                {
                    try
                    {
                        using var otherImage = Cv2.ImRead(otherFacePath, ImreadModes.Color);
                        if (otherImage.Empty())
                            continue;

                        using var otherFace = ExtractNormalizedFace(otherImage, faceCascade);
                        if (otherFace.Empty())
                            continue;

                        AddTrainingVariants(trainingFaces, trainingLabels, otherFace, label: 2);
                    }
                    catch
                    {
                        // Abaikan sample user lain yang rusak/tidak terbaca.
                    }
                }

                var recognizer = LBPHFaceRecognizer.Create(
                    radius: 1,
                    neighbors: 8,
                    gridX: 8,
                    gridY: 8,
                    threshold: double.MaxValue);

                recognizer.Train(trainingFaces, trainingLabels.ToArray());

                return new TrainedFaceRecognizer
                {
                    Recognizer = recognizer,
                    PositiveSampleCount = trainingLabels.Count(x => x == 1),
                    NegativeSampleCount = trainingLabels.Count(x => x == 2)
                };
            }
            finally
            {
                foreach (var face in trainingFaces)
                    face.Dispose();
            }
        }

        private async Task<IReadOnlyList<string>> GetCurrentUserRegisteredFacePhotoPathsAsync()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var samples = await FaceDataSheetService.GetSamplesForUserAsync(_username);
                foreach (var sample in samples)
                {
                    if (!string.Equals(sample.PhotoPath, _dbPhotoPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(sample.PhotoPath))
                    {
                        paths.Add(sample.PhotoPath);
                    }
                }
            }
            catch
            {
                // Kalau datasheet belum ada/rusak, recognizer tetap memakai foto utama.
            }

            return paths.ToList();
        }

        private async Task<IReadOnlyList<string>> GetOtherRegisteredFacePhotoPathsAsync()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string appDir = FileSystem.AppDataDirectory;

            foreach (var path in Directory.GetFiles(appDir, "*_face.png"))
            {
                if (!string.Equals(path, _dbPhotoPath, StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            try
            {
                var datasheet = await FaceDataSheetService.LoadAsync();
                foreach (var record in datasheet)
                {
                    if (!record.Username.Equals(_username, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(record.PhotoPath))
                    {
                        paths.Add(record.PhotoPath);
                    }
                }
            }
            catch
            {
                // Kalau datasheet belum ada/rusak, tetap pakai file *_face.png dari AppData.
            }

            return paths.ToList();
        }

        private static void AddTrainingVariants(
            List<Mat> trainingFaces,
            List<int> trainingLabels,
            Mat face,
            int label)
        {
            trainingFaces.Add(face.Clone());
            trainingLabels.Add(label);

            var flipped = new Mat();
            Cv2.Flip(face, flipped, FlipMode.Y);
            trainingFaces.Add(flipped);
            trainingLabels.Add(label);

            trainingFaces.Add(AdjustBrightnessContrast(face, alpha: 0.95, beta: -8));
            trainingLabels.Add(label);

            trainingFaces.Add(AdjustBrightnessContrast(face, alpha: 1.05, beta: 8));
            trainingLabels.Add(label);

            trainingFaces.Add(AdjustBrightnessContrast(face, alpha: 0.88, beta: -18));
            trainingLabels.Add(label);

            trainingFaces.Add(AdjustBrightnessContrast(face, alpha: 1.12, beta: 18));
            trainingLabels.Add(label);

            trainingFaces.Add(AdjustBrightnessContrast(face, alpha: 1.18, beta: -4));
            trainingLabels.Add(label);
        }


        private static Mat ExtractNormalizedFace(Mat image, CascadeClassifier faceCascade)
        {
            if (image.Empty())
                return new Mat();

            using var gray = new Mat();
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);

            var faces = faceCascade.DetectMultiScale(
                gray,
                scaleFactor: 1.1,
                minNeighbors: 5,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: new OpenCvSharp.Size(80, 80));

            if (faces.Length == 0)
                return new Mat();

            var faceRect = faces
                .OrderByDescending(r => r.Width * r.Height)
                .First();

            return NormalizeFaceFromGray(gray, faceRect);
        }

        private static Mat NormalizeFaceFromGray(Mat gray, OpenCvSharp.Rect faceRect)
        {
            var safeRect = ExpandRect(faceRect, gray.Width, gray.Height, 0.05);
            using var face = new Mat(gray, safeRect);
            var resized = new Mat();

            Cv2.Resize(
                face,
                resized,
                new OpenCvSharp.Size(200, 200),
                0,
                0,
                InterpolationFlags.Area);

            Cv2.EqualizeHist(resized, resized);
            return resized;
        }

        private static Mat AdjustBrightnessContrast(Mat source, double alpha, double beta)
        {
            var adjusted = new Mat();
            source.ConvertTo(adjusted, source.Type(), alpha, beta);
            return adjusted;
        }

        private static double ConvertLbphConfidenceToSimilarity(double confidence)
        {
            if (double.IsNaN(confidence) || double.IsInfinity(confidence))
                return 0;

            return Math.Clamp(100.0 - confidence, 0, 100);
        }

        private static OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, int maxWidth, int maxHeight, double paddingRatio)
        {
            int padX = (int)(rect.Width * paddingRatio);
            int padY = (int)(rect.Height * paddingRatio);

            int x = Math.Max(0, rect.X - padX);
            int y = Math.Max(0, rect.Y - padY);
            int right = Math.Min(maxWidth, rect.X + rect.Width + padX);
            int bottom = Math.Min(maxHeight, rect.Y + rect.Height + padY);

            return new OpenCvSharp.Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
        }

        private static async Task<string> EnsureCascadeFileAsync()
        {
            string targetPath = Path.Combine(
                FileSystem.AppDataDirectory,
                "haarcascade_frontalface_default.xml");

            if (File.Exists(targetPath))
                return targetPath;

            await using var input =
                await FileSystem.OpenAppPackageFileAsync("haarcascade_frontalface_default.xml");

            await using var output = File.Create(targetPath);
            await input.CopyToAsync(output);

            return targetPath;
        }

        private Task UpdatePreviewOnUiAsync(Mat frame, string statusText)
        {
            Cv2.ImEncode(".jpg", frame, out var imageBytes);

            return MainThread.InvokeOnMainThreadAsync(() =>
            {
                LabelSimilarity.Text = statusText;
                CameraPreview.Source = ImageSource.FromStream(() => new MemoryStream(imageBytes));
            });
        }
#endif

        private async void OnUpdateFaceClicked(object sender, EventArgs e)
        {
            StopLiveFaceScan();

            bool confirm = await DisplayAlert(
                "Update Wajah",
                $"Ganti data wajah untuk akun '{_username}'? Akun dan password tetap sama, hanya foto + template biometrik yang diperbarui.",
                "Update",
                "Batal");

            if (!confirm)
            {
                StartLiveFaceScan();
                return;
            }

            try
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo == null)
                {
                    StartLiveFaceScan();
                    return;
                }

                byte[] newFaceBytes;
                using (var photoStream = await photo.OpenReadAsync())
                using (var memoryStream = new MemoryStream())
                {
                    await photoStream.CopyToAsync(memoryStream);
                    newFaceBytes = memoryStream.ToArray();
                }

                CameraPreview.Source = ImageSource.FromStream(() => new MemoryStream(newFaceBytes));

                File.WriteAllBytes(_dbPhotoPath, newFaceBytes);

                // Tetap simpan template/datasheet agar fitur lama tidak hilang, walaupun login sekarang memakai logika direct LBPH file ke-1.
                try
                {
                    var template = await FaceEmbeddingService.CreateTemplateAsync(newFaceBytes, _username);
                    await FaceEmbeddingService.SaveTemplateAsync(template, _embeddingPath);
                    await FaceDataSheetService.SaveNewSampleAsync(_username, _role, newFaceBytes, "verify-page-update-camera");
                }
                catch
                {
                    // Direct LBPH tetap bisa berjalan dari foto utama meski embedding/datasheet gagal dibuat.
                }

                BtnLanjut.IsVisible = false;

                await DisplayAlert(
                    "Update Wajah Berhasil",
                    $"Data wajah akun '{_username}' berhasil diperbarui. Silakan tekan Mulai Verifikasi Wajah untuk login.",
                    "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Gagal Update Wajah", ex.Message, "OK");
            }
        }

        private async void OnShowFacePathClicked(object sender, EventArgs e)
        {
            await DisplayAlert(
                "Lokasi Data Wajah",
                $"Foto utama:\n{_dbPhotoPath}\n\nTemplate utama:\n{_embeddingPath}\n\nDatasheet:\n{FaceDataSheetService.DataSheetPath}\n\nFolder sample:\n{FaceDataSheetService.SamplesFolder}\n\nMode login aktif: LBPH seimbang + negative sample user lain.",
                "OK");
        }

        private Task ShowMessageOnUiAsync(string title, string message)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                ScanIndicator.IsRunning = false;
                ScanIndicator.IsVisible = false;

                LabelSimilarity.Text = message;
                LabelStatus.Text = message;

                await DisplayAlert(title, message, "OK");
            });
        }

        private Task NavigateToDashboardAsync()
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                StopLiveFaceScan();

                ScanIndicator.IsRunning = false;
                ScanIndicator.IsVisible = false;

                LabelSimilarity.Text = "AKSES DITERIMA - wajah cocok.";
                LabelStatus.Text = "Validasi berhasil: wajah live cocok dengan foto wajah terdaftar.";

                await DisplayAlert(
                    "AKSES DITERIMA",
                    "Wajah live cocok dengan foto wajah terdaftar. Anda akan masuk ke dashboard.",
                    "OK");

                Application.Current!.MainPage = new MainDashboardPage(_role);
            });
        }

        private void OnNextClicked(object sender, EventArgs e)
            => Application.Current!.MainPage = new MainDashboardPage(_role);

        private void OnCancelClicked(object sender, EventArgs e)
        {
            StopLiveFaceScan();
            Application.Current!.MainPage = new MainPage();
        }
    }
}
