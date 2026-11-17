# 04 · 数据规格

> 所有 SQL 必须参数化。所有写操作必须走 ADR-007 的受控流程。

---

## 1. Ditto.db 只读契约

### 1.1 连接方式

```
file:D:/.../Ditto.db?mode=ro
```
- `mode=ro` 强制只读，从驱动层杜绝误写
- 必须设置 `busy_timeout = 3000`
- 不用 `immutable=1`（Ditto 会持续写入，缓存会失效）

### 1.2 允许的查询（白名单）

| 用途 | SQL 形态 |
|---|---|
| 首页列表 | `SELECT lID,mText,lParentID,lDate,CRC,clipOrder FROM Main WHERE bIsGroup=0 ORDER BY clipOrder DESC LIMIT ?` |
| 按组列表 | 同上 + `AND lParentID=?` |
| 搜索 | 见 §3 |
| 详情 | `SELECT ... FROM Data WHERE lParentID=?` |
| 分组树 | `SELECT lID,mText,lParentID,clipOrder FROM Main WHERE bIsGroup=1` |
| 类型统计 | `SELECT strClipBoardFormat,COUNT(*) FROM Data GROUP BY 1` |
| 新条目检测 | `SELECT MAX(lID) FROM Main` |

### 1.3 禁止

- `ALTER` / `DROP` / `CREATE` / `PRAGMA`（除读取）
- `DELETE FROM Main`（会触发级联，且绕过 Ditto）
- 读 `Data.ooData` 全量（830MB，只按 `lParentID` 单条读）

---

## 2. 写入契约（受控）

### 2.1 允许写的字段（仅这 4 个）

| 字段 | 用途 |
|---|---|
| `Main.lParentID` | 分组归属 |
| `Main.stickyClipOrder` / `stickyGroupOrder` | 置顶 |
| `Main.lDontAutoDelete` | 保留标记 |

### 2.2 新建分组

```sql
-- 在同层取最大 clipOrder，+1 插入
INSERT INTO Main (mText, lDate, bIsGroup, lParentID, clipOrder, clipGroupOrder,
                  lDontAutoDelete, lastPasteDate, stickyClipOrder, stickyGroupOrder)
VALUES (?, strftime('%s','now'), 1, ?, ?, 0, 0, 0, -2147483647, -2147483647);
```

⚠️ `stickyClipOrder` 的"未置顶"值是 **-2147483647**，不是 0（实测确认）。

### 2.3 写入流程（强制）

```
1. 复制 Ditto.db → backup/Ditto_YYYYMMDD_HHMMSS.db（按需，同一会话内 5 分钟内只备一次）
2. BEGIN IMMEDIATE
3. 执行 UPDATE/INSERT
4. COMMIT
5. SendMessage(hwnd, WM_REFRESH_VIEW=1229, 0, 0)
6. 失败 → ROLLBACK + 记录日志 + 抛出可恢复错误
```

### 2.4 并发处理

- `journal_mode=delete` → 写入会短暂锁库
- 遇到 `SQLITE_BUSY`：重试 3 次，间隔 50/100/200ms
- 三次仍失败：提示「Ditto 正忙，请稍后再试」，**不弹模态框**

---

## 3. 搜索方案（已定：LIKE，不用 FTS5）

### 3.1 为什么不用 FTS5

| 查询词 | FTS5 trigram | LIKE |
|---|---|---|
| `配装`（2 字） | **0 条** | 1 条 |
| `枪械`（2 字） | **0 条** | 1 条 |
| `枪械配装`（4 字） | 1 条 | 1 条 |

trigram 按 3 字符滑窗切分，**2 个汉字凑不成一个 trigram**，中文高频两字词全部失配。

### 3.2 实测性能

| 场景 | 耗时 |
|---|---|
| `LIKE '%kw%'` 6,179 条（磁盘） | 5.7–7.1 ms |
| 搜索 + 排序 + LIMIT 50 | 9.1 ms |
| 首页加载（最近 50 条） | 10.8 ms |

