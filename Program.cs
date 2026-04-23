using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

// .env があれば読み込む（gitignore 対象）
if (File.Exists(".env"))
    foreach (var line in File.ReadAllLines(".env"))
        if (!line.StartsWith("#") && line.Contains('=')) {
            var parts = line.Split('=', 2);
            Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
        }

var OLLAMA_ENDPOINT = Environment.GetEnvironmentVariable("LOCUS_OLLAMA_ENDPOINT")
    ?? "http://localhost:11434/api/embeddings";
var DB_CONNECTION = Environment.GetEnvironmentVariable("LOCUS_DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Username=postgres;Database=locus_memories";
const int WATCH_INTERVAL_SEC = 30;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
using var db = new NpgsqlConnection(DB_CONNECTION);
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
await db.OpenAsync();
await Migrate();

// --- スキーママイグレーション ---
async Task Migrate() {
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        ALTER TABLE memories ADD COLUMN IF NOT EXISTS line_number INT;

        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.table_constraints
                WHERE table_name='memories' AND constraint_type='UNIQUE'
                AND constraint_name LIKE '%content_hash%'
            ) THEN
                EXECUTE (
                    SELECT 'ALTER TABLE memories DROP CONSTRAINT ' || constraint_name
                    FROM information_schema.table_constraints
                    WHERE table_name='memories' AND constraint_type='UNIQUE'
                    AND constraint_name LIKE '%content_hash%'
                    LIMIT 1
                );
            END IF;
        END $$;

        CREATE UNIQUE INDEX IF NOT EXISTS memories_source_line_idx
            ON memories (source_file, line_number)
            WHERE line_number IS NOT NULL;

        DELETE FROM memories WHERE line_number IS NULL;
    ";
    await cmd.ExecuteNonQueryAsync();
}

// --- Ingest 1行 ---
async Task Ingest(string text, string sourceFile, int lineNum) {
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    using var checkCmd = new NpgsqlCommand(
        "SELECT 1 FROM memories WHERE source_file=@s AND line_number=@n AND content_hash=@h", db);
    checkCmd.Parameters.AddWithValue("s", sourceFile);
    checkCmd.Parameters.AddWithValue("n", lineNum);
    checkCmd.Parameters.AddWithValue("h", hash);
    if (await checkCmd.ExecuteScalarAsync() != null) return; // 変更なし、スキップ

    var req = new { model = "mxbai-embed-large", prompt = text };
    var res = await http.PostAsJsonAsync(OLLAMA_ENDPOINT, req);
    var result = await res.Content.ReadFromJsonAsync<OllamaResponse>(jsonOptions);
    if (result?.Embedding == null) {
        Console.Error.WriteLine($"[Warn] embedding失敗: {text[..Math.Min(40, text.Length)]}");
        return;
    }
    var vec = $"[{string.Join(",", result.Embedding)}]";
    using var cmd = new NpgsqlCommand(
        "INSERT INTO memories (content, content_hash, source_file, line_number, embedding) " +
        "VALUES (@t, @h, @s, @n, @v::vector) " +
        "ON CONFLICT (source_file, line_number) WHERE line_number IS NOT NULL DO UPDATE SET " +
        "content=EXCLUDED.content, content_hash=EXCLUDED.content_hash, embedding=EXCLUDED.embedding", db);
    cmd.Parameters.AddWithValue("t", text);
    cmd.Parameters.AddWithValue("h", hash);
    cmd.Parameters.AddWithValue("s", sourceFile);
    cmd.Parameters.AddWithValue("n", lineNum);
    cmd.Parameters.AddWithValue("v", vec);
    await cmd.ExecuteNonQueryAsync();
}

// --- ファイル1本インジェスト ---
async Task<int> IngestFile(string path, string label) {
    if (!File.Exists(path)) {
        Console.Error.WriteLine($"[Error] 見つからない: {path}");
        return 0;
    }
    var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
    int count = 0;
    for (int i = 0; i < lines.Length; i++) {
        int lineNum = i + 1;
        var t = lines[i].Trim();
        if (t.Length == 0 || t == "---" || t.StartsWith("#")) continue;
        await Ingest(t, label, lineNum);
        count++;
        if (count % 50 == 0) Console.WriteLine($"  [{label}] {count} 行処理済み...");
    }
    // ファイルが短くなった行を削除
    using var delCmd = new NpgsqlCommand(
        "DELETE FROM memories WHERE source_file=@s AND line_number > @max", db);
    delCmd.Parameters.AddWithValue("s", label);
    delCmd.Parameters.AddWithValue("max", lines.Length);
    int deleted = await delCmd.ExecuteNonQueryAsync();
    if (deleted > 0) Console.WriteLine($"  [{label}] 削除行 {deleted} 件クリーンアップ");
    Console.WriteLine($"[完了] {label}: {count} 行インジェスト");
    return count;
}

