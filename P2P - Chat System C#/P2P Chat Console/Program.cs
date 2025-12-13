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
                    if (!string.IsNullOrEmpty(name)) existing.Name = name;
                }
                else
                {
                    list.Add(new ChatSession { SessionId = sessionId, Name = name, LastActive = DateTime.Now });
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
                if (p.SessionId != null)
                    UpsertSession(p.SessionId, p.Type == PacketType.Invite ? $"Chat with {p.SenderName}" : null);

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

        // --- SCREEN 2: DASHBOARD ---
        static string Screen_Dashboard()
        {
            _currentSessionId = null;
            while (true)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"=== DASHBOARD: {_currentUser.Username} ===");
                Console.ResetColor();
                Console.WriteLine("LENH:");
                Console.WriteLine(" - Tao moi:    N <Ten doan chat>");
                Console.WriteLine(" - Xoa data:   RESET");
                Console.WriteLine(" - Join Room:  J (Nhap IP, Port, RoomID)");
                Console.WriteLine("----------------------------------");

                var sessions = JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList();
                for (int i = 0; i < sessions.Count; i++)
                {
                    Console.WriteLine($"[{i + 1}] {sessions[i].Name} (ID: {sessions[i].SessionId.Substring(0, 4)}...)");
                }

                Console.WriteLine("----------------------------------");
                Console.Write("Nhap lua chon: ");
                string input = Console.ReadLine()?.Trim();

                if (string.IsNullOrEmpty(input)) continue;

                // Tạo mới
                if (input.StartsWith("N ", StringComparison.OrdinalIgnoreCase))
                {
                    string chatName = input.Substring(2).Trim();
                    string newId = Guid.NewGuid().ToString();
                    JsonManager.UpsertSession(newId, string.IsNullOrEmpty(chatName) ? "New Chat" : chatName);
                    return newId;
                }

                // Reset
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

                // [THAY ĐỔI] Join bằng ID + IP
                else if (input.Equals("J", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("De vao phong, ban can IP va Port cua chu phong.");
                    Console.Write("Nhap theo cu phap: <IP> <Port> <RoomID>: ");
                    string joinCmd = Console.ReadLine(); // VD: 192.168.1.5 9000 abc-123

                    var parts = joinCmd.Split(' ');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int p))
                    {
                        string targetId = parts[2];
                        // Lưu session trước để vào được phòng
                        JsonManager.UpsertSession(targetId, "Joining...");
                        // Thực hiện kết nối ngầm
                        _ = ConnectAndJoin(parts[0], p, targetId);

                        Console.WriteLine("Dang ket noi...");
                        Thread.Sleep(1000);
                        return targetId;
                    }
                    else Console.WriteLine("[LOI] Sai cu phap! Can: IP Port RoomID");
                    Thread.Sleep(2000);
                }

                // Chọn cũ
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

                // [THAY ĐỔI] Lệnh Join trong phòng chat
                if (input.StartsWith("/join "))
                {
                    var p = input.Split(' ');
                    if (p.Length == 4) // /join IP Port RoomID
                    {
                        await ConnectAndJoin(p[1], int.Parse(p[2]), p[3]);
                        // Nếu ID khác phòng hiện tại, user cần gõ /back để ra dashboard chọn lại, hoặc tự động switch?
                        // Ở đây ta giữ nguyên phòng hiện tại, chỉ thiết lập kết nối.
                        Console.WriteLine("[SYSTEM] Da ket noi them.");
                    }
                    else Console.WriteLine("Sai cu phap. Dung: /join <IP> <Port> <RoomID>");
                }
                // Các lệnh cũ
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

        // --- NETWORK & LOGIC ---
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

        // [MỚI] Hàm dùng chung cho Join và Invite
        static async Task ConnectAndJoin(string ip, int port, string targetSessionId)
        {
            try
            {
                TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                lock (_uiLock) _neighbors.Add(c);

                // Gửi gói Invite/Join để báo cho bên kia biết mình muốn vào phòng nào
                var packet = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(),
                    SessionId = targetSessionId,
                    Type = PacketType.Invite, // Dùng Invite như một gói tin "Hello"
                    SenderName = _currentUser.Username,
                    SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                    Content = "Hello! Minh muon tham gia phong nay.",
                    Timestamp = DateTime.Now
                };

                var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(packet));
                _ = Task.Run(() => HandleClient(c));
            }
            catch { Console.WriteLine($"[LOI KET NOI] Khong tim thay {ip}:{port}"); }
        }

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

        static void RedrawChat(string sessionId)
        {
            lock (_uiLock)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Yellow;
                // Hiển thị đầy đủ SessionID để user copy chia sẻ
                Console.WriteLine($"=== ROOM ID: {sessionId} ===");
                Console.WriteLine($"User: {_currentUser.Username} | IP: {GetLocalIP()} | Port: {_currentUser.Port}");
                Console.WriteLine("Commands: /back, /invite <IP> <Port>, /edit, /delete");
                Console.ResetColor();
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

        static string GetLocalIP()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            return "127.0.0.1";
        }
    }
}