5 万条估算 ≈ 55 ms（目标 < 150 ms）。

### 3.3 查询构造

```sql
SELECT lID, mText, lParentID, lDate, CRC
FROM Main
WHERE bIsGroup = 0
  AND mText LIKE @kw1 AND mText LIKE @kw2   -- 多关键词 AND
ORDER BY clipOrder DESC
LIMIT 200;
```

- 关键词转义：`%` `_` `\` 需转义，或用 `ESCAPE '\'`
- 高亮：**客户端**匹配位置加标记，不在 SQL 里做
- 正则 `re:`：读出候选后在内存用 `Regex` 过滤（6 千条约 20–40ms，可接受）

### 3.4 语法映射

| 用户输入 | 转换 |
|---|---|
| `abc def` | `LIKE '%abc%' AND LIKE '%def%'` |
| `"abc def"` | `LIKE '%abc def%'` |
| `g:M4A1` | 先查组 ID → `AND lParentID=?` |
| `tag:周报` | 子查询 `lID IN (SELECT clip_id FROM sidecar.clip_tag ...)` |
| `type:image` | `AND lID IN (SELECT lParentID FROM Data WHERE strClipBoardFormat='CF_DIB')` |
| `app:chrome` | `AND lID IN (SELECT clip_id FROM sidecar.clip_meta WHERE source_app LIKE ?)` |
| `date:today` | `AND lDate >= strftime('%s','now','start of day')` |
| `secret:` | 客户端跑敏感检测，取命中的 id 集合 |
| `ai:` | 走 `AiService`，不走 SQL |

---

## 4. sidecar.db schema

### ⚠️ 关键约束：必须用 UTF-16LE 编码

Ditto.db 的编码是 `UTF-16le`。SQLite 要求 **ATTACH 的库与主库编码一致**，否则报：

```
attached databases must use the same text encoding as main database
```

所以创建 sidecar 时**必须在建表前**执行：

```sql
PRAGMA encoding = 'UTF-16le';
```

实测：设了之后 ATTACH 成功，跨库 JOIN 6 行耗时 **1.0 ms**。

### 4.1 建表

```sql
PRAGMA encoding = 'UTF-16le';
PRAGMA user_version = 1;

CREATE TABLE tag (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  name       TEXT NOT NULL UNIQUE,
  color      TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE clip_tag (
  clip_id INTEGER NOT NULL,
  tag_id  INTEGER NOT NULL,
  PRIMARY KEY (clip_id, tag_id)
);
CREATE INDEX idx_clip_tag_tag ON clip_tag(tag_id);

CREATE TABLE clip_meta (
  clip_id      INTEGER PRIMARY KEY,
  source_app   TEXT,          -- 来源进程名，如 chrome.exe
  source_title TEXT,
  crc          INTEGER,       -- 用于与 Ditto 的 Main.CRC 对齐
  seen_at      INTEGER
);
CREATE INDEX idx_clip_meta_app ON clip_meta(source_app);

CREATE TABLE ai_cache (
  clip_id    INTEGER PRIMARY KEY,
  summary    TEXT,
  tags_json  TEXT,
  updated_at INTEGER
);

CREATE TABLE never_upload_group (
  group_id INTEGER PRIMARY KEY,
  reason   TEXT
);

CREATE TABLE app_meta (
  key   TEXT PRIMARY KEY,
  value TEXT
);
```

### 4.2 与 Ditto.db 的联查（ATTACH）

```csharp
// 连接字符串同 Ditto.db，然后在同一连接上 ATTACH
// ATTACH 'data/sidecar.db' AS sc;
```

```sql
SELECT m.lID, m.mText, group_concat(t.name) AS tags
FROM Main m
LEFT JOIN sc.clip_tag ct ON ct.clip_id = m.lID
LEFT JOIN sc.tag t ON t.id = ct.tag_id
WHERE m.bIsGroup = 0
GROUP BY m.lID
ORDER BY m.clipOrder DESC
LIMIT 50;
```

---

## 5. 数据一致性

### 5.1 孤儿标签对账（启动时）

