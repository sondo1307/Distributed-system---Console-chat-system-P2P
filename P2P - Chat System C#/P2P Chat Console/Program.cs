using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace P2PFinalJson
{
    public static class ConsoleHelper
    {
        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZEBOX = 0x10000;
        private const int WS_THICKFRAME = 0x40000;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        public static void SetupWindow()
        {
            IntPtr handle = GetConsoleWindow();

            // 1. Vô hiệu hóa nút Maximize và kéo giãn (Resize)
            int style = GetWindowLong(handle, GWL_STYLE);
            style = style & ~WS_MAXIMIZEBOX; // Tắt nút Maximize
            style = style & ~WS_THICKFRAME;  // Tắt viền kéo giãn
            SetWindowLong(handle, GWL_STYLE, style);

            // 2. Đặt kích thước Pixel cố định (1500x750)
            // Tọa độ xuất hiện (200, 100) để không bị che khuất
            MoveWindow(handle, 200, 100, 1500, 750, true);
        }
    }

    // --- MODELS ---
    public enum PacketType { Hello, System, Message, Edit, Delete, Invite, Ping }

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

    // [MỚI] Class bạn bè
    public class Friend
    {
        public string Name { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsOnline { get; set; } // Chỉ dùng khi chạy (không lưu vào file)
    }

    public class ChatPacket
    {
        public string Id { get; set; }
        public string TargetId { get; set; }
        public string SessionId { get; set; }
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
        private static string DataFolder = "Data";
        private static string ConfigFile => Path.Combine(DataFolder, "config.json");
        private static string SessionsFile => Path.Combine(DataFolder, "sessions.json");
        private static string FriendsFile => Path.Combine(DataFolder, "friends.json"); // [MỚI]
        private static readonly object _fileLock = new object();

        public static void Initialize(string profileName = null)
        {
            if (!string.IsNullOrEmpty(profileName)) DataFolder = $"Data_{profileName}";
            if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder);
            if (!File.Exists(SessionsFile)) File.WriteAllText(SessionsFile, "[]");
            if (!File.Exists(FriendsFile)) File.WriteAllText(FriendsFile, "[]"); // [MỚI]
        }

        public static void DeleteAllData()
        {
            lock (_fileLock)
            {
                if (Directory.Exists(DataFolder)) { Directory.Delete(DataFolder, true); Initialize(); }
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
            lock (_fileLock) File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static List<ChatSession> LoadSessions()
        {
            lock (_fileLock)
            {
                if (!File.Exists(SessionsFile)) return new List<ChatSession>();
                try { return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(SessionsFile)) ?? new List<ChatSession>(); } catch { return new List<ChatSession>(); }
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
                    // Dòng này quan trọng: Không cho phép chữ "Joining..." ghi đè lên tên thật nếu đã có
                    if (!string.IsNullOrEmpty(name) && name != "Joining...") existing.Name = name;
                }
                else list.Add(new ChatSession { SessionId = sessionId, Name = name ?? "Unknown", LastActive = DateTime.Now });
                File.WriteAllText(SessionsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        public static List<Friend> LoadFriends()
        {
            lock (_fileLock)
            {
                if (!File.Exists(FriendsFile)) return new List<Friend>();
                try { return JsonSerializer.Deserialize<List<Friend>>(File.ReadAllText(FriendsFile)) ?? new List<Friend>(); } catch { return new List<Friend>(); }
            }
        }

        public static bool AddFriend(string name, string ip, int port)
        {
            lock (_fileLock)
            {
                var list = LoadFriends();

                // [MỚI] Kiểm tra trùng tên (Không phân biệt hoa thường)
                if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return false; // Trả về false báo lỗi
                }

                // Logic cũ: Xóa IP cũ nếu trùng để cập nhật
                list.RemoveAll(x => x.Ip == ip && x.Port == port);

                list.Add(new Friend { Name = name, Ip = ip, Port = port });
                File.WriteAllText(FriendsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));

                return true; // Trả về true báo thành công
            }
        }

        public static List<ChatPacket> GetMessages(string sessionId)
        {
            lock (_fileLock)
            {
                string path = Path.Combine(DataFolder, $"msg_{sessionId}.json");
                if (!File.Exists(path)) return new List<ChatPacket>();
                try { return JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>(); } catch { return new List<ChatPacket>(); }
            }
        }

        public static void HandlePacketStorage(ChatPacket p)
        {
            lock (_fileLock)
            {
                // Không lưu gói tin Ping vào lịch sử chat
                if (p.Type == PacketType.Ping) return;

                string sessionName = null;
                if (!string.IsNullOrEmpty(p.GroupName)) sessionName = p.GroupName;
                else if (p.Type == PacketType.Invite) sessionName = $"Chat with {p.SenderName}";

                if (p.SessionId != null) UpsertSession(p.SessionId, sessionName);

                string path = Path.Combine(DataFolder, $"msg_{p.SessionId}.json");
                List<ChatPacket> msgs = File.Exists(path) ? (JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>()) : new List<ChatPacket>();

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

        public static string GetSessionName(string sessionId)
        {
            var s = LoadSessions().FirstOrDefault(x => x.SessionId == sessionId);
            return s?.Name;
        }
    }

    class Program
    {
        static UserConfig _currentUser;
        static TcpListener _server;
        static List<TcpClient> _neighbors = new List<TcpClient>();
        static string _currentSessionId = null;
        static object _uiLock = new object();
        static List<ChatPacket> _currentViewMap = new List<ChatPacket>();

        static List<Friend> _myFriends = new List<Friend>();

        static async Task Main(string[] args)
        {
            ConsoleHelper.SetupWindow();

            Console.InputEncoding = Encoding.Unicode;
            Console.OutputEncoding = Encoding.Unicode;

            string profile = args.Length > 0 ? args[0] : null;
            JsonManager.Initialize(profile);
            Console.Title = profile != null ? $"P2P Chat - {profile}" : "P2P Chat";

            while (true)
            {
                if (_currentUser == null)
                {
                    await Screen_Login();
                    _ = Task.Run(() => StartServer());
                    // [MỚI] Bắt đầu chạy ngầm kiểm tra Online
                    _ = Task.Run(() => StartOnlineChecker());
                }

                string selectedSession = Screen_Dashboard();

                if (selectedSession == "RESET_APP") { _currentUser = null; continue; }

                if (!string.IsNullOrEmpty(selectedSession)) await Screen_ChatRoom(selectedSession);
            }
        }

        // --- CHECK ONLINE STATUS (BACKGROUND) ---
        static async Task StartOnlineChecker()
        {
            while (true)
            {
                if (_currentUser != null)
                {
                    // Load lại danh sách từ file để cập nhật nếu có thêm mới
                    _myFriends = JsonManager.LoadFriends();

                    foreach (var friend in _myFriends)
                    {
                        // Thử kết nối TCP nhẹ
                        try
                        {
                            using (var client = new TcpClient())
                            {
                                // Timeout ngắn (500ms) để không bị lag hệ thống
                                var connectTask = client.ConnectAsync(friend.Ip, friend.Port);
                                var timeoutTask = Task.Delay(500);

                                if (await Task.WhenAny(connectTask, timeoutTask) == connectTask && client.Connected)
                                {
                                    friend.IsOnline = true;

                                    // Gửi gói Ping nhẹ để bên kia không báo lỗi
                                    try
                                    {
                                        var ping = new ChatPacket { Type = PacketType.Ping };
                                        using var w = new StreamWriter(client.GetStream()) { AutoFlush = true };
                                        await w.WriteLineAsync(JsonSerializer.Serialize(ping));
                                    }
                                    catch { }
                                }
                                else
                                {
                                    friend.IsOnline = false;
                                }
                            }
                        }
                        catch
                        {
                            friend.IsOnline = false;
                        }
                    }
                }
                // Nghỉ 5 giây rồi check lại
                await Task.Delay(5000);
            }
        }

        // --- SCREEN 1: LOGIN ---
        static async Task Screen_Login()
        {
            while (true)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=== P2P CHAT ===");
                Console.ResetColor();

                var config = JsonManager.LoadConfig();
                if (config != null)
                {
                    Console.WriteLine($"Chào mừng {config.Username} (Port: {config.Port})");
                    Console.WriteLine("[1] Tiếp tục vào hệ thống | [2] Thay đổi thông tin");
                    if (Console.ReadLine() == "1") { _currentUser = config; return; }
                }

                Console.Write("Nhập tên: ");
                string name = Console.ReadLine();
                if (!name.Any(char.IsLetter))
                {
                    continue;
                }
                Console.Write("Nhập port (VD: 9000): ");
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
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"=== DASHBOARD: {_currentUser.Username} ===");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[INFO] IP: {GetLocalIP()} | PORT: {_currentUser.Port}");
                Console.ResetColor();
                Console.WriteLine("NEUTRAL COMMAND:");
                Console.WriteLine(" [N <Name of the group chat>] New group chat | [J] Join                    | [RESET] Reset all data | [Ctrl+R] Refresh");
                Console.WriteLine("PHONEBOOK COMMAND:");
                Console.WriteLine(" [F] Add friend/ contact                     | [L] List friends/ contacts");
                Console.WriteLine("-------------------------------------------------------------");

                var sessions = JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList();
                for (int i = 0; i < sessions.Count; i++)
                {
                    Console.WriteLine($"[{i + 1}] {sessions[i].Name} (ID: {sessions[i].SessionId.Substring(0, 4)}...)");
                }

                Console.WriteLine("-------------------------------------------------------------");
                Console.Write("> ");

                string input = ReadInputWithHotkeys();

                if (input == "CMD_REFRESH") continue;
                if (string.IsNullOrEmpty(input)) continue;

                // [MỚI] THÊM BẠN
                if (input.Equals("F", StringComparison.OrdinalIgnoreCase))
                {

                    Console.WriteLine("--- Add friend ---");
                    Console.Write("IP: "); string ip = Console.ReadLine();
                    Console.Write("Port: "); int.TryParse(Console.ReadLine(), out int p);
                    Console.Write("Nickname: "); string n = Console.ReadLine();

                    if (JsonManager.AddFriend(n, ip, p))
                    {
                        Console.WriteLine("Add to contacts success!");
                    }
                    else
                    {
                        Console.WriteLine("[ERROR] Name already exists! Please choose another name.");
                    }
                    Thread.Sleep(1000);
                }
                // [MỚI] DANH SÁCH BẠN
                else if (input.Equals("L", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Clear();
                    Console.WriteLine("--- List friends ---");
                    // _myFriends được cập nhật bởi background task
                    if (_myFriends.Count == 0) Console.WriteLine("(You have zero friend)");

                    foreach (var f in _myFriends)
                    {
                        Console.Write($"- {f.Name} ({f.Ip}:{f.Port}): ");
                        if (f.IsOnline)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("ONLINE");
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine("OFFLINE");
                        }
                        Console.ResetColor();
                    }
                    Console.WriteLine("\nPress Enter to back to DASHBOARD...");
                    Console.ReadLine();
                }

                // CÁC LỆNH CŨ
                else if (input.StartsWith("N ", StringComparison.OrdinalIgnoreCase))
                {
                    string chatName = input.Substring(2).Trim();
                    string newId = GenerateShortId();
                    JsonManager.UpsertSession(newId, string.IsNullOrEmpty(chatName) ? "New Chat" : chatName);
                    return newId;
                }
                else if (input.Equals("RESET", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("CONFIRM DELETING DATA? (Y/N): ");
                    if (Console.ReadLine()?.ToUpper() == "Y") { JsonManager.DeleteAllData(); return "RESET_APP"; }
                }
                else if (input.Equals("J", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("Input: <IP> <Port> <RoomID>: ");
                    string joinCmd = Console.ReadLine();
                    var parts = joinCmd.Split(' ');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int p))
                    {
                        string targetId = parts[2];
                        JsonManager.UpsertSession(targetId, "Joining...");
                        _ = ConnectAndJoin(parts[0], p, targetId);
                        Console.WriteLine("Connecting...");
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

                // [MỚI] Mời nhanh bạn bè từ danh bạ
                // Cú pháp: /invitefriend <Tên> (Tìm trong danh bạ để lấy IP/Port và mời)
                if (input.StartsWith("/invitefriend "))
                {
                    string friendName = input.Substring(14).Trim();
                    var friend = _myFriends.FirstOrDefault(f => f.Name.Equals(friendName, StringComparison.OrdinalIgnoreCase));
                    if (friend != null)
                    {
                        await ConnectAndJoin(friend.Ip, friend.Port, sessionId);
                        Console.WriteLine($"[SYSTEM] Invited {friend.Name} ({friend.Ip}:{friend.Port})");
                    }
                    else Console.WriteLine("[ERROR] Cannot find friend in contact list.");
                }

                else if (input.StartsWith("/join "))
                {
                    var p = input.Split(' ');
                    if (p.Length == 4) await ConnectAndJoin(p[1], int.Parse(p[2]), p[3]);
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

        // --- NETWORK & UTILS ---
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

                    if (packet.Type == PacketType.Ping) continue;

                    // [ĐOẠN CODE MỚI THÊM VÀO] ----------------------------------------
                    // Nếu nhận được Invite (người khác Join vào), hãy gửi lại tên phòng cho họ biết
                    if (packet.Type == PacketType.Invite)
                    {
                        string realName = JsonManager.GetSessionName(packet.SessionId);
                        // Chỉ gửi nếu mình đang có tên xịn (khác Joining...)
                        if (!string.IsNullOrEmpty(realName) && realName != "Joining...")
                        {
                            var replyPacket = new ChatPacket
                            {
                                Id = Guid.NewGuid().ToString(),
                                SessionId = packet.SessionId,
                                GroupName = realName, // <--- Gửi tên phòng chuẩn về cho người mới
                                Type = PacketType.System,
                                SenderName = _currentUser.Username,
                                SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                                Content = $"Welcome to {realName}!", // Tin nhắn chào mừng
                                Timestamp = DateTime.Now
                            };

                            // Gửi phản hồi ngay lập tức
                            try
                            {
                                var w = new StreamWriter(client.GetStream()) { AutoFlush = true };
                                await w.WriteLineAsync(JsonSerializer.Serialize(replyPacket));
                            }
                            catch { }
                        }
                    }
                    // ------------------------------------------------------------------

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

        static async Task ConnectAndJoin(string ip, int port, string targetSessionId)
        {
            try
            {
                TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                lock (_uiLock) _neighbors.Add(c);
                string myGroupName = JsonManager.GetSessionName(targetSessionId);
                var packet = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(),
                    SessionId = targetSessionId,
                    GroupName = myGroupName,
                    Type = PacketType.Invite,
                    SenderName = _currentUser.Username,
                    SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                    Content = "Hello! I am added you to this group",
                    Timestamp = DateTime.Now
                };
                var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(packet));
                _ = Task.Run(() => HandleClient(c));
            }
            catch { Console.WriteLine($"[ERROR] Connection error to {ip}:{port}"); }
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
                else Console.WriteLine("[ERROR] You ONLY allow to edit your message.");
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
                else Console.WriteLine("[ERROR] You ONLY allow to delete your message.");
            }
        }

        static void RedrawChat(string sessionId)
        {
            lock (_uiLock)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"=== ROOM ID: {sessionId} ===");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[MY INFO] IP: {GetLocalIP()} | Port: {_currentUser.Port}");
                Console.ResetColor();
                Console.WriteLine("Commands: /back, /invite <IP> <Port>, /edit, /delete, /invitefriend <Nickname>");
                Console.WriteLine("---------------------------------------------------------");
                var msgs = JsonManager.GetMessages(sessionId).OrderBy(x => x.Timestamp).ToList();
                _currentViewMap.Clear();
                int idx = 0;
                foreach (var m in msgs)
                {
                    _currentViewMap.Add(m); idx++;
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

        static string ReadInputWithHotkeys()
        {
            StringBuilder buffer = new StringBuilder();
            while (true)
            {
                var keyInfo = Console.ReadKey(true);
                if (keyInfo.Modifiers == ConsoleModifiers.Control && keyInfo.Key == ConsoleKey.R) return "CMD_REFRESH";
                if (keyInfo.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
                if (keyInfo.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) { buffer.Length--; Console.Write("\b \b"); } }
                else if (!char.IsControl(keyInfo.KeyChar)) { buffer.Append(keyInfo.KeyChar); Console.Write(keyInfo.KeyChar); }
            }
        }

        static string GetLocalIP()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            return "127.0.0.1";
        }

        // [MỚI] Hàm tạo ID ngắn 6 ký tự (Chữ in hoa + Số)
        private static string GenerateShortId()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            Random random = new Random();
            return new string(Enumerable.Repeat(chars, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }
}