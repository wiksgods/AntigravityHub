using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProfileSwitchChrome.Services
{
    /// <summary>
    /// Restores lost or unmapped conversation histories in Antigravity IDE and re-maps them
    /// to their respective sidebar project folders and workspace databases.
    /// Based on: https://github.com/alenjoby/Antigravity-2.0-Restore-Fix
    /// </summary>
    public static class AntigravityChatRestorer
    {
        private static readonly string AppDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public class RestoreResult
        {
            public bool Success { get; set; }
            public int TotalRestoredCount { get; set; }
            public int WorkspacesCount { get; set; }
            public string Message { get; set; } = string.Empty;
            public List<string> Details { get; set; } = new();
        }

        private class WorkspaceInfo
        {
            public string Id { get; set; } = string.Empty;
            public string Uri { get; set; } = string.Empty;
            public string LocalPath { get; set; } = string.Empty;
        }

        public class ProtoField
        {
            public int FieldNum { get; set; }
            public int WireType { get; set; } // 0 = varint, 1 = 64-bit, 2 = bytes, 5 = 32-bit
            public ulong VarintValue { get; set; }
            public byte[] BytesValue { get; set; } = Array.Empty<byte>();
        }

        public class ChatIndexEntry
        {
            [JsonProperty("sessionId")]
            public string SessionId { get; set; } = string.Empty;

            [JsonProperty("title")]
            public string Title { get; set; } = string.Empty;

            [JsonProperty("lastMessageDate")]
            public long LastMessageDate { get; set; }

            [JsonProperty("isImported")]
            public bool IsImported { get; set; } = false;

            [JsonProperty("initialLocation")]
            public string InitialLocation { get; set; } = "panel";

            [JsonProperty("isEmpty")]
            public bool IsEmpty { get; set; } = false;
        }

        public class ChatIndexRoot
        {
            [JsonProperty("version")]
            public int Version { get; set; } = 1;

            [JsonProperty("entries")]
            public Dictionary<string, ChatIndexEntry> Entries { get; set; } = new();
        }

        #region Path Resolvers

        private static string GetAppDataRoot()
        {
            string p1 = Path.Combine(AppDataRoaming, "Antigravity IDE");
            if (Directory.Exists(p1)) return p1;
            string p2 = Path.Combine(AppDataRoaming, "Antigravity");
            if (Directory.Exists(p2)) return p2;
            return p1;
        }

        private static List<string> GetGeminiRoots()
        {
            var roots = new List<string>();
            string r1 = Path.Combine(UserProfile, ".gemini", "antigravity-ide");
            if (Directory.Exists(r1)) roots.Add(r1);
            string r2 = Path.Combine(UserProfile, ".gemini", "antigravity");
            if (Directory.Exists(r2)) roots.Add(r2);
            if (roots.Count == 0) roots.Add(r1);
            return roots;
        }

        public static string NormalizeUriToPath(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return "";
            try
            {
                string decoded = Uri.UnescapeDataString(uri).ToLowerInvariant();
                decoded = decoded.Replace("file:///", "").Replace("file://", "").Replace('\\', '/');
                while (decoded.Contains("//"))
                {
                    decoded = decoded.Replace("//", "/");
                }
                return decoded.TrimEnd('/');
            }
            catch
            {
                return uri.ToLowerInvariant().Replace("file:///", "").Replace("file://", "").Replace('\\', '/').TrimEnd('/');
            }
        }

        private static string? GetOriginalUriFromConvDb(string? dbPath)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || !dbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    DefaultTimeout = 3
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT data FROM trajectory_metadata_blob WHERE id = 'main' LIMIT 1";
                var obj = cmd.ExecuteScalar();
                if (obj is byte[] blob)
                {
                    string s = Encoding.UTF8.GetString(blob);
                    var m = System.Text.RegularExpressions.Regex.Match(s, @"file:///[a-zA-Z0-9_\-\.\/%:]+");
                    if (m.Success)
                    {
                        return m.Value;
                    }
                }
            }
            catch
            {
                try
                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = dbPath,
                        Mode = SqliteOpenMode.ReadOnly,
                        DefaultTimeout = 2
                    }.ToString();

                    using var conn = new SqliteConnection(connStr);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT data FROM trajectory_metadata_blob WHERE id = 'main' LIMIT 1";
                    var obj = cmd.ExecuteScalar();
                    if (obj is byte[] blob)
                    {
                        string s = Encoding.UTF8.GetString(blob);
                        var m = System.Text.RegularExpressions.Regex.Match(s, @"file:///[a-zA-Z0-9_\-\.\/%:]+");
                        if (m.Success)
                        {
                            return m.Value;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string? ExtractUriFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                string s = Encoding.UTF8.GetString(bytes);
                var m = System.Text.RegularExpressions.Regex.Match(s, @"file:///[a-zA-Z0-9_\-\.\/%:]+");
                if (m.Success) return m.Value;
            }
            catch { }
            return null;
        }

        private static WorkspaceInfo? FindBestMatchingWorkspace(string projectUri, List<WorkspaceInfo> workspaces)
        {
            if (string.IsNullOrWhiteSpace(projectUri) || workspaces == null || workspaces.Count == 0)
                return null;

            string normProj = NormalizeUriToPath(projectUri);
            if (string.IsNullOrEmpty(normProj)) return null;

            // 1. Exact match (case-insensitive) - Highest priority!
            var exact = workspaces.FirstOrDefault(w => string.Equals(w.LocalPath, normProj, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // 2. normProj is INSIDE workspace w (normProj is a subdirectory or file in workspace w)
            // workspaces is sorted descending by LocalPath.Length, so the most specific enclosing workspace matches!
            var enclosing = workspaces.FirstOrDefault(w => normProj.StartsWith(w.LocalPath + "/", StringComparison.OrdinalIgnoreCase));
            if (enclosing != null) return enclosing;

            // 3. Workspace w is a subdirectory inside normProj
            var sub = workspaces.FirstOrDefault(w => w.LocalPath.StartsWith(normProj + "/", StringComparison.OrdinalIgnoreCase));
            if (sub != null) return sub;

            return null;
        }

        #endregion

        #region Protobuf Helpers

        public static (ulong value, int length) ReadVarint(byte[] buffer, int offset)
        {
            ulong result = 0;
            int shift = 0;
            int idx = offset;
            while (idx < buffer.Length)
            {
                byte b = buffer[idx++];
                result |= (ulong)(b & 0x7f) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            return (result, idx - offset);
        }

        public static byte[] WriteVarint(ulong value)
        {
            var bytes = new List<byte>();
            ulong val = value;
            while (val >= 0x80)
            {
                bytes.Add((byte)((val & 0x7f) | 0x80));
                val >>= 7;
            }
            bytes.Add((byte)(val & 0x7f));
            return bytes.ToArray();
        }

        public static List<ProtoField> DecodeProto(byte[] buffer, int start = 0, int end = -1)
        {
            if (end < 0 || end > buffer.Length) end = buffer.Length;
            int idx = start;
            var fields = new List<ProtoField>();

            while (idx < end)
            {
                if (idx >= buffer.Length) break;
                var (tag, tagLen) = ReadVarint(buffer, idx);
                idx += tagLen;
                int wireType = (int)(tag & 0x07);
                int fieldNum = (int)(tag >> 3);

                if (idx > end) break;

                if (wireType == 0) // varint
                {
                    var (val, vlen) = ReadVarint(buffer, idx);
                    fields.Add(new ProtoField { FieldNum = fieldNum, WireType = 0, VarintValue = val });
                    idx += vlen;
                }
                else if (wireType == 1) // 64-bit
                {
                    idx += 8;
                }
                else if (wireType == 2) // bytes / length-delimited
                {
                    var (valLen, lenLen) = ReadVarint(buffer, idx);
                    idx += lenLen;
                    int length = (int)valLen;
                    if (idx + length <= buffer.Length && length >= 0)
                    {
                        byte[] valBuf = new byte[length];
                        Buffer.BlockCopy(buffer, idx, valBuf, 0, length);
                        fields.Add(new ProtoField { FieldNum = fieldNum, WireType = 2, BytesValue = valBuf });
                        idx += length;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (wireType == 5) // 32-bit
                {
                    idx += 4;
                }
                else
                {
                    break;
                }
            }

            return fields;
        }

        public static byte[] EncodeField(int fieldNum, int wireType, byte[] value)
        {
            byte[] key = WriteVarint((ulong)((fieldNum << 3) | wireType));
            if (wireType == 2)
            {
                byte[] len = WriteVarint((ulong)value.Length);
                byte[] res = new byte[key.Length + len.Length + value.Length];
                Buffer.BlockCopy(key, 0, res, 0, key.Length);
                Buffer.BlockCopy(len, 0, res, key.Length, len.Length);
                Buffer.BlockCopy(value, 0, res, key.Length + len.Length, value.Length);
                return res;
            }
            else if (wireType == 0)
            {
                byte[] res = new byte[key.Length + value.Length];
                Buffer.BlockCopy(key, 0, res, 0, key.Length);
                Buffer.BlockCopy(value, 0, res, key.Length, value.Length);
                return res;
            }
            throw new NotSupportedException($"Wire type {wireType} not supported.");
        }

        public static byte[] EncodeString(int fieldNum, string str)
        {
            return EncodeField(fieldNum, 2, Encoding.UTF8.GetBytes(str));
        }

        public static byte[] EncodeBytes(int fieldNum, byte[] data)
        {
            return EncodeField(fieldNum, 2, data);
        }

        public static byte[] EncodeVarintField(int fieldNum, ulong val)
        {
            return EncodeField(fieldNum, 0, WriteVarint(val));
        }

        public static byte[] ConcatBuffers(params byte[][] bufs)
        {
            int total = 0;
            foreach (var b in bufs) if (b != null) total += b.Length;
            byte[] res = new byte[total];
            int offset = 0;
            foreach (var b in bufs)
            {
                if (b != null && b.Length > 0)
                {
                    Buffer.BlockCopy(b, 0, res, offset, b.Length);
                    offset += b.Length;
                }
            }
            return res;
        }

        public static byte[] BuildTimestampMessage(long seconds, long nanos = 0)
        {
            return ConcatBuffers(EncodeVarintField(1, (ulong)seconds), EncodeVarintField(2, (ulong)nanos));
        }

        public static long GetTimestampFromMeta(List<ProtoField> fields, long fallback = 0)
        {
            if (fields == null) return fallback;
            var f3 = fields.FirstOrDefault(x => x.FieldNum == 3 && x.WireType == 2);
            if (f3 != null)
            {
                var sub = DecodeProto(f3.BytesValue);
                var s1 = sub.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 0);
                if (s1 != null) return (long)s1.VarintValue;
            }
            return fallback;
        }

        public static bool IsGarbageTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length < 2) return true;
            string trimmed = title.Trim();
            string low = trimmed.ToLowerInvariant();
            if (low.StartsWith("conversation (") || low.StartsWith("conversation(") || low.StartsWith("$")) return true;
            if (low.StartsWith("c:\\") || low.StartsWith("d:\\") || low.StartsWith("e:\\") || low.StartsWith("/") || low.StartsWith("\\")) return true;
            if (low.StartsWith("file:") || low.StartsWith("http:") || low.StartsWith("https:")) return true;
            if (low.StartsWith("<") && (low.EndsWith(">") || low.Contains("</"))) return true;
            if (low.StartsWith("# conversation history") || low.StartsWith("conversation history")) return true;
            if (low is "tasks" or "task" or "task list" or "todo" or "todos" or "checklist" or "overview" or "implementation plan" or "summary") return true;
            if (low.StartsWith("- [ ]") || low.StartsWith("- [x]") || low.StartsWith("[ ]") || low.StartsWith("[x]")) return true;
            return false;
        }

        private static string? ExtractTitleFromConversationDb(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath) || !dbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 2
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT step_payload FROM steps WHERE step_type = 23 ORDER BY idx ASC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader[0] is byte[] payload && payload.Length > 0)
                    {
                        var fields = DecodeProto(payload);
                        foreach (var f in fields)
                        {
                            if (f.FieldNum == 30 && f.WireType == 2)
                            {
                                var subFields = DecodeProto(f.BytesValue);
                                foreach (var sf in subFields)
                                {
                                    if (sf.FieldNum == 4 && sf.WireType == 2)
                                    {
                                        string raw = Encoding.UTF8.GetString(sf.BytesValue).Trim();
                                        if (!string.IsNullOrEmpty(raw))
                                        {
                                            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                            foreach (var l in lines)
                                            {
                                                string candidate = l.Trim();
                                                if (!IsGarbageTitle(candidate))
                                                {
                                                    return candidate.Length > 70 ? candidate.Substring(0, 70) : candidate;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extracts a meaningful conversation title from brain artifacts or transcripts.
        /// Priority: 1. conversation .db step 23, 2. task.md heading, 3. transcript.jsonl prompt, 4. fallback to short ID.
        /// </summary>
        public static string ExtractConversationTitle(string id, IEnumerable<string> geminiRoots)
        {
            foreach (var gr in geminiRoots)
            {
                // Priority 1: Check conversation database step 23
                string dbPath = Path.Combine(gr, "conversations", $"{id}.db");
                string? dbTitle = ExtractTitleFromConversationDb(dbPath);
                if (!string.IsNullOrEmpty(dbTitle))
                {
                    return dbTitle;
                }

                string brainPath = Path.Combine(gr, "brain", id);
                if (!Directory.Exists(brainPath)) continue;

                // Priority 2: Check task.md
                string taskFile = Path.Combine(brainPath, "task.md");
                if (File.Exists(taskFile))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(taskFile))
                        {
                            string trimmed = line.Trim();
                            if (trimmed.StartsWith("#"))
                            {
                                string heading = trimmed.TrimStart('#', ' ').Trim();
                                heading = System.Text.RegularExpressions.Regex.Replace(
                                    heading,
                                    @"^(?:Task\s*List|Tasks?)\s*:\s*",
                                    "",
                                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

                                if (!IsGarbageTitle(heading))
                                {
                                    return heading.Length > 70 ? heading.Substring(0, 70) : heading;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Priority 3: Check transcript.jsonl or transcript_full.jsonl
                string logDir = Path.Combine(brainPath, ".system_generated", "logs");
                string[] candidateLogs = { Path.Combine(logDir, "transcript.jsonl"), Path.Combine(logDir, "transcript_full.jsonl") };
                foreach (var lp in candidateLogs)
                {
                    if (File.Exists(lp))
                    {
                        try
                        {
                            using var sr = new StreamReader(lp, Encoding.UTF8);
                            string? line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                if (!line.Contains("\"USER_INPUT\"") && !line.Contains("\"USER_EXPLICIT\"")) continue;

                                var match = System.Text.RegularExpressions.Regex.Match(line, @"<USER_REQUEST>\s*([\s\S]*?)\s*</USER_REQUEST>");
                                string prompt = match.Success ? match.Groups[1].Value.Trim() : "";
                                if (string.IsNullOrEmpty(prompt))
                                {
                                    try
                                    {
                                        var j = JObject.Parse(line);
                                        prompt = j["content"]?.ToString() ?? "";
                                    }
                                    catch { }
                                }

                                if (!string.IsNullOrWhiteSpace(prompt))
                                {
                                    // Strip leading system block if conversation starts with history
                                    prompt = System.Text.RegularExpressions.Regex.Replace(
                                        prompt,
                                        @"# Conversation History[\s\S]*?</conversation_summaries>",
                                        "",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

                                    // Strip leading Windows path even with spaces (e.g. C:\...\ProfileSwitchChrome - Sao chép)
                                    prompt = System.Text.RegularExpressions.Regex.Replace(
                                        prompt,
                                        @"^[a-zA-Z]:\\[^\r\n]*?(?: - Sao chép| - Copy)?(?=[,:\s]|$)[,:\s]*",
                                        "").Trim();
                                    prompt = System.Text.RegularExpressions.Regex.Replace(
                                        prompt,
                                        @"^[\s\-–—]*(?:Sao\s+chép|Copy)[\s,:\-–—]*",
                                        "",
                                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                                    prompt = prompt.TrimStart(',', ':', '-', '–', '—', ' ', '\t', '\r', '\n').Trim();

                                    var lines = prompt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                    foreach (var l in lines)
                                    {
                                        string firstLine = l.Trim();
                                        if (firstLine.Length > 0 && !IsGarbageTitle(firstLine))
                                        {
                                            firstLine = char.ToUpper(firstLine[0]) + (firstLine.Length > 1 ? firstLine.Substring(1) : "");
                                            if (firstLine.Length > 70) firstLine = firstLine.Substring(0, 70);
                                            return firstLine;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            return $"Conversation ({id.Substring(0, Math.Min(8, id.Length))})";
        }

        #endregion

        /// <summary>
        /// Main restoration logic. Scans all conversation sources, reconstructs metadata and indexes,
        /// and updates agyhub_summaries_proto.pb, trajectorySummaries, and workspace databases.
        public static RestoreResult RestoreConversations(bool forceCloseIde = false)
        {
            var result = new RestoreResult();

            string appDataRoot = GetAppDataRoot();
            var geminiRoots = GetGeminiRoots();
            string globalDbPath = Path.Combine(appDataRoot, "User", "globalStorage", "state.vscdb");
            string globalDbBackupPath = Path.Combine(appDataRoot, "User", "globalStorage", "state.vscdb.backup");
            string workspaceStorageDir = Path.Combine(appDataRoot, "User", "workspaceStorage");

            // Auto-recover from backup if global state.vscdb is missing or 0 bytes after crash
            if (!File.Exists(globalDbPath) || new FileInfo(globalDbPath).Length == 0)
            {
                if (File.Exists(globalDbBackupPath) && new FileInfo(globalDbBackupPath).Length > 0)
                {
                    try { File.Copy(globalDbBackupPath, globalDbPath, overwrite: true); } catch { }
                }
                else if (File.Exists(globalDbPath + ".bak") && new FileInfo(globalDbPath + ".bak").Length > 0)
                {
                    try { File.Copy(globalDbPath + ".bak", globalDbPath, overwrite: true); } catch { }
                }
            }

            if (!File.Exists(globalDbPath))
            {
                result.Success = false;
                result.Message = "Không tìm thấy cơ sở dữ liệu global (state.vscdb).";
                return result;
            }

            // Close IDE / lingering helper processes if requested
            if (forceCloseIde)
            {
                AntigravityProfileManager.KillAllAntigravityProcesses();
                System.Threading.Thread.Sleep(1000);
            }

            try
            {
                // 1. Read sidebar workspaces from state.vscdb
                var sidebarWorkspaceUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = globalDbPath,
                        Mode = SqliteOpenMode.ReadWrite,
                        DefaultTimeout = 5
                    }.ToString();

                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var pragmaCmd = conn.CreateCommand())
                        {
                            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                            pragmaCmd.ExecuteNonQuery();
                        }

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.sidebarWorkspaces'";
                        var val = cmd.ExecuteScalar()?.ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            byte[] buf = Convert.FromBase64String(val);
                            var fields = DecodeProto(buf);
                            foreach (var f in fields)
                            {
                                if (f.FieldNum == 1 && f.WireType == 2)
                                {
                                    try
                                    {
                                        var nested = DecodeProto(f.BytesValue);
                                        var uriField = nested.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                        if (uriField != null)
                                        {
                                            string uriStr = Encoding.UTF8.GetString(uriField.BytesValue);
                                            sidebarWorkspaceUris.Add(Uri.UnescapeDataString(uriStr));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatRestorer] Error loading sidebar workspaces: " + ex.Message);
                }

                // 2. Load all valid workspaces in workspaceStorage
                var workspaces = new List<WorkspaceInfo>();
                if (Directory.Exists(workspaceStorageDir))
                {
                    foreach (var dir in Directory.GetDirectories(workspaceStorageDir))
                    {
                        string dirName = Path.GetFileName(dir);
                        string wsJsonPath = Path.Combine(dir, "workspace.json");
                        if (File.Exists(wsJsonPath))
                        {
                            try
                            {
                                string content = File.ReadAllText(wsJsonPath);
                                var jObj = JObject.Parse(content);
                                string folderUri = jObj["folder"]?.ToString() ?? jObj["workspace"]?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(folderUri))
                                {
                                    string normFolder = NormalizeUriToPath(folderUri);
                                    if (!string.IsNullOrEmpty(normFolder))
                                    {
                                        workspaces.Add(new WorkspaceInfo
                                        {
                                            Id = dirName,
                                            Uri = folderUri,
                                            LocalPath = normFolder
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                workspaces.Sort((a, b) => b.LocalPath.Length.CompareTo(a.LocalPath.Length));

                // Determine default/active workspace (most recently modified state.vscdb)
                WorkspaceInfo? defaultWorkspace = null;
                if (workspaces.Count > 0)
                {
                    defaultWorkspace = workspaces.OrderByDescending(w =>
                    {
                        string wsDb = Path.Combine(workspaceStorageDir, w.Id, "state.vscdb");
                        return File.Exists(wsDb) ? File.GetLastWriteTimeUtc(wsDb) : DateTime.MinValue;
                    }).FirstOrDefault() ?? workspaces[0];
                }

                // 3. Helper to verify if conversation files exist on disk
                string? FindConvoFile(string cid)
                {
                    foreach (var gr in geminiRoots)
                    {
                        string convDir = Path.Combine(gr, "conversations");
                        string dbFile = Path.Combine(convDir, $"{cid}.db");
                        string pbFile = Path.Combine(convDir, $"{cid}.pb");
                        if (File.Exists(dbFile)) return dbFile;
                        if (File.Exists(pbFile)) return pbFile;
                    }
                    return null;
                }

                // 4. Gather summaries from agyhub_summaries_proto.pb and trajectorySummaries
                var allSummaries = new Dictionary<string, List<ProtoField>>(StringComparer.OrdinalIgnoreCase);

                // 4a. Read agyhub_summaries_proto.pb
                foreach (var gr in geminiRoots)
                {
                    string pbPath = Path.Combine(gr, "agyhub_summaries_proto.pb");
                    string pbBak = Path.Combine(gr, "agyhub_summaries_proto.pb.bak");
                    foreach (var p in new[] { pbPath, pbBak })
                    {
                        if (File.Exists(p) && new FileInfo(p).Length > 0)
                        {
                            try
                            {
                                byte[] pbuf = File.ReadAllBytes(p);
                                var repeatedFields = DecodeProto(pbuf);
                                foreach (var elem in repeatedFields)
                                {
                                    if (elem.FieldNum == 1 && elem.WireType == 2)
                                    {
                                        var sub = DecodeProto(elem.BytesValue);
                                        var idField = sub.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                        var metaField = sub.FirstOrDefault(x => x.FieldNum == 2 && x.WireType == 2);
                                        if (idField != null && metaField != null)
                                        {
                                            string id = Encoding.UTF8.GetString(idField.BytesValue);
                                            if (FindConvoFile(id) != null && !allSummaries.ContainsKey(id))
                                            {
                                                allSummaries[id] = DecodeProto(metaField.BytesValue);
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                // 4b. Read trajectorySummaries from global SQLite state.vscdb
                try
                {
                    var connStr = new SqliteConnectionStringBuilder
                    {
                        DataSource = globalDbPath,
                        Mode = SqliteOpenMode.ReadWrite,
                        DefaultTimeout = 5
                    }.ToString();

                    using (var conn = new SqliteConnection(connStr))
                    {
                        conn.Open();
                        using (var pragmaCmd = conn.CreateCommand())
                        {
                            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                            pragmaCmd.ExecuteNonQuery();
                        }

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'antigravityUnifiedStateSync.trajectorySummaries'";
                        var val = cmd.ExecuteScalar()?.ToString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            byte[] buf = Convert.FromBase64String(val);
                            var repeatedFields = DecodeProto(buf);
                            foreach (var elem in repeatedFields)
                            {
                                if (elem.FieldNum == 1 && elem.WireType == 2)
                                {
                                    var sub = DecodeProto(elem.BytesValue);
                                    var idField = sub.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                    var metaField = sub.FirstOrDefault(x => x.FieldNum == 2 && x.WireType == 2);
                                    if (idField != null && metaField != null)
                                    {
                                        string id = Encoding.UTF8.GetString(idField.BytesValue);
                                        if (FindConvoFile(id) != null && !allSummaries.ContainsKey(id))
                                        {
                                            var metaFields = DecodeProto(metaField.BytesValue);
                                            var subValueField = metaFields.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                            if (subValueField != null)
                                            {
                                                string b64Str = Encoding.UTF8.GetString(subValueField.BytesValue);
                                                byte[] innerBuf = Convert.FromBase64String(b64Str);
                                                allSummaries[id] = DecodeProto(innerBuf);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatRestorer] Error reading trajectorySummaries: " + ex.Message);
                }

                // 4c. Discover any remaining conversations directly from disk
                foreach (var gr in geminiRoots)
                {
                    string convDir = Path.Combine(gr, "conversations");
                    if (Directory.Exists(convDir))
                    {
                        foreach (var file in Directory.GetFiles(convDir))
                        {
                            string fn = Path.GetFileName(file);
                            if (fn.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || fn.EndsWith(".pb", StringComparison.OrdinalIgnoreCase))
                            {
                                string id = Path.GetFileNameWithoutExtension(fn);
                                long mtimeSec = 0;
                                try { mtimeSec = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds(); } catch { }
                                if (mtimeSec <= 0) mtimeSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                                if (!allSummaries.ContainsKey(id))
                                {
                                    string title = ExtractConversationTitle(id, geminiRoots);
                                    byte[] tsBuf = BuildTimestampMessage(mtimeSec);

                                    var mFields = new List<ProtoField>
                                    {
                                        new ProtoField { FieldNum = 1, WireType = 2, BytesValue = Encoding.UTF8.GetBytes(title) },
                                        new ProtoField { FieldNum = 2, WireType = 0, VarintValue = 20 },
                                        new ProtoField { FieldNum = 3, WireType = 2, BytesValue = tsBuf },
                                        new ProtoField { FieldNum = 4, WireType = 2, BytesValue = Encoding.UTF8.GetBytes(id) },
                                        new ProtoField { FieldNum = 5, WireType = 0, VarintValue = 1 },
                                        new ProtoField { FieldNum = 7, WireType = 2, BytesValue = BuildTimestampMessage(Math.Max(0, mtimeSec - 600)) },
                                        new ProtoField { FieldNum = 10, WireType = 2, BytesValue = tsBuf },
                                        new ProtoField { FieldNum = 22, WireType = 0, VarintValue = 4 }
                                    };
                                    allSummaries[id] = mFields;
                                }
                                else
                                {
                                    // If existing summary has generic/corrupted title, replace with real title
                                    var existingMeta = allSummaries[id];
                                    var tField = existingMeta.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                    string curTitle = tField != null ? Encoding.UTF8.GetString(tField.BytesValue) : "";
                                    if (IsGarbageTitle(curTitle))
                                    {
                                        string newTitle = ExtractConversationTitle(id, geminiRoots);
                                        if (!IsGarbageTitle(newTitle))
                                        {
                                            if (tField != null) tField.BytesValue = Encoding.UTF8.GetBytes(newTitle);
                                            else existingMeta.Insert(0, new ProtoField { FieldNum = 1, WireType = 2, BytesValue = Encoding.UTF8.GetBytes(newTitle) });
                                        }
                                    }
                                    // Ensure timestamp Field 3 exists
                                    if (!existingMeta.Any(x => x.FieldNum == 3))
                                    {
                                        existingMeta.Add(new ProtoField { FieldNum = 3, WireType = 2, BytesValue = BuildTimestampMessage(mtimeSec) });
                                    }
                                }
                            }
                        }
                    }
                }

                if (allSummaries.Count == 0)
                {
                    result.Success = true;
                    result.TotalRestoredCount = 0;
                    result.WorkspacesCount = 0;
                    result.Message = "Không có cuộc trò chuyện nào để khôi phục.";
                    return result;
                }

                // 5. Map each conversation to a Workspace (sorted by timestamp descending so newest are on top)
                var mappedConversations = new Dictionary<string, List<ChatIndexEntry>>();
                var pbElementList = new List<byte[]>();
                var sqliteElementList = new List<byte[]>();

                var sortedSummaries = allSummaries.OrderByDescending(kvp =>
                {
                    string? cf = FindConvoFile(kvp.Key);
                    long fallback = cf != null ? new DateTimeOffset(File.GetLastWriteTimeUtc(cf)).ToUnixTimeSeconds() : 0;
                    return GetTimestampFromMeta(kvp.Value, fallback);
                }).ToList();

                foreach (var kvp in sortedSummaries)
                {
                    string id = kvp.Key;
                    var metaProtoFields = kvp.Value;

                    string? convoFile = FindConvoFile(id);
                    long metaSec = GetTimestampFromMeta(metaProtoFields, 0);
                    long lastMessageDate = metaSec > 0
                        ? metaSec * 1000
                        : (convoFile != null
                            ? new DateTimeOffset(File.GetLastWriteTimeUtc(convoFile)).ToUnixTimeMilliseconds()
                            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                    WorkspaceInfo? matchedWorkspace = null;
                    string projectUri = "";

                    // Step A: First try reading true uncorrupted URI from .db file (trajectory_metadata_blob)
                    if (convoFile != null && convoFile.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    {
                        projectUri = GetOriginalUriFromConvDb(convoFile) ?? "";
                    }

                    // Step B: Try Field 17 (often preserves original project URI if Field 9 was rewritten)
                    if (string.IsNullOrEmpty(projectUri))
                    {
                        var f17 = metaProtoFields.FirstOrDefault(x => x.FieldNum == 17 && x.WireType == 2);
                        if (f17 != null)
                        {
                            projectUri = ExtractUriFromBytes(f17.BytesValue) ?? "";
                        }
                    }

                    // Step C: Try existing Field 9
                    if (string.IsNullOrEmpty(projectUri))
                    {
                        var projectPathField = metaProtoFields.FirstOrDefault(x => x.FieldNum == 9 && x.WireType == 2);
                        if (projectPathField != null)
                        {
                            var pathFields = DecodeProto(projectPathField.BytesValue);
                            var uriField = pathFields.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                            if (uriField != null)
                            {
                                projectUri = Encoding.UTF8.GetString(uriField.BytesValue);
                            }
                        }
                    }

                    // Step D: Match against workspaces using exact and hierarchy matching
                    if (!string.IsNullOrEmpty(projectUri))
                    {
                        matchedWorkspace = FindBestMatchingWorkspace(projectUri, workspaces);
                    }

                    // Step E: If not matched, scan transcript in brain folder
                    if (matchedWorkspace == null)
                    {
                        foreach (var gr in geminiRoots)
                        {
                            string logDir = Path.Combine(gr, "brain", id, ".system_generated", "logs");
                            string[] candidateLogs = [Path.Combine(logDir, "transcript.jsonl"), Path.Combine(logDir, "transcript_full.jsonl")];
                            foreach (var lp in candidateLogs)
                            {
                                if (File.Exists(lp))
                                {
                                    try
                                    {
                                        using var stream = File.OpenRead(lp);
                                        byte[] buf = new byte[Math.Min(16384, stream.Length)];
                                        int read = stream.Read(buf, 0, buf.Length);
                                        string sample = Encoding.UTF8.GetString(buf, 0, read);
                                        var mUri = System.Text.RegularExpressions.Regex.Match(sample, @"file:///[a-zA-Z0-9_\-\.\/%:]+");
                                        if (mUri.Success)
                                        {
                                            matchedWorkspace = FindBestMatchingWorkspace(mUri.Value, workspaces);
                                            if (matchedWorkspace != null) break;
                                        }

                                        string sampleLower = sample.ToLowerInvariant().Replace('\\', '/');
                                        foreach (var w in workspaces)
                                        {
                                            if (sampleLower.Contains(w.LocalPath))
                                            {
                                                matchedWorkspace = w;
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                if (matchedWorkspace != null) break;
                            }
                            if (matchedWorkspace != null) break;
                        }
                    }

                    WorkspaceInfo? targetWs = matchedWorkspace ?? defaultWorkspace;
                    string targetUri = targetWs != null ? targetWs.Uri : "";

                    // Extract title
                    string title = "";
                    var titleField = metaProtoFields.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                    if (titleField != null)
                    {
                        title = Encoding.UTF8.GetString(titleField.BytesValue);
                    }
                    if (IsGarbageTitle(title))
                    {
                        string realT = ExtractConversationTitle(id, geminiRoots);
                        if (!IsGarbageTitle(realT) || string.IsNullOrWhiteSpace(title))
                        {
                            title = realT;
                        }
                    }

                    // Re-encode metadata with preservation of fields and injection of target projectUri
                    var newInnerFieldBufs = new List<byte[]>();
                    bool field9Encoded = false;

                    foreach (var mf in metaProtoFields)
                    {
                        if (mf.FieldNum == 9 && !string.IsNullOrEmpty(targetUri))
                        {
                            var pathFields = DecodeProto(mf.BytesValue);
                            var pathBufs = new List<byte[]>();
                            foreach (var pf in pathFields)
                            {
                                if (pf.FieldNum == 1 || pf.FieldNum == 2)
                                {
                                    pathBufs.Add(EncodeString(pf.FieldNum, targetUri));
                                }
                                else if (pf.WireType == 2)
                                {
                                    pathBufs.Add(EncodeBytes(pf.FieldNum, pf.BytesValue));
                                }
                                else if (pf.WireType == 0)
                                {
                                    pathBufs.Add(EncodeVarintField(pf.FieldNum, pf.VarintValue));
                                }
                            }
                            if (!pathFields.Any(x => x.FieldNum == 1))
                                pathBufs.Add(EncodeString(1, targetUri));
                            if (!pathFields.Any(x => x.FieldNum == 2))
                                pathBufs.Add(EncodeString(2, targetUri));

                            newInnerFieldBufs.Add(EncodeBytes(9, ConcatBuffers(pathBufs.ToArray())));
                            field9Encoded = true;
                        }
                        else
                        {
                            if (mf.WireType == 2)
                                newInnerFieldBufs.Add(EncodeBytes(mf.FieldNum, mf.BytesValue));
                            else if (mf.WireType == 0)
                                newInnerFieldBufs.Add(EncodeVarintField(mf.FieldNum, mf.VarintValue));
                        }
                    }

                    if (!field9Encoded && !string.IsNullOrEmpty(targetUri))
                    {
                        byte[] p1 = EncodeString(1, targetUri);
                        byte[] p2 = EncodeString(2, targetUri);
                        newInnerFieldBufs.Add(EncodeBytes(9, ConcatBuffers(p1, p2)));
                    }

                    byte[] innerBuf = ConcatBuffers(newInnerFieldBufs.ToArray());

                    // Format for agyhub_summaries_proto.pb:
                    // field 1 = subfields (field 1 = id, field 2 = innerBuf)
                    byte[] pbSub = ConcatBuffers(EncodeString(1, id), EncodeBytes(2, innerBuf));
                    byte[] pbElement = EncodeBytes(1, pbSub);
                    pbElementList.Add(pbElement);

                    // Format for SQLite trajectorySummaries:
                    // field 1 = subfields (field 1 = id, field 2 = subValue (field 1 = b64(innerBuf)))
                    string innerB64 = Convert.ToBase64String(innerBuf);
                    byte[] sqliteMetaBuf = EncodeString(1, innerB64);
                    byte[] sqliteSub = ConcatBuffers(EncodeString(1, id), EncodeBytes(2, sqliteMetaBuf));
                    byte[] sqliteElement = EncodeBytes(1, sqliteSub);
                    sqliteElementList.Add(sqliteElement);

                    // Add to workspace chat index (ensures every conversation is mapped and visible)
                    if (targetWs != null)
                    {
                        string wsId = targetWs.Id;
                        if (!mappedConversations.ContainsKey(wsId))
                        {
                            mappedConversations[wsId] = new List<ChatIndexEntry>();
                        }
                        mappedConversations[wsId].Add(new ChatIndexEntry
                        {
                            SessionId = id,
                            Title = title,
                            LastMessageDate = lastMessageDate,
                            IsImported = false,
                            InitialLocation = "panel",
                            IsEmpty = false
                        });
                    }
                }

                // 6. Write rebuilt payloads
                byte[] finalAgyhubBuf = ConcatBuffers(pbElementList.ToArray());
                byte[] finalSqliteBuf = ConcatBuffers(sqliteElementList.ToArray());
                string finalSqliteBase64 = Convert.ToBase64String(finalSqliteBuf);

                // 6a. Write agyhub_summaries_proto.pb to all gemini roots
                foreach (var gr in geminiRoots)
                {
                    try
                    {
                        string pbPath = Path.Combine(gr, "agyhub_summaries_proto.pb");
                        string pbBak = Path.Combine(gr, "agyhub_summaries_proto.pb.bak");
                        if (File.Exists(pbPath))
                        {
                            try { File.Copy(pbPath, pbBak, overwrite: true); } catch { }
                        }
                        string dir = Path.GetDirectoryName(pbPath)!;
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(pbPath, finalAgyhubBuf);
                        result.Details.Add($"Đã cập nhật: {pbPath}");
                    }
                    catch (Exception ex)
                    {
                        result.Details.Add($"Lỗi ghi agyhub: {ex.Message}");
                    }
                }

                // 6b. Write trajectorySummaries to global SQLite state.vscdb and backup
                bool globalDbSuccess = false;
                string[] globalDbs = [globalDbPath, globalDbBackupPath];
                foreach (var dbP in globalDbs)
                {
                    if (File.Exists(dbP))
                    {
                        try
                        {
                            try { File.Copy(dbP, dbP + ".bak", overwrite: true); } catch { }
                            var connStr = new SqliteConnectionStringBuilder
                            {
                                DataSource = dbP,
                                Mode = SqliteOpenMode.ReadWrite,
                                DefaultTimeout = 5
                            }.ToString();

                            using var conn = new SqliteConnection(connStr);
                            conn.Open();

                            using (var pragmaCmd = conn.CreateCommand())
                            {
                                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                                pragmaCmd.ExecuteNonQuery();
                            }

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('antigravityUnifiedStateSync.trajectorySummaries', @val)";
                                cmd.Parameters.AddWithValue("@val", finalSqliteBase64);
                                cmd.ExecuteNonQuery();
                            }

                            // Also write global chatSessionStore index with ALL discovered sessions
                            var allEntriesDict = new Dictionary<string, ChatIndexEntry>();
                            foreach (var kvp in allSummaries)
                            {
                                string cid = kvp.Key;
                                string t = "";
                                var tf = kvp.Value.FirstOrDefault(x => x.FieldNum == 1 && x.WireType == 2);
                                if (tf != null) t = Encoding.UTF8.GetString(tf.BytesValue);
                                if (IsGarbageTitle(t))
                                {
                                    string realT = ExtractConversationTitle(cid, geminiRoots);
                                    if (!IsGarbageTitle(realT) || string.IsNullOrWhiteSpace(t))
                                    {
                                        t = realT;
                                    }
                                }

                                string? cf = FindConvoFile(cid);
                                long metaSecAll = GetTimestampFromMeta(kvp.Value, 0);
                                long lmd = metaSecAll > 0
                                    ? metaSecAll * 1000
                                    : (cf != null
                                        ? new DateTimeOffset(File.GetLastWriteTimeUtc(cf)).ToUnixTimeMilliseconds()
                                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                                allEntriesDict[cid] = new ChatIndexEntry
                                {
                                    SessionId = cid,
                                    Title = t,
                                    LastMessageDate = lmd,
                                    IsImported = false,
                                    InitialLocation = "panel",
                                    IsEmpty = false
                                };
                            }
                            var globalRoot = new ChatIndexRoot { Version = 1, Entries = allEntriesDict };
                            string globalJson = JsonConvert.SerializeObject(globalRoot, Formatting.None);

                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('chat.ChatSessionStore.index', @val)";
                                cmd.Parameters.AddWithValue("@val", globalJson);
                                cmd.ExecuteNonQuery();
                            }

                            using (var cpCmd = conn.CreateCommand())
                            {
                                cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                                cpCmd.ExecuteNonQuery();
                            }

                            globalDbSuccess = true;
                            result.Details.Add($"Đã cập nhật global DB: {Path.GetFileName(dbP)}");
                        }
                        catch (Exception ex)
                        {
                            result.Details.Add($"Lỗi ghi global DB ({Path.GetFileName(dbP)}): {ex.Message}");
                        }
                    }
                }

                // 6c. Re-index each workspace database in workspaceStorage
                int wsUpdatedCount = 0;
                var allWsIds = new HashSet<string>(workspaces.Select(w => w.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var k in mappedConversations.Keys) allWsIds.Add(k);

                foreach (var wsId in allWsIds)
                {
                    var entries = mappedConversations.TryGetValue(wsId, out var list) ? list : new List<ChatIndexEntry>();
                    string wsDir = Path.Combine(workspaceStorageDir, wsId);

                    var root = new ChatIndexRoot();
                    foreach (var entry in entries)
                    {
                        root.Entries[entry.SessionId] = entry;
                    }
                    string jsonValue = JsonConvert.SerializeObject(root, Formatting.None);

                    string[] wsDbPaths = [
                        Path.Combine(wsDir, "state.vscdb"),
                        Path.Combine(wsDir, "state.vscdb.backup")
                    ];

                    bool updated = false;
                    foreach (var wsDb in wsDbPaths)
                    {
                        if (File.Exists(wsDb))
                        {
                            try
                            {
                                var connStr = new SqliteConnectionStringBuilder
                                {
                                    DataSource = wsDb,
                                    Mode = SqliteOpenMode.ReadWrite,
                                    DefaultTimeout = 5
                                }.ToString();

                                using var conn = new SqliteConnection(connStr);
                                conn.Open();

                                using (var pragmaCmd = conn.CreateCommand())
                                {
                                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                                    pragmaCmd.ExecuteNonQuery();
                                }

                                using (var cmd = conn.CreateCommand())
                                {
                                    cmd.CommandText = "INSERT OR REPLACE INTO ItemTable (key, value) VALUES ('chat.ChatSessionStore.index', @val)";
                                    cmd.Parameters.AddWithValue("@val", jsonValue);
                                    cmd.ExecuteNonQuery();
                                }

                                using (var cpCmd = conn.CreateCommand())
                                {
                                    cpCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                                    cpCmd.ExecuteNonQuery();
                                }

                                updated = true;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ChatRestorer] Error updating {wsDb}: {ex.Message}");
                            }
                        }
                    }

                    if (updated && entries.Count > 0) wsUpdatedCount++;
                }

                result.Success = globalDbSuccess || wsUpdatedCount > 0;
                result.TotalRestoredCount = allSummaries.Count;
                result.WorkspacesCount = wsUpdatedCount;
                result.Message = result.Success
                    ? $"Đã khôi phục thành công {allSummaries.Count} cuộc trò chuyện vào {wsUpdatedCount} project!"
                    : "Không thể ghi dữ liệu khôi phục vào cơ sở dữ liệu.";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Lỗi khi khôi phục lịch sử chat: " + ex.Message;
                return result;
            }
        }
    }
}
