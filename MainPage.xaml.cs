using KAMERA;
using Microsoft.Maui.Controls;
using System;

namespace KAMERA
{
    public partial class MainPage : ContentPage
    {
        private string _selectedRole = "Publik"; // default

        public MainPage()
        {
            InitializeComponent();
        }

        // ── Role Selection Handlers ──────────────────────────────────────────
        private void OnRolePublikSelected(object sender, EventArgs e)
        {
            _selectedRole = "Publik";

            // Highlight Rakyat
            FramePublik.BackgroundColor = Color.FromArgb("#0EA5E9");
            FramePublik.BorderColor    = Colors.Transparent;

            // Dim Raja
            FrameAdmin.BackgroundColor = Color.FromArgb("#334155");
            FrameAdmin.BorderColor     = Color.FromArgb("#475569");

            // Update label colors inside Raja frame
            UpdateRoleLabels(publikActive: true);
        }

        private void OnRoleAdminSelected(object sender, EventArgs e)
        {
            _selectedRole = "Admin";

            // Dim Rakyat
            FramePublik.BackgroundColor = Color.FromArgb("#334155");
            FramePublik.BorderColor     = Color.FromArgb("#475569");

            // Highlight Raja
            FrameAdmin.BackgroundColor = Color.FromArgb("#7C3AED");
            FrameAdmin.BorderColor     = Colors.Transparent;

            UpdateRoleLabels(publikActive: false);
        }

        private void UpdateRoleLabels(bool publikActive)
        {
            // Labels inside the frames auto-inherit color from Frame background style.
            // No extra work needed for the text; the frame colour change is the visual cue.
        }

        // ── Login Handler ────────────────────────────────────────────────────
        private async void OnLoginClicked(object sender, EventArgs e)
        {
            string user = EntryUsername.Text?.Trim() ?? "";
            string pass = EntryPassword.Text ?? "";

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                await DisplayAlert("Perhatian", "Username dan password tidak boleh kosong!", "OK");
                return;
            }

            var account = await AppDatabase.Instance.GetUserAsync(user);
            if (account == null)
            {
                await DisplayAlert("Ditolak", "Username tidak ditemukan!", "OK");
                return;
            }

            if (pass != account.Password)
            {
                await DisplayAlert("Ditolak", "Password salah!", "OK");
                return;
            }

            // Validate that the stored role matches the selected role
            string storedRole = account.Role;
            if (storedRole != _selectedRole)
            {
                await DisplayAlert("Ditolak", $"Akun ini terdaftar sebagai '{storedRole}', bukan '{_selectedRole}'.", "OK");
                return;
            }

            // Save session
            Preferences.Set("session_username", user);
            Preferences.Set("session_role", storedRole);

            // Proceed to face verification (same flow as before)
            Application.Current!.MainPage = new FaceVerificationPage(user, storedRole);
        }

        private void OnGoToRegisterClicked(object sender, EventArgs e)
            => Application.Current!.MainPage = new RegisterPage();
    }
}
