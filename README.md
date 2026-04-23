# Locus.RAG.Repo

Locus System の記憶ファイル（log.md / projects/*.md）をベクトル DB に投入して検索するインポーター。

## 構成

| コンポーネント | 場所 |
|---|---|
| pgvector (DB) | Docker (WSL2内) |
| Embedding モデル | Ollama `mxbai-embed-large` |
| インポーター | このリポジトリ (C# .NET 10) |

## セットアップ (Windows + WSL2 + Docker)

### 1. pgvector を WSL2 で起動

```bash
mkdir -p ~/locus_rag
cat > ~/locus_rag/docker-compose.yml << 'EOF'
services:
  db:
    image: pgvector/pgvector:pg16
    container_name: locus_rag_db
    environment:
      POSTGRES_PASSWORD: locus_password
      POSTGRES_DB: locus_memories
    ports:
      - "5432:5432"
    restart: always
EOF

cd ~/locus_rag && docker compose up -d
```

### 2. スキーマ初期化

```bash
docker exec -i locus_rag_db psql -U postgres -d locus_memories << 'EOF'
CREATE EXTENSION IF NOT EXISTS vector;
CREATE TABLE IF NOT EXISTS memories (
    id SERIAL PRIMARY KEY,
    content TEXT NOT NULL,
    content_hash TEXT UNIQUE NOT NULL,
    source_file TEXT,
    embedding vector(1024)
);
EOF
```

### 3. LAN 公開 (portproxy)

WSL2 は仮想 NIC を持つため、同一 LAN の別マシンからは `<ホストIP>:5432` に届かない。
`scripts/setup_portproxy.ps1` を **管理者権限** で実行して Windows 側にポート転送ルールを追加する。

```powershell
# 手動実行
powershell -ExecutionPolicy Bypass -File scripts\setup_portproxy.ps1
```

**タスクスケジューラで自動化（推奨）**

WSL2 の仮想 IP は再起動のたびに変わるため、ログオン時に自動実行させる。

```powershell
schtasks /Create /SC ONLOGON /TN "WSL2 portproxy pgvector" `
  /TR "powershell -ExecutionPolicy Bypass -File C:\path\to\scripts\setup_portproxy.ps1" `
  /RU SYSTEM /RL HIGHEST /F
```

> **Note:** `.wslconfig` の `networkingMode=mirrored` でも同様の効果を得られるが、
> WSL2 内の Docker ネットワークと干渉するリスクがあるため本構成では不採用。

### 4. インポーター実行

`Program.cs` 先頭の定数を環境に合わせて変更する。

```csharp
const string OLLAMA_ENDPOINT = "http://<ollama-host>:11434/api/embeddings";
const string DB_CONNECTION = "Host=<db-host>;Port=5432;Username=postgres;Password=locus_password;Database=locus_memories";
```

```powershell
# log.md + projects/*.md を全件インジェスト（冪等・重複スキップ）
dotnet run -- ingest

# 検索
dotnet run -- search "クエリ"
```

## DB メンテナンス

```bash
# 全件削除してやり直し
docker exec -it locus_rag_db psql -U postgres -d locus_memories -c "TRUNCATE memories;"

# 件数確認
docker exec locus_rag_db psql -U postgres -d locus_memories -c \
  "SELECT source_file, COUNT(*) FROM memories GROUP BY source_file ORDER BY source_file;"
```
