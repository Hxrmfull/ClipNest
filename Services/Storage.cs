using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClipNest.Models;
using Microsoft.Data.Sqlite;

namespace ClipNest.Services;

public enum ClipFilter { All, Text, Links, Images, Files, Pinned }

/// <summary>SQLite history. UI thread only.
/// `items` is kept slim (preview, metadata, search key last) so listing never reads large payloads;
/// full text, HTML, RTF and file lists live in `bodies` and are loaded only when an item is pasted.
/// Images are PNG files next to the database (DPAPI-encrypted .enc for secret ones).</summary>
public sealed class Storage : IDisposable
{
    public const int PreviewChars = 3000;
    private const int SearchChars = 100_000;
    // regions only for secret images: a CASE keeps SQLite from reading that column (added at the end of the row,
    // after the possibly long search key) for every other row
    private const string ItemColumns = "id,kind,preview,rich,image,width,height,hash,source,created,stamp,size,pinned,secret,"
                                       + "CASE WHEN secret=1 AND kind=2 THEN regions END";

    private SqliteConnection _db = null!;
    private long _count = -1;
    public string DataDir { get; }
    public string ImagesDir { get; }
    public string DbPath { get; }

    public Storage(string dataDir)
    {
        DataDir = dataDir;
        ImagesDir = Path.Combine(dataDir, "images");
        Directory.CreateDirectory(ImagesDir);
        DbPath = Path.Combine(dataDir, "history.db");

        OpenEncrypted();
        // secure_delete: freed pages are zeroed, so a deleted or re-encrypted password doesn't linger in the file
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA secure_delete=ON; PRAGMA cache_size=-16000; PRAGMA temp_store=MEMORY;");

        if (TableHasColumn("items", "html")) MigrateFromV1();
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                kind     INTEGER NOT NULL,
                preview  TEXT,
                rich     INTEGER NOT NULL DEFAULT 0,
                image    TEXT,
                width    INTEGER NOT NULL DEFAULT 0,
                height   INTEGER NOT NULL DEFAULT 0,
                hash     TEXT NOT NULL UNIQUE,
                source   TEXT,
                created  INTEGER NOT NULL,
                stamp    INTEGER NOT NULL,
                size     INTEGER NOT NULL DEFAULT 0,
                pinned   INTEGER NOT NULL DEFAULT 0,
                secret   INTEGER NOT NULL DEFAULT 0,
                regions  TEXT,
                search   TEXT
            );
            CREATE TABLE IF NOT EXISTS bodies(
                id    INTEGER PRIMARY KEY,
                text  TEXT,
                html  TEXT,
                rtf   TEXT,
                files TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_items_stamp ON items(pinned DESC, stamp DESC);
            CREATE INDEX IF NOT EXISTS ix_items_kind ON items(kind, pinned DESC, stamp DESC);
            """);
        if (!TableHasColumn("items", "regions")) Exec("ALTER TABLE items ADD COLUMN regions TEXT");
        EnsureSearchIndex();
        if (UserVersion() < 3)
        {
            Exec("UPDATE items SET search = replace(search, 'ё', 'е') WHERE instr(search, 'ё') > 0; PRAGMA user_version = 3;");
        }
        if (UserVersion() < 4)
        {
            // the words added for English and Chinese (see SecretWords, ImageWords)
            Exec("UPDATE items SET search = search || ' hidden password 秘密 机密 密码 隐藏' WHERE secret = 1;"
                + "UPDATE items SET search = search || ' picture 图片 图像' WHERE kind = 2 AND secret = 0; PRAGMA user_version = 4;");
        }
        EncryptPlainImages();
    }

    // ---------- encryption at rest ----------

    private const string PlainHeader = "SQLite format 3\0";

    /// <summary>A plaintext SQLite file starts with a fixed header; an SQLCipher one is random from byte 0.</summary>
    private static bool IsPlainSqlite(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 16) return false;
        var head = new byte[16];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.ReadExactly(head);
        return Encoding.ASCII.GetString(head) == PlainHeader;
    }

    /// <summary>Opens history.db with its SQLCipher key. A database from before encryption is encrypted first;
    /// one that can't be opened with the key (the key is gone, e.g. Windows password reset by an admin) is set aside,
    /// not deleted, and a new history starts.</summary>
    private void OpenEncrypted()
    {
        bool plain = IsPlainSqlite(DbPath);
        if (plain)
        {
            try { EncryptExisting(); plain = false; }
            catch (Exception ex) { Log.Write("encrypting history.db failed, staying unencrypted for now: " + ex.Message); }
        }

        _db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        _db.Open();
        if (plain) return; // could not encrypt: keep working, try again next start

        Exec($"PRAGMA key = \"{Secrets.DatabaseKeyPragma}\";");
        try
        {
            Exec("SELECT count(*) FROM sqlite_master;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // "file is not a database": wrong key
        {
            _db.Dispose();
            var aside = DbPath + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var ext in new[] { "", "-wal", "-shm" })
                if (File.Exists(DbPath + ext)) File.Move(DbPath + ext, aside + ext);
            Log.Write("history.db can't be opened with this account's key; set aside as " + Path.GetFileName(aside));
            _db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            _db.Open();
            Exec($"PRAGMA key = \"{Secrets.DatabaseKeyPragma}\";");
        }
    }

    /// <summary>Copies a plaintext database into an encrypted one (sqlcipher_export), then wipes and replaces the original.
    /// The full-text index is dropped first and rebuilt on the encrypted copy.</summary>
    private void EncryptExisting()
    {
        var tmp = DbPath + ".encrypting";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                PRAGMA wal_checkpoint(TRUNCATE);
                DROP TRIGGER IF EXISTS items_fts_ai; DROP TRIGGER IF EXISTS items_fts_ad; DROP TRIGGER IF EXISTS items_fts_au;
                DROP TABLE IF EXISTS search_fts;
                ATTACH DATABASE '{tmp.Replace("'", "''")}' AS enc KEY "{Secrets.DatabaseKeyPragma}";
                SELECT sqlcipher_export('enc');
                DETACH DATABASE enc;
                PRAGMA journal_mode=DELETE;
                """;
            cmd.ExecuteNonQuery();
        }
        // swap with a fallback: the plaintext original is only wiped once the encrypted copy is in its place
        var old = DbPath + ".plain-old";
        foreach (var ext in new[] { "", "-wal", "-shm" })
            if (File.Exists(DbPath + ext)) File.Move(DbPath + ext, old + ext, true);
        try
        {
            File.Move(tmp, DbPath);
        }
        catch
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
                if (File.Exists(old + ext)) File.Move(old + ext, DbPath + ext, true);
            throw;
        }
        foreach (var ext in new[] { "", "-wal", "-shm" }) Wipe(old + ext);
        Log.Write("history.db encrypted (SQLCipher)");
    }

    /// <summary>Overwrites a file with zeros before deleting it (best effort: an SSD may still keep old copies
    /// of the blocks; only full-disk encryption covers that).</summary>
    internal static void Wipe(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[1 << 16];
                for (long left = fs.Length; left > 0; left -= zeros.Length)
                    fs.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
                fs.Flush(true);
            }
        }
        catch { }
        File.Delete(path);
    }

    /// <summary>Images from before every image was encrypted: encrypt them in place, once.</summary>
    private void EncryptPlainImages()
    {
        var rows = new List<(long id, string file)>();
        using (var sel = _db.CreateCommand())
        {
            sel.CommandText = "SELECT id, image FROM items WHERE image LIKE '%.png'";
            using var r = sel.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        if (rows.Count == 0) return;
        using var tx = _db.BeginTransaction();
        foreach (var (id, file) in rows)
        {
            var src = Path.Combine(ImagesDir, file);
            var encName = Path.ChangeExtension(file, Secrets.EncryptedImageExt);
            try
            {
                if (File.Exists(src))
                {
                    File.WriteAllBytes(Path.Combine(ImagesDir, encName), Secrets.Protect(File.ReadAllBytes(src)));
                    Wipe(src);
                }
                using var up = _db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = "UPDATE items SET image=$f WHERE id=$id";
                up.Parameters.AddWithValue("$f", encName);
                up.Parameters.AddWithValue("$id", id);
                up.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Write("encrypting image " + file + ": " + ex.Message); }
        }
        tx.Commit();
        Log.Write($"encrypted {rows.Count} image(s)");
    }

    /// <summary>Trigram full-text index over items.search: substring search without scanning every row.
    /// Kept in sync by triggers; built once for databases that predate it.</summary>
    private void EnsureSearchIndex()
    {
        using (var check = _db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE name='search_fts'";
            if (check.ExecuteScalar() is not null) return;
        }
        using var tx = _db.BeginTransaction();
        Exec("""
            CREATE VIRTUAL TABLE search_fts USING fts5(search, content='items', content_rowid='id', tokenize='trigram case_sensitive 1');
            CREATE TRIGGER items_fts_ai AFTER INSERT ON items BEGIN
                INSERT INTO search_fts(rowid, search) VALUES (new.id, new.search);
            END;
            CREATE TRIGGER items_fts_ad AFTER DELETE ON items BEGIN
                INSERT INTO search_fts(search_fts, rowid, search) VALUES ('delete', old.id, old.search);
            END;
            CREATE TRIGGER items_fts_au AFTER UPDATE OF search ON items BEGIN
                INSERT INTO search_fts(search_fts, rowid, search) VALUES ('delete', old.id, old.search);
                INSERT INTO search_fts(rowid, search) VALUES (new.id, new.search);
            END;
            INSERT INTO search_fts(search_fts) VALUES ('rebuild');
            """, tx);
        tx.Commit();
    }

    /// <summary>v1 kept text/html/rtf inline in `items`; split them out once.</summary>
    private void MigrateFromV1()
    {
        if (!TableHasColumn("items", "secret")) Exec("ALTER TABLE items ADD COLUMN secret INTEGER NOT NULL DEFAULT 0");
        using var tx = _db.BeginTransaction();
        Exec($"""
            CREATE TABLE bodies(id INTEGER PRIMARY KEY, text TEXT, html TEXT, rtf TEXT, files TEXT);
            INSERT INTO bodies(id,text,html,rtf,files) SELECT id,text,html,rtf,files FROM items;
            CREATE TABLE items_v2(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, preview TEXT, rich INTEGER NOT NULL DEFAULT 0,
                image TEXT, width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, hash TEXT NOT NULL UNIQUE,
                source TEXT, created INTEGER NOT NULL, stamp INTEGER NOT NULL, size INTEGER NOT NULL DEFAULT 0,
                pinned INTEGER NOT NULL DEFAULT 0, secret INTEGER NOT NULL DEFAULT 0, search TEXT);
            INSERT INTO items_v2 SELECT id, kind,
                CASE WHEN secret=1 THEN NULL WHEN kind=3 THEN substr(files,1,{PreviewChars}) ELSE substr(text,1,{PreviewChars}) END,
                (html IS NOT NULL OR rtf IS NOT NULL), image, width, height, hash, source, created, stamp,
                CASE WHEN kind=3 THEN (length(files) - length(replace(files, char(10), '')) + 1) ELSE size END,
                pinned, secret, substr(search,1,{SearchChars}) FROM items;
            DROP TABLE items;
            ALTER TABLE items_v2 RENAME TO items;
            """, tx);
        tx.Commit();
        Exec("VACUUM;");
        Log.Write("migrated history.db to v2");
    }

    private long UserVersion()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return (long)cmd.ExecuteScalar()!;
    }

    private bool TableHasColumn(string table, string name)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ToUnix(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static DateTime FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    // a secret or a picture is found by these words in any of the interface languages
    private const string SecretWords = "секрет скрыто пароль secret hidden password 秘密 机密 密码 隐藏";
    private const string ImageWords = "изображение картинка image picture 图片 图像";

    private static string SearchKey(ClipItem i)
    {
        // secrets are findable only by source and kind, never by content
        if (i.Secret) return Normalize($"{i.Source}\n{SecretWords}");

        var sb = new StringBuilder();
        // huge texts are stored whole, but only their beginning is searchable
        var text = i.Text is { Length: > SearchChars } t ? t[..SearchChars] : i.Text;
        sb.Append(text).Append('\n').Append(i.Files).Append('\n').Append(i.Source);
        if (i.Kind == ClipKind.Image) sb.Append('\n').Append(ImageWords).Append('\n').Append(i.OcrText); // text read off the picture
        return Normalize(sb.ToString());
    }

    /// <summary>Search keys and queries alike: lower case, and «ё» as «е» (people type «елка» for «ёлка»).</summary>
    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Length in characters as the trigram index counts them: "😀" is one, though two UTF-16 units.</summary>
    private static int Chars(string s) => s.EnumerateRunes().Count();

    private static string? PreviewOf(ClipItem i)
    {
        if (i.Secret) return null;
        var s = i.Kind == ClipKind.Files ? i.Files : i.Text;
        return s is { Length: > PreviewChars } ? s[..PreviewChars] : s;
    }

    private static object Db(object? v) => v ?? DBNull.Value;

    /// <summary>Inserts the item, or bumps the existing one with the same content to the top.
    /// Returns true if a new row was created. The item must carry its full content.</summary>
    public bool Add(ClipItem item)
    {
        var now = DateTime.Now;
        using var tx = _db.BeginTransaction();
        using (var up = _db.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE items SET stamp=$s, source=COALESCE($src, source), rich=MAX(rich, $rich) WHERE hash=$h RETURNING id";
            up.Parameters.AddWithValue("$s", ToUnix(now));
            up.Parameters.AddWithValue("$src", Db(item.Source));
            up.Parameters.AddWithValue("$rich", !item.Secret && item.HasRichFormat ? 1 : 0);
            up.Parameters.AddWithValue("$h", item.Hash);
            if (up.ExecuteScalar() is long existing)
            {
                item.Id = existing;
                // keep formatting from this copy if the stored one had none
                if (!item.Secret && item.HasRichFormat)
                {
                    using var b = _db.CreateCommand();
                    b.Transaction = tx;
                    b.CommandText = "UPDATE bodies SET html=COALESCE(html, $html), rtf=COALESCE(rtf, $rtf) WHERE id=$id";
                    b.Parameters.AddWithValue("$html", Db(item.Html));
                    b.Parameters.AddWithValue("$rtf", Db(item.Rtf));
                    b.Parameters.AddWithValue("$id", existing);
                    b.ExecuteNonQuery();
                }
                tx.Commit();
                return false;
            }
        }

        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO items(kind,preview,rich,image,width,height,hash,source,created,stamp,size,pinned,secret,regions,search)
                VALUES($k,$p,$rich,$img,$w,$hgt,$h,$src,$c,$s,$size,0,$sec,$reg,$q) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$k", (int)item.Kind);
            cmd.Parameters.AddWithValue("$p", Db(PreviewOf(item)));
            cmd.Parameters.AddWithValue("$rich", !item.Secret && item.HasRichFormat ? 1 : 0);
            cmd.Parameters.AddWithValue("$img", Db(item.ImagePath is null ? null : Path.GetFileName(item.ImagePath)));
            cmd.Parameters.AddWithValue("$w", item.Width);
            cmd.Parameters.AddWithValue("$hgt", item.Height);
            cmd.Parameters.AddWithValue("$h", item.Hash);
            cmd.Parameters.AddWithValue("$src", Db(item.Source));
            cmd.Parameters.AddWithValue("$c", ToUnix(now));
            cmd.Parameters.AddWithValue("$s", ToUnix(now));
            cmd.Parameters.AddWithValue("$size", item.Size);
            cmd.Parameters.AddWithValue("$sec", item.Secret ? 1 : 0);
            cmd.Parameters.AddWithValue("$reg", Db(item.Secret ? item.Regions : null));
            cmd.Parameters.AddWithValue("$q", SearchKey(item));
            item.Id = (long)cmd.ExecuteScalar()!;
        }

        if (item.Text is not null || item.Files is not null)
        {
            using var b = _db.CreateCommand();
            b.Transaction = tx;
            b.CommandText = "INSERT INTO bodies(id,text,html,rtf,files) VALUES($id,$t,$html,$rtf,$f)";
            b.Parameters.AddWithValue("$id", item.Id);
            b.Parameters.AddWithValue("$t", Db(item.Secret && item.Text is not null ? Secrets.EncryptText(item.Text) : item.Text));
            b.Parameters.AddWithValue("$html", Db(item.Secret ? null : item.Html));
            b.Parameters.AddWithValue("$rtf", Db(item.Secret ? null : item.Rtf));
            b.Parameters.AddWithValue("$f", Db(item.Files));
            b.ExecuteNonQuery();
        }
        tx.Commit();

        item.PreviewText = PreviewOf(item);
        item.Created = item.Stamp = now;
        item.IsFull = true;
        if (_count >= 0) _count++;
        return true;
    }

    public bool HashExists(string hash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM items WHERE hash=$h";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is not null;
    }

    public List<ClipItem> Query(string? search, ClipFilter filter, int offset, int limit)
    {
        using var cmd = _db.CreateCommand();
        var where = new List<string>();
        switch (filter)
        {
            case ClipFilter.Text: where.Add("kind=0"); break;
            case ClipFilter.Links: where.Add("kind=1"); break;
            case ClipFilter.Images: where.Add("kind=2"); break;
            case ClipFilter.Files: where.Add("kind=3"); break;
            case ClipFilter.Pinned: where.Add("pinned=1"); break;
        }
        var terms = Normalize(search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // any whitespace
        // 3+ characters go through the trigram index; shorter ones can't, and are cheap to scan for anyway
        var indexed = terms.Where(t => Chars(t) >= 3).Select(t => "\"" + t.Replace("\"", "\"\"") + "\"").ToList();
        var ftsQuery = string.Join(" AND ", indexed);
        // the index wins for rare words; for common ones a plain scan in display order reaches the first page sooner
        bool useIndex = indexed.Count > 0 && CountMatches(ftsQuery, FtsCommonThreshold + 1) <= FtsCommonThreshold;
        if (useIndex)
        {
            where.Add("id IN (SELECT rowid FROM search_fts WHERE search_fts MATCH $fts)");
            cmd.Parameters.AddWithValue("$fts", ftsQuery);
        }
        var shortTerms = useIndex ? terms.Where(t => Chars(t) < 3).ToList() : terms.ToList();
        for (int i = 0; i < shortTerms.Count; i++)
        {
            where.Add($"instr(search, $q{i}) > 0");
            cmd.Parameters.AddWithValue($"$q{i}", shortTerms[i]);
        }
        // only 1–2 characters: the index can't help, and scanning every (encrypted) page for a rare pair took ~150 ms
        // on 20 000 items. Such a short query looks through the most recent items; from 3 characters, everything.
        // A time boundary rather than an id list: a common pair still stops at the first page of matches.
        if (terms.Length > 0 && terms.All(t => Chars(t) < 3) && ShortQueryCutoff() is long cut)
        {
            where.Add("(pinned = 1 OR stamp >= $cut)");
            cmd.Parameters.AddWithValue("$cut", cut);
        }
        cmd.CommandText = $"SELECT {ItemColumns} FROM items"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY pinned DESC, stamp DESC LIMIT $lim OFFSET $off";
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);

        var list = new List<ClipItem>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var kind = (ClipKind)r.GetInt32(1);
            list.Add(new ClipItem
            {
                Id = r.GetInt64(0),
                Kind = kind,
                PreviewText = r.IsDBNull(2) ? null : r.GetString(2),
                RichFormat = r.GetInt64(3) != 0,
                ImagePath = r.IsDBNull(4) ? null : Path.Combine(ImagesDir, r.GetString(4)),
                Width = r.GetInt32(5),
                Height = r.GetInt32(6),
                Hash = r.GetString(7),
                Source = r.IsDBNull(8) ? null : r.GetString(8),
                Created = FromUnix(r.GetInt64(9)),
                Stamp = FromUnix(r.GetInt64(10)),
                Size = r.GetInt64(11),
                Pinned = r.GetInt64(12) != 0,
                Secret = r.GetInt64(13) != 0,
                Regions = r.IsDBNull(14) ? null : r.GetString(14),
                IsFull = kind == ClipKind.Image,
            });
        }
        return list;
    }

    private const int FtsCommonThreshold = 300;
    private const int ShortQueryWindow = 4000;

    /// <summary>The stamp of the 4000th most recent unpinned item, or null when there are fewer.</summary>
    private long? ShortQueryCutoff()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT stamp FROM items WHERE pinned=0 ORDER BY stamp DESC LIMIT 1 OFFSET {ShortQueryWindow - 1}";
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    private long CountMatches(string ftsQuery, int cap)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM (SELECT 1 FROM search_fts WHERE search_fts MATCH $fts LIMIT $cap)";
        cmd.Parameters.AddWithValue("$fts", ftsQuery);
        cmd.Parameters.AddWithValue("$cap", cap);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Loads full text / HTML / RTF / file list (decrypting secrets). Cheap no-op if already loaded.</summary>
    public void LoadFull(ClipItem item)
    {
        if (item.IsFull) return;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT text,html,rtf,files FROM bodies WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", item.Id);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            var text = r.IsDBNull(0) ? null : r.GetString(0);
            item.Text = item.Secret ? Secrets.DecryptText(text) : text;
            item.Html = r.IsDBNull(1) ? null : r.GetString(1);
            item.Rtf = r.IsDBNull(2) ? null : r.GetString(2);
            item.Files = r.IsDBNull(3) ? null : r.GetString(3);
        }
        item.IsFull = true;
    }

    public long Count()
    {
        if (_count >= 0) return _count;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items";
        return _count = (long)cmd.ExecuteScalar()!;
    }

    public long DiskUsage()
    {
        long total = 0;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            if (File.Exists(f)) total += new FileInfo(f).Length;
        try { total += new DirectoryInfo(ImagesDir).EnumerateFiles().Sum(f => f.Length); } catch { }
        return total;
    }

    public void SetPinned(long id, bool pinned)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE items SET pinned=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marks or unmarks an item as secret: re-encrypts text or the image file, swaps the hash,
    /// preview and search key. Rich formats are dropped when marking.</summary>
    public void SetSecret(ClipItem item, bool secret)
    {
        if (item.Secret == secret) return;
        LoadFull(item);

        string hash = item.Hash;
        string? image = item.ImagePath is null ? null : Path.GetFileName(item.ImagePath);

        if (item.IsTextual && item.Text is not null)
        {
            hash = secret ? Secrets.Hash(item.Text) : Secrets.PlainHash("t:", item.Text);
        }
        else if (item.Kind == ClipKind.Image && item.ImagePath is not null && File.Exists(item.ImagePath)
                 && !item.ImagePath.EndsWith(Secrets.EncryptedImageExt, StringComparison.OrdinalIgnoreCase))
        {
            // every image is stored encrypted; this only catches a file left from before that
            var target = Path.ChangeExtension(item.ImagePath, Secrets.EncryptedImageExt);
            File.WriteAllBytes(target, Secrets.Protect(File.ReadAllBytes(item.ImagePath)));
            Wipe(item.ImagePath);
            image = Path.GetFileName(target);
            item.ImagePath = target;
        }

        item.Secret = secret;
        if (secret) { item.Html = null; item.Rtf = null; }
        else item.Regions = null;

        using var tx = _db.BeginTransaction();
        try
        {
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE items SET secret=$sec, hash=$h, image=$img, preview=$p, search=$q" +
                                  (secret ? ", rich=0" : ", regions=NULL") + " WHERE id=$id";
                cmd.Parameters.AddWithValue("$sec", secret ? 1 : 0);
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$img", Db(image));
                cmd.Parameters.AddWithValue("$p", Db(PreviewOf(item)));
                cmd.Parameters.AddWithValue("$q", SearchKey(item));
                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.ExecuteNonQuery();
            }
            if (item.Text is not null)
            {
                using var b = _db.CreateCommand();
                b.Transaction = tx;
                b.CommandText = "UPDATE bodies SET text=$t" + (secret ? ", html=NULL, rtf=NULL" : "") + " WHERE id=$id";
                b.Parameters.AddWithValue("$t", secret ? Secrets.EncryptText(item.Text) : item.Text);
                b.Parameters.AddWithValue("$id", item.Id);
                b.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* UNIQUE: the same content already exists in that form */)
        {
            tx.Rollback();
            Delete(item.Id);
        }
        item.Hash = hash;
        item.PreviewText = PreviewOf(item);
        // push the old plaintext pages out of the WAL file
        Exec("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>Result of a later OCR pass over an image (context menu): secrets found → the image becomes secret
    /// (encrypted) with those regions blurred, or the whole picture when there are too many; otherwise its text
    /// becomes searchable.</summary>
    public void ApplyScan(ClipItem item, ImageScan scan)
    {
        if (item.Kind != ClipKind.Image) return;
        if (scan.Secrets.Count > 0 || scan.WholeImage)
        {
            if (!item.Secret) SetSecret(item, true);
            item.Regions = scan.WholeImage ? null : ImageScanner.Serialize(scan.Secrets);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE items SET regions=$r WHERE id=$id";
            cmd.Parameters.AddWithValue("$r", Db(item.Regions));
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.ExecuteNonQuery();
        }
        else if (!item.Secret)
        {
            item.OcrText = scan.Text;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE items SET search=$q WHERE id=$id";
            cmd.Parameters.AddWithValue("$q", SearchKey(item));
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Touch(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE items SET stamp=$s WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", ToUnix(DateTime.Now));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id) => DeleteWhere("id=$id", ("$id", id));

    public int ClearUnpinned() => DeleteWhere("pinned=0");

    /// <summary>Applies the item-count and age limits. Pinned items are never removed.</summary>
    public int Prune(int maxItems, int maxAgeDays)
    {
        int removed = 0;
        if (maxAgeDays > 0)
            removed += DeleteWhere("pinned=0 AND stamp < $t", ("$t", ToUnix(DateTime.Now.AddDays(-maxAgeDays))));
        if (maxItems > 0)
            removed += DeleteWhere("pinned=0 AND id NOT IN (SELECT id FROM items WHERE pinned=0 ORDER BY stamp DESC LIMIT $n)", ("$n", maxItems));
        return removed;
    }

    private int DeleteWhere(string where, params (string name, object value)[] args)
    {
        var images = new List<string>();
        int count;
        using (var tx = _db.BeginTransaction())
        {
            using (var sel = _db.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = $"SELECT image FROM items WHERE image IS NOT NULL AND {where}";
                foreach (var (n, v) in args) sel.Parameters.AddWithValue(n, v);
                using var r = sel.ExecuteReader();
                while (r.Read()) images.Add(r.GetString(0));
            }
            using (var body = _db.CreateCommand())
            {
                body.Transaction = tx;
                body.CommandText = $"DELETE FROM bodies WHERE id IN (SELECT id FROM items WHERE {where})";
                foreach (var (n, v) in args) body.Parameters.AddWithValue(n, v);
                body.ExecuteNonQuery();
            }
            using (var del = _db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM items WHERE {where}";
                foreach (var (n, v) in args) del.Parameters.AddWithValue(n, v);
                count = del.ExecuteNonQuery();
            }
            tx.Commit();
        }
        foreach (var img in images)
        {
            try { File.Delete(Path.Combine(ImagesDir, img)); } catch { }
        }
        if (count > 0) _count = -1;
        return count;
    }

    public void Dispose() => _db.Dispose();
}
