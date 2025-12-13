using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace P2PFinalJson
{
    // --- MODELS ---
    public enum PacketType { Hello, System, Message, Edit, Delete, Invite }

    public class UserConfig
    {
        public string Username { get; set; }
        public int Port { get; set; }
    }

    public class ChatSession
    {
        public string SessionId { get; set; }
        public string Name { get; set; }
        public DateTime LastActive { get; set; }
    }

    public class ChatPacket
    {
        public string Id { get; set; }
        public string TargetId { get; set; }
        public string SessionId { get; set; }

        // [MỚI] Thêm trường này để đồng bộ tên nhóm
        public string GroupName { get; set; }

        public PacketType Type { get; set; }
        public string SenderName { get; set; }
        public string SenderInfo { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // --- JSON MANAGER ---
    public static class JsonManager
    {
        private static readonly string DataFolder = "Data";
        private static readonly string ConfigFile = Path.Combine(DataFolder, "config.json");
        private static readonly string SessionsFile = Path.Combine(DataFolder, "sessions.json");
        private static readonly object _fileLock = new object();

        public static void Initialize()
        {
            if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder);
            if (!File.Exists(SessionsFile)) File.WriteAllText(SessionsFile, "[]");
        }

        public static void DeleteAllData()
        {
            lock (_fileLock)
            {
                if (Directory.Exists(DataFolder))
                {
                    Directory.Delete(DataFolder, true);
                    Initialize();
                }
            }
        }

        public static UserConfig LoadConfig()
        {
            lock (_fileLock)
            {
                if (!File.Exists(ConfigFile)) return null;
                try { return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(ConfigFile)); } catch { return null; }
            }
        }

        public static void SaveConfig(UserConfig config)
        {
            lock (_fileLock)
            {
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public static List<ChatSession> LoadSessions()
        {
            lock (_fileLock)
            {
                if (!File.Exists(SessionsFile)) return new List<ChatSession>();
                try { return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(SessionsFile)) ?? new List<ChatSession>(); }
                catch { return new List<ChatSession>(); }
            }
        }

        public static void UpsertSession(string sessionId, string name)
        {
            lock (_fileLock)
            {
                var list = LoadSessions();
                var existing = list.FirstOrDefault(s => s.SessionId == sessionId);
                if (existing != null)
                {
                    existing.LastActive = DateTime.Now;
                    // Chỉ cập nhật tên nếu tên mới hợp lệ và không phải mặc định
                    if (!string.IsNullOrEmpty(name) && name != "Joining...") existing.Name = name;
                }
                else
                {
                    list.Add(new ChatSession { SessionId = sessionId, Name = name ?? "Unknown Chat", LastActive = DateTime.Now });
                }
                File.WriteAllText(SessionsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public static List<ChatPacket> GetMessages(string sessionId)
        {
            lock (_fileLock)
            {
                string path = Path.Combine(DataFolder, $"msg_{sessionId}.json");
                if (!File.Exists(path)) return new List<ChatPacket>();
                try { return JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>(); }
                catch { return new List<ChatPacket>(); }
            }
        }

        public static void HandlePacketStorage(ChatPacket p)
        {
            lock (_fileLock)
            {
                // [MỚI] Đồng bộ tên nhóm: Nếu gói tin có GroupName (từ Invite), cập nhật tên session
                string sessionName = null;
                if (!string.IsNullOrEmpty(p.GroupName)) sessionName = p.GroupName;
                else if (p.Type == PacketType.Invite) sessionName = $"Chat with {p.SenderName}";

                if (p.SessionId != null)
                    UpsertSession(p.SessionId, sessionName);

                // Lưu tin nhắn
                string path = Path.Combine(DataFolder, $"msg_{p.SessionId}.json");
                List<ChatPacket> msgs = File.Exists(path)
                    ? (JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>())
                    : new List<ChatPacket>();

                if (p.Type == PacketType.Message || p.Type == PacketType.Invite || p.Type == PacketType.System)
                {
                    if (!msgs.Any(x => x.Id == p.Id)) msgs.Add(p);
                }
                else if (p.Type == PacketType.Edit)
                {
                    var target = msgs.FirstOrDefault(x => x.Id == p.TargetId);
                    if (target != null) target.Content = p.Content + " (edited)";
                }
                else if (p.Type == PacketType.Delete)
                {
                    var target = msgs.FirstOrDefault(x => x.Id == p.TargetId);
                    if (target != null) msgs.Remove(target);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        // Helper để lấy tên session hiện tại (dùng khi gửi Invite)
        public static string GetSessionName(string sessionId)
        {
            var s = LoadSessions().FirstOrDefault(x => x.SessionId == sessionId);
            return s?.Name;
        }
    }

    // --- MAIN PROGRAM ---
    class Program
    {
        static UserConfig _currentUser;
        static TcpListener _server;
        static List<TcpClient> _neighbors = new List<TcpClient>();
        static string _currentSessionId = null;
        static object _uiLock = new object();
        static List<ChatPacket> _currentViewMap = new List<ChatPacket>();

        static async Task Main(string[] args)
        {
            Console.InputEncoding = Encoding.Unicode;
            Console.OutputEncoding = Encoding.Unicode;
            JsonManager.Initialize();

            while (true)
            {
                if (_currentUser == null)
                {
                    await Screen_Login();
                    _ = Task.Run(() => StartServer());
                }

                string selectedSession = Screen_Dashboard();

                if (selectedSession == "RESET_APP")
                {
                    _currentUser = null;
                    continue;
                }

                if (!string.IsNullOrEmpty(selectedSession))
                {
                    await Screen_ChatRoom(selectedSession);
                }
            }
        }

        // --- SCREEN 1: LOGIN ---
        static async Task Screen_Login()
        {
            while (true)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=== P2P CHAT PRO (JSON STORE) ===");
                Console.ResetColor();

                var config = JsonManager.LoadConfig();
                if (config != null)
                {
                    Console.WriteLine($"Chao mung {config.Username} (Port: {config.Port})");
                    Console.WriteLine("[1] Tiep tuc | [2] Doi thong tin");
                    var k = Console.ReadLine();
                    if (k == "1") { _currentUser = config; return; }
                }

                Console.WriteLine("--- THIET LAP MOI ---");
                Console.Write("Nhap Ten: ");
                string name = Console.ReadLine();
                Console.Write("Nhap Port (VD: 9000): ");
                if (int.TryParse(Console.ReadLine(), out int port))
                {
                    _currentUser = new UserConfig { Username = name, Port = port };
                    JsonManager.SaveConfig(_currentUser);
                    return;
                }
            }
        }

        // --- SCREEN 2: DASHBOARD (CÓ IP/PORT & REFRESH) ---
        static string Screen_Dashboard()
        {
            _currentSessionId = null;
            while (true)
            {
                Console.Clear();

                // [MỚI] Hiển thị thông tin kết nối ngay đầu Dashboard
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"=== DASHBOARD: {_currentUser.Username} ===");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[INFO] IP: {GetLocalIP()} | PORT: {_currentUser.Port}");
                Console.ResetColor();

                Console.WriteLine("LENH: [N <Ten>] Tao moi | [J] Join ID | [RESET] Xoa data | [Ctrl+R] Refresh");
                Console.WriteLine("-------------------------------------------------------------");

                var sessions = JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList();
                for (int i = 0; i < sessions.Count; i++)
                {
                    Console.WriteLine($"[{i + 1}] {sessions[i].Name} (ID: {sessions[i].SessionId.Substring(0, 4)}...)");
                }

                Console.WriteLine("-------------------------------------------------------------");
                Console.Write("> ");

                // [MỚI] Dùng hàm nhập liệu tùy chỉnh để bắt phím Ctrl+R
                string input = ReadInputWithHotkeys();

                if (input == "CMD_REFRESH") continue; // Refresh vòng lặp để vẽ lại
                if (string.IsNullOrEmpty(input)) continue;

                // Logic cũ...
                if (input.StartsWith("N ", StringComparison.OrdinalIgnoreCase))
                {
                    string chatName = input.Substring(2).Trim();
                    string newId = Guid.NewGuid().ToString();
                    JsonManager.UpsertSession(newId, string.IsNullOrEmpty(chatName) ? "New Chat" : chatName);
                    return newId;
                }

                if (input.Equals("RESET", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("XOA SACH DU LIEU? (Y/N): ");
                    if (Console.ReadLine()?.ToUpper() == "Y")
                    {
                        JsonManager.DeleteAllData();
                        return "RESET_APP";
                    }
                    Console.ResetColor();
                }

                else if (input.Equals("J", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("Nhap: <IP> <Port> <RoomID>: ");
                    string joinCmd = Console.ReadLine();

                    var parts = joinCmd.Split(' ');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int p))
                    {
                        string targetId = parts[2];
                        JsonManager.UpsertSession(targetId, "Joining...");
                        _ = ConnectAndJoin(parts[0], p, targetId);

                        Console.WriteLine("Dang ket noi...");
                        Thread.Sleep(1000);
                        return targetId;
                    }
                }

                else if (int.TryParse(input, out int idx) && idx > 0 && idx <= sessions.Count)
                {
                    return sessions[idx - 1].SessionId;
                }
            }
        }

        // --- SCREEN 3: CHAT ROOM ---
        static async Task Screen_ChatRoom(string sessionId)
        {
            _currentSessionId = sessionId;
            RedrawChat(sessionId);

            while (true)
            {
                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;

                if (input == "/back") return;

                if (input.StartsWith("/join "))
                {
                    var p = input.Split(' ');
                    if (p.Length == 4)
                    {
                        await ConnectAndJoin(p[1], int.Parse(p[2]), p[3]);
                        Console.WriteLine("[SYSTEM] Da gui tin nhan ket noi.");
                    }
                }
                else if (input.StartsWith("/invite "))
                {
                    var p = input.Split(' ');
                    if (p.Length == 3) await ConnectAndJoin(p[1], int.Parse(p[2]), sessionId);
                }
                else if (input.StartsWith("/edit "))
                {
                    var parts = input.Split(' ', 3);
                    if (parts.Length == 3 && int.TryParse(parts[1], out int index))
                        HandleEditCommand(index, parts[2], sessionId);
                }
                else if (input.StartsWith("/delete "))
                {
                    var parts = input.Split(' ');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int index))
                        HandleDeleteCommand(index, sessionId);
                }
                else
                {
                    var packet = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        SessionId = sessionId,
                        Type = PacketType.Message,
                        SenderName = _currentUser.Username,
                        SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                        Content = input,
                        Timestamp = DateTime.Now
                    };
                    ProcessPacket(packet);
                    Broadcast(packet);
                }
            }
        }

        // --- NETWORK HANDLERS ---
        static void StartServer()
        {
            try
            {
                if (_server == null)
                {
                    _server = new TcpListener(IPAddress.Any, _currentUser.Port);
                    _server.Start();
                    _ = Task.Run(async () => {
                        while (true)
                        {
                            try
                            {
                                var client = await _server.AcceptTcpClientAsync();
                                lock (_uiLock) _neighbors.Add(client);
                                _ = Task.Run(() => HandleClient(client));
                            }
                            catch { break; }
                        }
                    });
                }
            }
            catch { }
        }

        static async Task HandleClient(TcpClient client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream());
                while (client.Connected)
                {
                    string json = await reader.ReadLineAsync();
                    if (json == null) break;
                    var packet = JsonSerializer.Deserialize<ChatPacket>(json);
                    ProcessPacket(packet);
                }
            }
            catch { }
            finally { lock (_uiLock) _neighbors.Remove(client); }
        }

        static void ProcessPacket(ChatPacket p)
        {
            JsonManager.HandlePacketStorage(p);
            // Nếu đang mở đúng phòng chat thì vẽ lại
            if (_currentSessionId == p.SessionId) RedrawChat(p.SessionId);
        }

        static void Broadcast(ChatPacket p)
        {
            string json = JsonSerializer.Serialize(p);
            lock (_uiLock)
            {
                foreach (var c in _neighbors)
                {
                    try
                    {
                        var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                        w.WriteLine(json);
                    }
                    catch { }
                }
            }
        }

        static async Task ConnectAndJoin(string ip, int port, string targetSessionId)
        {
            try
            {
                TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                lock (_uiLock) _neighbors.Add(c);

                // [MỚI] Lấy tên nhóm hiện tại để gửi kèm
                string myGroupName = JsonManager.GetSessionName(targetSessionId);

                var packet = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(),
                    SessionId = targetSessionId,
                    GroupName = myGroupName, // Gửi kèm tên nhóm
                    Type = PacketType.Invite,
                    SenderName = _currentUser.Username,
                    SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                    Content = "Hello! Minh muon tham gia.",
                    Timestamp = DateTime.Now
                };

                var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(packet));
                _ = Task.Run(() => HandleClient(c));
            }
            catch { Console.WriteLine($"[LOI KET NOI] Khong tim thay {ip}:{port}"); }
        }

        // --- EDIT / DELETE LOGIC ---
        static void HandleEditCommand(int index, string newContent, string sessionId)
        {
            if (index > 0 && index <= _currentViewMap.Count)
            {
                var target = _currentViewMap[index - 1];
                if (target.SenderName == _currentUser.Username && target.SenderInfo.Contains(_currentUser.Port.ToString()))
                {
                    var p = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        TargetId = target.Id,
                        SessionId = sessionId,
                        Type = PacketType.Edit,
                        SenderName = _currentUser.Username,
                        SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                        Content = newContent,
                        Timestamp = DateTime.Now
                    };
                    ProcessPacket(p); Broadcast(p);
                }
                else Console.WriteLine("[LOI] Chi duoc sua tin cua minh.");
            }
        }

        static void HandleDeleteCommand(int index, string sessionId)
        {
            if (index > 0 && index <= _currentViewMap.Count)
            {
                var target = _currentViewMap[index - 1];
                if (target.SenderName == _currentUser.Username && target.SenderInfo.Contains(_currentUser.Port.ToString()))
                {
                    var p = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        TargetId = target.Id,
                        SessionId = sessionId,
                        Type = PacketType.Delete,
                        SenderName = _currentUser.Username,
                        SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                        Timestamp = DateTime.Now
                    };
                    ProcessPacket(p); Broadcast(p);
                }
                else Console.WriteLine("[LOI] Chi duoc xoa tin cua minh.");
            }
        }

        // --- UI UTILS ---
        static void RedrawChat(string sessionId)
        {
            lock (_uiLock)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                // [MỚI] Hiển thị rõ IP/Port trong phòng chat
                Console.WriteLine($"=== ROOM ID: {sessionId} ===");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[MY INFO] IP: {GetLocalIP()} | Port: {_currentUser.Port}");
                Console.ResetColor();
                Console.WriteLine("Commands: /back, /invite <IP> <Port>, /edit, /delete");
                Console.WriteLine("---------------------------------------------------------");

                var msgs = JsonManager.GetMessages(sessionId).OrderBy(x => x.Timestamp).ToList();
                _currentViewMap.Clear();

                int idx = 0;
                foreach (var m in msgs)
                {
                    _currentViewMap.Add(m);
                    idx++;
                    Console.ForegroundColor = ConsoleColor.Cyan; Console.Write($"[{idx}] ");
                    if (m.Type == PacketType.Invite) { Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine($"[SYSTEM] {m.SenderName}: {m.Content}"); }
                    else if (m.SenderName == _currentUser.Username && m.SenderInfo.Contains(_currentUser.Port.ToString())) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"You: {m.Content}"); }
                    else { Console.ForegroundColor = ConsoleColor.White; Console.WriteLine($"{m.SenderName}: {m.Content}"); }
                }
                Console.ResetColor();
                Console.WriteLine("---------------------------------------------------------");
                Console.Write("> ");
            }
        }

        // [MỚI] Hàm nhập liệu xử lý phím tắt (Ctrl+R)
        static string ReadInputWithHotkeys()
        {
            StringBuilder buffer = new StringBuilder();
            while (true)
            {
                var keyInfo = Console.ReadKey(true); // Đọc phím không hiện ra màn hình

                // Xử lý Ctrl + R
                if (keyInfo.Modifiers == ConsoleModifiers.Control && keyInfo.Key == ConsoleKey.R)
                {
                    return "CMD_REFRESH";
                }

                // Xử lý Enter
                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return buffer.ToString();
                }

                // Xử lý Backspace
                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b"); // Xóa ký tự trên màn hình
                    }
                }
                // Xử lý ký tự thường
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    buffer.Append(keyInfo.KeyChar);
                    Console.Write(keyInfo.KeyChar);
                }
            }
        }

        static string GetLocalIP()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            return "127.0.0.1";
        }
    }
}