// --- 検索 ---
async Task Search(string query) {
    Console.WriteLine($"\n[Search] '{query}' に近い記憶を探索中...");
    var req = new { model = "mxbai-embed-large", prompt = query };
    var res = await http.PostAsJsonAsync(OLLAMA_ENDPOINT, req);
    var result = await res.Content.ReadFromJsonAsync<OllamaResponse>(jsonOptions);
    if (result?.Embedding == null) { Console.Error.WriteLine("[Error] embedding失敗"); return; }
    var vec = $"[{string.Join(",", result.Embedding)}]";
    using var cmd = new NpgsqlCommand(
        "SELECT content, source_file, line_number, 1 - (embedding <=> @v::vector) AS score " +
        "FROM memories ORDER BY embedding <=> @v::vector LIMIT 5", db);
    cmd.Parameters.AddWithValue("v", vec);
    using var reader = await cmd.ExecuteReaderAsync();
    var hits = new List<(string content, string src, int? line, double score)>();
    while (await reader.ReadAsync()) {
        var line = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
        hits.Add((reader.GetString(0), reader.GetString(1), line, reader.GetDouble(3)));
    }
    reader.Close();

    foreach (var (content, src, line, score) in hits) {
        var loc = line.HasValue ? $"{src}:{line}" : src;
        Console.WriteLine($"  [{score:F3}] ({loc}) {content}");
    }
}

// --- コンテキスト取得 (前後N行) ---
async Task Context(string sourceFile, int lineNum, int window = 5) {
    var path = ResolveSourcePath(sourceFile);
    if (path == null || !File.Exists(path)) {
        Console.Error.WriteLine($"[Error] ファイルが見つからない: {sourceFile}");
        return;
    }
    var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
    int start = Math.Max(0, lineNum - 1 - window);
    int end = Math.Min(lines.Length - 1, lineNum - 1 + window);
    Console.WriteLine($"\n[Context] {sourceFile}:{lineNum} (±{window}行)");
    for (int i = start; i <= end; i++) {
        var marker = (i == lineNum - 1) ? ">>>" : "   ";
        Console.WriteLine($"  {marker} L{i + 1}: {lines[i]}");
    }
}

// --- watch モード ---
async Task Watch() {
    Console.WriteLine($"[Watch] 監視開始 ({WATCH_INTERVAL_SEC}秒ポーリング)");
    var lastMod = new Dictionary<string, DateTime>();
    while (true) {
        foreach (var (path, label) in GetFiles()) {
            if (!File.Exists(path)) continue;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (!lastMod.TryGetValue(path, out var prev) || mtime > prev) {
                Console.WriteLine($"[Watch] 変更検出: {label} ({mtime:HH:mm:ss} UTC)");
                await IngestFile(path, label);
                lastMod[path] = mtime;
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(WATCH_INTERVAL_SEC));
    }
}

// --- ヘルパー ---
(string path, string label)[] GetFiles() {
    var list = new List<(string, string)> { (@"L:\log.md", "log.md") };
    if (Directory.Exists(@"L:\projects"))
        foreach (var p in Directory.GetFiles(@"L:\projects", "*.md"))
            list.Add((p, "projects/" + Path.GetFileName(p)));
    return list.ToArray();
}

string? ResolveSourcePath(string label) => label switch {
    "log.md" => @"L:\log.md",
    var s when s.StartsWith("projects/") => $@"L:\projects\{Path.GetFileName(s)}",
    _ => null
};

// --- エントリーポイント ---
switch (args.Length > 0 ? args[0] : "ingest") {
    case "search":
        await Search(string.Join(" ", args[1..]));
        break;
    case "context":
        if (args.Length >= 3 && int.TryParse(args[2], out int ln)) {
            int w = args.Length >= 4 && int.TryParse(args[3], out int ww) ? ww : 5;
            await Context(args[1], ln, w);
        } else {
            Console.Error.WriteLine("使い方: dotnet run -- context <file> <line> [window=5]");
        }
        break;
    case "watch":
        await Watch();
        break;
    default:
        Console.WriteLine("--- Locus RAG: Ingest Mode ---");
        foreach (var (path, label) in GetFiles())
            await IngestFile(path, label);
        break;
}

record OllamaResponse(float[] Embedding);
