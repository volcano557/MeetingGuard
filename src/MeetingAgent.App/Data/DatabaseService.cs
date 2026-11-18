using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MeetingAgent.App.Data;

/// <summary>
/// SQLite 数据服务：用户认证、历史记录、面部模板元数据的持久化存储。
///
/// 密码安全：
///   · 每个用户独立的 16 字节随机盐（base64 存储于 password_salt 列）
///   · PBKDF2-HMAC-SHA256，迭代 100 000 次，输出 32 字节
///   · 兼容旧版 SHA256-pepper 哈希，首次成功登录后自动升级为 PBKDF2
/// </summary>
public class DatabaseService
{
    private readonly string _dbPath;
    private string ConnStr => $"Data Source={_dbPath}";

    private const int PbkdfIterations = 100_000;
    private const int SaltBytes       = 16;
    private const int HashBytes       = 32;
    // 旧版静态 pepper（仅用于兼容老账号，不再用于新注册）
    private const string LegacyPepper = "MeetingGuard_Pepper_2026";

    public DatabaseService()
    {
        _dbPath = Path.Combine(AppContext.BaseDirectory, "meetingguard.db");
        Initialize();
    }

    // ─── 初始化 & 迁移 ────────────────────────────────────────

    private void Initialize()
    {
        using var conn = Open();
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS users (
                id            TEXT PRIMARY KEY,
                username      TEXT UNIQUE NOT NULL,
                password_hash TEXT NOT NULL,
                password_salt TEXT,                        -- 新增：base64 随机盐
                role          TEXT NOT NULL DEFAULT 'user',
                email         TEXT,
                created_at    TEXT NOT NULL,
                last_login    TEXT
            );
            CREATE TABLE IF NOT EXISTS meetings (
                id               TEXT PRIMARY KEY,
                user_id          TEXT NOT NULL,
                name             TEXT,
                started_at       TEXT NOT NULL,
                ended_at         TEXT,
                total_detections INTEGER DEFAULT 0,
                max_risk         TEXT DEFAULT 'Info',
                FOREIGN KEY (user_id) REFERENCES users(id)
            );
            CREATE TABLE IF NOT EXISTS face_templates (
                id          TEXT PRIMARY KEY,
                user_id     TEXT NOT NULL,
                username    TEXT NOT NULL,
                description TEXT,
                photo_count INTEGER DEFAULT 0,
                created_at  TEXT NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users(id)
            );
        ");

        // 已存在的旧库自动迁移：补 password_salt / email 列（若缺）
        MigrateColumn(conn, "users", "password_salt", "TEXT");
        MigrateColumn(conn, "users", "email", "TEXT");

        EnsureDefaultUsers();
    }

