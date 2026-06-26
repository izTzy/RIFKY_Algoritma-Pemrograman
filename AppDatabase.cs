using SQLite;
using SQLitePCL;

namespace KAMERA
{
    [Table("users")]
    public sealed class UserAccountEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed(Unique = true), NotNull]
        public string Username { get; set; } = "";

        [NotNull]
        public string Password { get; set; } = "";

        [NotNull]
        public string Role { get; set; } = "Publik";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    [Table("face_samples")]
    public sealed class FaceSampleEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int UserId { get; set; }

        [Indexed, NotNull]
        public string Username { get; set; } = "";

        [NotNull]
        public string Role { get; set; } = "Publik";

        public int SampleId { get; set; }
        public string PhotoPath { get; set; } = "";
        public string EmbeddingPath { get; set; } = "";
        public double QualityScore { get; set; }
        public string Source { get; set; } = "camera";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    [Table("screening_history")]
    public sealed class ScreeningHistoryEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public string Username { get; set; } = "";

        public int Score { get; set; }
        public string LevelText { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Method { get; set; } = "";
        public string Detail { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class AppDatabase
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        public static AppDatabase Instance { get; } = new();

        public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "kamera.db3");

        private AppDatabase()
        {
            Batteries_V2.Init();
            _db = new SQLiteAsyncConnection(DatabasePath);
        }

        public async Task InitializeAsync()
        {
            if (_initialized)
                return;

            await _initLock.WaitAsync();
            try
            {
                if (_initialized)
                    return;

                await _db.CreateTableAsync<UserAccountEntity>();
                await _db.CreateTableAsync<FaceSampleEntity>();
                await _db.CreateTableAsync<ScreeningHistoryEntity>();
                await MigrateLegacyPreferencesAsync();
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<UserAccountEntity?> GetUserAsync(string username)
        {
            await InitializeAsync();
            return await _db.FindWithQueryAsync<UserAccountEntity>(
                "SELECT * FROM users WHERE lower(Username) = lower(?) LIMIT 1",
                username);
        }

        public async Task<IReadOnlyList<UserAccountEntity>> GetUsersAsync()
        {
            await InitializeAsync();
            return await _db.QueryAsync<UserAccountEntity>("SELECT * FROM users ORDER BY Username COLLATE NOCASE");
        }

        public async Task<bool> IsAdminTakenAsync()
        {
            await InitializeAsync();
            int count = await _db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE lower(Role) = 'admin'");
            return count > 0;
        }

        public async Task<int> AddUserAsync(string username, string password, string role)
        {
            await InitializeAsync();
            var existing = await GetUserAsync(username);
            if (existing != null)
                throw new InvalidOperationException($"Username '{username}' sudah terdaftar.");

            var user = new UserAccountEntity
            {
                Username = username,
                Password = password,
                Role = role,
                CreatedAt = DateTime.Now
            };

            await _db.InsertAsync(user);
            SyncLegacyUserPreference(user);
            return user.Id;
        }

        public async Task UpdateUserPasswordRoleAsync(string username, string password, string role)
        {
            await InitializeAsync();
            var user = await GetUserAsync(username);
            if (user == null)
                return;

            user.Password = password;
            user.Role = role;
            await _db.UpdateAsync(user);
            SyncLegacyUserPreference(user);
        }

        public async Task DeleteUserAsync(string username)
        {
            await InitializeAsync();
            var user = await GetUserAsync(username);
            if (user == null)
                return;

            await _db.ExecuteAsync("DELETE FROM face_samples WHERE UserId = ?", user.Id);
            await _db.ExecuteAsync("DELETE FROM screening_history WHERE lower(Username) = lower(?)", username);
            await _db.DeleteAsync(user);
            RemoveLegacyUserPreference(username);
        }

        public async Task<FaceSampleEntity> AddFaceSampleAsync(
            string username,
            string role,
            int sampleId,
            string photoPath,
            string embeddingPath,
            double qualityScore,
            string source,
            DateTime createdAt)
        {
            await InitializeAsync();
            var user = await GetUserAsync(username);
            int userId = user?.Id ?? await AddUserAsync(username, Preferences.Get($"user_pass_{username}", ""), role);

            var sample = new FaceSampleEntity
            {
                UserId = userId,
                Username = username,
                Role = role,
                SampleId = sampleId,
                PhotoPath = photoPath,
                EmbeddingPath = embeddingPath,
                QualityScore = qualityScore,
                Source = source,
                CreatedAt = createdAt
            };

            await _db.InsertAsync(sample);
            return sample;
        }

        public async Task<IReadOnlyList<FaceSampleEntity>> GetFaceSamplesForUserAsync(string username)
        {
            await InitializeAsync();
            return await _db.QueryAsync<FaceSampleEntity>(
                "SELECT * FROM face_samples WHERE lower(Username) = lower(?) ORDER BY CreatedAt DESC",
                username);
        }

        public async Task<IReadOnlyList<FaceSampleEntity>> GetOtherFaceSamplesAsync(string username)
        {
            await InitializeAsync();
            return await _db.QueryAsync<FaceSampleEntity>(
                "SELECT * FROM face_samples WHERE lower(Username) <> lower(?) ORDER BY CreatedAt DESC",
                username);
        }

        public async Task AddScreeningHistoryAsync(string username, int score, string levelText, string mode, string method, string detail)
        {
            await InitializeAsync();
            await _db.InsertAsync(new ScreeningHistoryEntity
            {
                Username = username,
                Score = score,
                LevelText = levelText,
                Mode = mode,
                Method = method,
                Detail = detail,
                CreatedAt = DateTime.Now
            });
        }

        public async Task<IReadOnlyList<ScreeningHistoryEntity>> GetScreeningHistoryAsync()
        {
            await InitializeAsync();
            return await _db.QueryAsync<ScreeningHistoryEntity>("SELECT * FROM screening_history ORDER BY CreatedAt DESC, Id DESC");
        }

        public async Task DeleteScreeningHistoryAsync(int id)
        {
            await InitializeAsync();
            await _db.ExecuteAsync("DELETE FROM screening_history WHERE Id = ?", id);
        }

        public async Task ClearScreeningHistoryAsync()
        {
            await InitializeAsync();
            await _db.ExecuteAsync("DELETE FROM screening_history");
            Preferences.Remove("stroke_history");
        }

        private async Task MigrateLegacyPreferencesAsync()
        {
            string rawUsers = Preferences.Get("daftar_semua_user", "");
            var users = rawUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

            foreach (string username in users)
            {
                if (await GetUserNoInitAsync(username) != null)
                    continue;

                var user = new UserAccountEntity
                {
                    Username = username,
                    Password = Preferences.Get($"user_pass_{username}", ""),
                    Role = Preferences.Get($"user_role_{username}", "Publik"),
                    CreatedAt = DateTime.Now
                };

                await _db.InsertAsync(user);
            }

            string rawHistory = Preferences.Get("stroke_history", "");
            if (!string.IsNullOrWhiteSpace(rawHistory))
            {
                int existingHistory = await _db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM screening_history");
                if (existingHistory == 0)
                {
                    foreach (var row in rawHistory.Split("||", StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = row.Split('|');
                        if (parts.Length < 4)
                            continue;

                        int.TryParse(parts[1], out int score);
                        string mode = parts.Length >= 5 ? parts[3] : "Teks Bebas";
                        string detail = parts.Length >= 5 ? parts[4] : parts[3];

                        await _db.InsertAsync(new ScreeningHistoryEntity
                        {
                            Username = parts[0],
                            Score = score,
                            LevelText = parts[2],
                            Mode = mode,
                            Method = mode,
                            Detail = detail,
                            CreatedAt = DateTime.Now
                        });
                    }
                }
            }
        }

        private async Task<UserAccountEntity?> GetUserNoInitAsync(string username)
            => await _db.FindWithQueryAsync<UserAccountEntity>(
                "SELECT * FROM users WHERE lower(Username) = lower(?) LIMIT 1",
                username);

        private static void SyncLegacyUserPreference(UserAccountEntity user)
        {
            Preferences.Set($"user_pass_{user.Username}", user.Password);
            Preferences.Set($"user_role_{user.Username}", user.Role);

            string rawUsers = Preferences.Get("daftar_semua_user", "");
            var users = rawUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Append(user.Username)
                                .Distinct(StringComparer.OrdinalIgnoreCase);
            Preferences.Set("daftar_semua_user", string.Join(",", users));

            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                Preferences.Set("has_admin", true);
        }

        private static void RemoveLegacyUserPreference(string username)
        {
            Preferences.Remove($"user_pass_{username}");
            Preferences.Remove($"user_role_{username}");

            string rawUsers = Preferences.Get("daftar_semua_user", "");
            var users = rawUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Where(u => !string.Equals(u, username, StringComparison.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase);
            Preferences.Set("daftar_semua_user", string.Join(",", users));
        }
    }
}