```sql
-- 找出 sidecar 里指向已不存在条目的引用
SELECT ct.clip_id FROM clip_tag ct
LEFT JOIN Main m ON m.lID = ct.clip_id
WHERE m.lID IS NULL;
```
- 删除这些引用
- 数量 > 100 时提示「标签需要重新整理」

### 5.2 新条目检测

- 定时器（面板可见时 1s 一次）查 `SELECT MAX(lID) FROM Main`
- 变化则增量拉取 `lID > lastSeenId`
- 不在每次按键时才查（浪费）

### 5.3 来源应用补录

Ditto 不记录来源应用。补录方式：

1. 外壳轻量监听 `WM_CLIPBOARDUPDATE`
2. 取前台进程名 + 剪贴板文本
3. 算 CRC（与 Ditto 同算法）
4. 查 `SELECT lID FROM Main WHERE CRC=? ORDER BY lID DESC LIMIT 1`
5. 写入 `clip_meta`

⚠️ 第 3 步的 CRC 算法必须与 Ditto 一致（见 `src/Misc.cpp` 的 `GetClipMD5` / CRC 计算），实现前需核对。

---

## 6. Ditto.Settings 读写

### 6.1 格式

- **UTF-16LE 编码的 INI 文本**
- 约 142 行，`[Ditto]` 段
- 格式：`Key=Value`，每行一个

### 6.2 读

```csharp
File.ReadAllText(path, Encoding.Unicode)  // UTF-16LE
// 逐行解析 key=value
```

### 6.3 写（按行编辑，不重写全文件）

1. 读取全部行
2. 找到目标 key 所在行，替换 `Key=新值`
3. key 不存在 → 追加到 `[Ditto]` 段末尾
4. 写回时保持 UTF-16LE（`Encoding.Unicode` + BOM）
5. 写前备份到 `backup/Ditto.Settings_YYYYMMDD_HHMMSS.ini`

### 6.4 已知关键值

| Key | 现值 | 含义 |
|---|---|---|
| `MaxEntries` | 10000 | 最大条目数 |
| `CheckForExpiredEntries` | 0 | 按天清理开关 |
| `ExpiredEntries` | 5 | 保留天数 |
| `MaxClipSizeInBytes` | 0 | 单条大小上限（0=不限） |
| `AllowDuplicates` | 0 | 允许重复 |
| `SimpleTextSearch` | 1 | 简单搜索 |
| `DittoHotKey` | **704** | `704 = 0x02C0 = Ctrl + \`` |
| `DittoHotKey2` | 960 | `Ctrl + Alt + \`` |
| `MainhWnd` | 327812 | 主窗口句柄（会变） |
| `LanguageFile` | Chinese Simplified | 语言 |

### 6.5 需要重启才生效的设置

- 修改后提示「部分设置需重启 Ditto 生效」
- 实现阶段逐项验证（待实测 #2）

---

## 7. 缩略图缓存

| 项 | 规格 |
|---|---|
| 存储 | **仅内存**，不落盘 |
| 容量 | LRU，最多 100 张 |
| 尺寸 | 长边 200px（列表）/ 800px（预览）两档 |
| 估算内存 | 200px ≈ 160KB/张 → 100 张 ≈ 16MB |
| 解码线程 | 后台，`Task.Run`，带 `CancellationToken` |
| 失败 | 显示灰色占位图 + 记日志 |

**CF_DIB 格式**：从 `BITMAPINFOHEADER`（首字节 `0x28`）开始，**没有 BMP 文件头**。解码时需自己拼 14 字节 `BITMAPFILEHEADER`，或直接用 WPF 的 `BitmapSource.Create` 手动解析像素。

---

## 8. 文件布局

```
<AppDir>/
├─ DittoShell.exe
├─ data/
│  ├─ config.json
│  ├─ sidecar.db          ← UTF-16LE！
│  └─ logs/
└─ backup/

Ditto 安装目录（不归我们管，只读 + 两个文件写）：
├─ Ditto.db
└─ Ditto.Settings
```
