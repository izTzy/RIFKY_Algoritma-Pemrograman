using Microsoft.Maui.Media;
using System.IO;

namespace KAMERA
{
    public partial class RegisterPage : ContentPage
    {
        private byte[]? _faceData;
        private string _selectedRole = "Publik"; // default

        public RegisterPage()
        {
            InitializeComponent();
        }

        private void OnRegRolePublikSelected(object sender, EventArgs e)
        {
            _selectedRole = "Publik";

            RegFramePublik.BackgroundColor = Color.FromArgb("#4F46E5");
            RegFramePublik.BorderColor = Colors.Transparent;

            RegFrameAdmin.BackgroundColor = Color.FromArgb("#F1F5F9");
            RegFrameAdmin.BorderColor = Color.FromArgb("#CBD5E1");
        }

        private async void OnRegRoleAdminSelected(object sender, EventArgs e)
        {
            bool isAdminTaken = await AppDatabase.Instance.IsAdminTakenAsync();
            if (isAdminTaken)
            {
                await DisplayAlert("Tidak Tersedia", "Peran Admin sudah didaftarkan oleh pengguna lain.", "OK");
                return;
            }

            _selectedRole = "Admin";

            RegFrameAdmin.BackgroundColor = Color.FromArgb("#4F46E5");
            RegFrameAdmin.BorderColor = Colors.Transparent;

            RegFramePublik.BackgroundColor = Color.FromArgb("#F1F5F9");
            RegFramePublik.BorderColor = Color.FromArgb("#CBD5E1");
        }

        private async void OnCaptureFaceClicked(object sender, EventArgs e)
        {
            try
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo == null) return;

                using var stream = await photo.OpenReadAsync();
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);
                _faceData = memoryStream.ToArray();

                CameraPreview.Source = ImageSource.FromStream(() => new MemoryStream(_faceData));
                CameraPreview.IsVisible = true;
                CameraPlaceholder.IsVisible = false;
                LabelPhotoStatus.IsVisible = true;

                await DisplayAlert("Sukses", "Foto wajah berhasil diambil. Sistem akan membuat template face embedding saat akun disimpan.", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Gagal mengambil foto: {ex.Message}", "OK");
            }
        }

        private async void OnRegisterClicked(object sender, EventArgs e)
        {
            string user = EntryUsername.Text?.Trim() ?? "";
            string pass = EntryPassword.Text ?? "";

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                await DisplayAlert("Gagal", "Username dan password wajib diisi!", "OK");
                return;
            }

            if (_faceData == null)
            {
                await DisplayAlert("Gagal", "Foto wajah wajib diambil untuk membuat template biometrik!", "OK");
                return;
            }

            if (await AppDatabase.Instance.GetUserAsync(user) != null)
            {
                await DisplayAlert(
                    "Username Sudah Ada",
                    $"Username '{user}' sudah terdaftar. Jika hanya ingin mengganti wajah, gunakan tombol 'Update Wajah Akun Ini'.",
                    "OK");
                return;
            }

            if (_selectedRole == "Admin" && await AppDatabase.Instance.IsAdminTakenAsync())
            {
                await DisplayAlert("Gagal", "Peran Admin sudah diambil. Pilih peran Publik.", "OK");
                return;
            }

            bool userCreated = false;
            try
            {
                await AppDatabase.Instance.AddUserAsync(user, pass, _selectedRole);
                userCreated = true;

                string imagePath = Path.Combine(FileSystem.AppDataDirectory, $"{user}_face.png");
                string embeddingPath = FaceEmbeddingService.GetTemplatePath(user);

                File.WriteAllBytes(imagePath, _faceData);
                var template = await FaceEmbeddingService.CreateTemplateAsync(_faceData, user);
                await FaceEmbeddingService.SaveTemplateAsync(template, embeddingPath);

                // Tambahan v13: simpan juga ke datasheet agar 1 user bisa punya banyak sample pembanding.
                var sample = await FaceDataSheetService.SaveNewSampleAsync(user, _selectedRole, _faceData, "register-camera");

                await DisplayAlert(
                    "Pendaftaran Berhasil",
                    $"Akun '{user}' berhasil didaftarkan sebagai {_selectedRole}.\n\n" +
                    $"Lokasi foto utama:\n{imagePath}\n\n" +
                    $"Lokasi template utama:\n{embeddingPath}\n\n" +
                    $"Sample datasheet #{sample.SampleId}:\n{sample.PhotoPath}\n\n" +
                    $"Datasheet:\n{FaceDataSheetService.DataSheetPath}",
                    "OK");

                Application.Current!.MainPage = new MainPage();
            }
            catch (Exception ex)
            {
                if (userCreated)
                    await AppDatabase.Instance.DeleteUserAsync(user);
                await DisplayAlert("Gagal Membuat Face Embedding", ex.Message, "OK");
            }
        }

        private async void OnUpdateExistingFaceClicked(object sender, EventArgs e)
        {
            string user = EntryUsername.Text?.Trim() ?? "";
            string pass = EntryPassword.Text ?? "";

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                await DisplayAlert("Gagal", "Isi username dan password akun yang ingin diperbarui.", "OK");
                return;
            }

            if (_faceData == null)
            {
                await DisplayAlert("Gagal", "Ambil foto wajah baru terlebih dahulu.", "OK");
                return;
            }

            var account = await AppDatabase.Instance.GetUserAsync(user);
            if (account == null)
            {
                await DisplayAlert("Gagal", "Username belum terdaftar. Gunakan tombol Daftar Sekarang.", "OK");
                return;
            }

            if (pass != account.Password)
            {
                await DisplayAlert("Ditolak", "Password salah. Update wajah dibatalkan.", "OK");
                return;
            }

            try
            {
                string imagePath = Path.Combine(FileSystem.AppDataDirectory, $"{user}_face.png");
                string embeddingPath = FaceEmbeddingService.GetTemplatePath(user);

                File.WriteAllBytes(imagePath, _faceData);
                var template = await FaceEmbeddingService.CreateTemplateAsync(_faceData, user);
                await FaceEmbeddingService.SaveTemplateAsync(template, embeddingPath);

                // Tambahan v13: update wajah tidak menghapus sample lama; sample baru ditambahkan ke datasheet.
                var sample = await FaceDataSheetService.SaveNewSampleAsync(user, account.Role, _faceData, "update-camera");

                await DisplayAlert(
                    "Update Wajah Berhasil",
                    $"Data wajah akun '{user}' berhasil diperbarui tanpa membuat akun baru.\n\n" +
                    $"Lokasi foto utama:\n{imagePath}\n\n" +
                    $"Lokasi template utama:\n{embeddingPath}\n\n" +
                    $"Sample datasheet #{sample.SampleId}:\n{sample.PhotoPath}\n\n" +
                    $"Datasheet:\n{FaceDataSheetService.DataSheetPath}",
                    "OK");

                Application.Current!.MainPage = new MainPage();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Gagal Update Wajah", ex.Message, "OK");
            }
        }

        private void OnBackToLoginClicked(object sender, EventArgs e)
            => Application.Current!.MainPage = new MainPage();
    }
}