    private static void MigrateColumn(SqliteConnection conn, string table, string column, string type)
    {
        var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var r = check.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return; // 已存在
        }
        var add = conn.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        add.ExecuteNonQuery();
    }

    private void EnsureDefaultUsers()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username=@u";

        cmd.Parameters.AddWithValue("@u", "admin");
        if ((long)cmd.ExecuteScalar()! == 0)
            RegisterInternal(conn, Guid.NewGuid().ToString(), "admin", "admin123", "安全审计员", "admin@meetingguard.local");

        cmd.Parameters["@u"].Value = "demo";
        if ((long)cmd.ExecuteScalar()! == 0)
            RegisterInternal(conn, Guid.NewGuid().ToString(), "demo", "demo123", "参会人员", "demo@meetingguard.local");
    }

    // ─── 认证 ────────────────────────────────────────────────

    public (bool Success, string Error, DbUser? User) Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return (false, "用户名不能为空", null);
        if (string.IsNullOrEmpty(password))      return (false, "密码不能为空", null);

        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, username, password_hash, password_salt, role, email, created_at
                            FROM users WHERE username=@u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@u", username.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (false, "用户不存在", null);

        var uid       = r.GetString(0);
        var dbUser    = r.GetString(1);
        var dbHash    = r.GetString(2);
        var dbSaltStr = r.IsDBNull(3) ? "" : r.GetString(3);
        var role      = r.GetString(4);
        var email     = r.IsDBNull(5) ? "" : r.GetString(5);
        var createdAt = DateTime.TryParse(r.GetString(6), out var ca) ? ca : DateTime.Now;
        r.Close();

        bool ok;
        bool needRehash = false;
        if (string.IsNullOrEmpty(dbSaltStr))
        {
            // 旧版：固定 pepper + SHA256，验证后即升级
            ok = LegacyHash(password) == dbHash;
            needRehash = ok;
        }
        else
        {
            ok = VerifyPbkdf2(password, dbSaltStr, dbHash);
        }
        if (!ok) return (false, "密码错误", null);

        if (needRehash)
        {
            var (newSalt, newHash) = HashPasswordWithSalt(password);
            var up = conn.CreateCommand();
            up.CommandText = "UPDATE users SET password_hash=@h, password_salt=@s WHERE id=@id";
            up.Parameters.AddWithValue("@h", newHash);
            up.Parameters.AddWithValue("@s", newSalt);
            up.Parameters.AddWithValue("@id", uid);
            up.ExecuteNonQuery();
        }

        // 更新最近登录
        var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE users SET last_login=@t WHERE id=@id";
        upd.Parameters.AddWithValue("@t", DateTime.Now.ToString("O"));
        upd.Parameters.AddWithValue("@id", uid);
        upd.ExecuteNonQuery();

        return (true, "", new DbUser(uid, dbUser, role, email, createdAt));
    }

    public (bool Success, string Error) Register(string username, string password, string role = "参会人员", string email = "")
    {
        if (string.IsNullOrWhiteSpace(username)) return (false, "用户名不能为空");
        if (username.Trim().Length < 2)          return (false, "用户名至少 2 个字符");
        if (username.Trim().Length > 32)         return (false, "用户名不超过 32 个字符");
        if (string.IsNullOrEmpty(password))      return (false, "密码不能为空");
        if (password.Length < 6)                 return (false, "密码至少 6 位");
        if (password.Length > 64)                return (false, "密码不超过 64 位");

        try
        {
            using var conn = Open();
            RegisterInternal(conn, Guid.NewGuid().ToString(), username.Trim(), password, role, email.Trim());
            return (true, "");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return (false, "用户名已存在");
        }
    }

    private void RegisterInternal(SqliteConnection conn, string id, string username, string password, string role, string email = "")
    {
        var (salt, hash) = HashPasswordWithSalt(password);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO users(id, username, password_hash, password_salt, role, email, created_at)
                            VALUES(@id, @u, @ph, @ps, @r, @e, @ca)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@u",  username);
        cmd.Parameters.AddWithValue("@ph", hash);
        cmd.Parameters.AddWithValue("@ps", salt);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@e",  email);
        cmd.Parameters.AddWithValue("@ca", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // ─── 面部模板 ────────────────────────────────────────────

    public List<DbFaceTemplate> GetFaceTemplates(string userId)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,username,description,photo_count,created_at FROM face_templates WHERE user_id=@uid ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@uid", userId);
        using var r = cmd.ExecuteReader();
        var list = new List<DbFaceTemplate>();
        while (r.Read())
            list.Add(new DbFaceTemplate(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.GetInt32(3),
                DateTime.TryParse(r.GetString(4), out var d) ? d : DateTime.Now));
        return list;
    }

    public void SaveFaceTemplate(string userId, string username, string description, int photoCount)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO face_templates(id,user_id,username,description,photo_count,created_at)
            VALUES(@id,@uid,@un,@desc,@pc,@ca)
            ON CONFLICT(id) DO UPDATE SET description=@desc,photo_count=@pc,created_at=@ca";
        cmd.Parameters.AddWithValue("@id", $"{userId}_face_{DateTime.Now:yyyyMMdd}");
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@un", username);
        cmd.Parameters.AddWithValue("@desc", description);
        cmd.Parameters.AddWithValue("@pc", photoCount);
        cmd.Parameters.AddWithValue("@ca", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // ─── 会议记录 ────────────────────────────────────────────

    public List<DbMeeting> GetUserMeetings(string userId, int limit = 10)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id,name,started_at,total_detections,max_risk
                            FROM meetings WHERE user_id=@uid
                            ORDER BY started_at DESC LIMIT @lim";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<DbMeeting>();
        while (r.Read())
            list.Add(new DbMeeting(r.GetString(0), r.IsDBNull(1) ? "未命名" : r.GetString(1),
                r.GetString(2), r.GetInt32(3), r.GetString(4)));
        return list;
    }

    public void SaveMeeting(string meetingId, string userId, string name)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO meetings(id,user_id,name,started_at) VALUES(@id,@uid,@n,@t)";
        cmd.Parameters.AddWithValue("@id", meetingId);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public int GetUserMeetingCount(string userId)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM meetings WHERE user_id=@uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public int GetUserFaceTemplateCount(string userId)
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM face_templates WHERE user_id=@uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // ─── 内部工具 ────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnStr);
        c.Open();
        return c;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>新版：PBKDF2-HMAC-SHA256，逐用户独立 16 字节随机盐。</summary>
    private static (string SaltB64, string HashB64) HashPasswordWithSalt(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password: password,
            salt:     salt,
            iterations: PbkdfIterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: HashBytes);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static bool VerifyPbkdf2(string password, string saltB64, string expectedHashB64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltB64);
            var calc = Rfc2898DeriveBytes.Pbkdf2(
                password: password,
                salt:     salt,
                iterations: PbkdfIterations,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: HashBytes);
            var expected = Convert.FromBase64String(expectedHashB64);
            return CryptographicOperations.FixedTimeEquals(calc, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>旧版兼容：仅用于校验老账号一次，验证后立刻升级到 PBKDF2。</summary>
    private static string LegacyHash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password + LegacyPepper));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

// ─── DTO ────────────────────────────────────────────────────
public record DbUser(string Id, string Username, string Role, string Email, DateTime CreatedAt);
public record DbFaceTemplate(string Id, string Username, string Description, int PhotoCount, DateTime CreatedAt);
public record DbMeeting(string Id, string Name, string StartedAt, int TotalDetections, string MaxRisk);
