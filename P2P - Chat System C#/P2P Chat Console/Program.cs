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

namespace P2PFinalJson
{
    public static class ConsoleHelper
    {
        private const int GWL_STYLE = -16;
        private const int WS_MAXIMIZEBOX = 0x10000;
        private const int WS_THICKFRAME = 0x40000;
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        public static void SetupWindow()
        {
            IntPtr handle = GetConsoleWindow();
            if (handle == IntPtr.Zero) return;
            int style = GetWindowLong(handle, GWL_STYLE);
            style = style & ~WS_MAXIMIZEBOX;
            style = style & ~WS_THICKFRAME;
            SetWindowLong(handle, GWL_STYLE, style);
            MoveWindow(handle, 200, 100, 1500, 750, true);
        }
    }

    // --- MODELS ---
    public enum PacketType { Hello, System, Message, Edit, Delete, Invite, Ping, FriendReq, FriendRes }
    public class UserConfig { public string Username { get; set; } public int Port { get; set; } }
    public class ChatSession { public string SessionId { get; set; } public string Name { get; set; } public DateTime LastActive { get; set; } }
    public class Friend { public string Name { get; set; } public string Ip { get; set; } public int Port { get; set; } [System.Text.Json.Serialization.JsonIgnore] public bool IsOnline { get; set; } }
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
        private static string FriendsFile => Path.Combine(DataFolder, "friends.json");
        private static readonly object _fileLock = new object();
        public static void Initialize(string profileName = null)
        {
            if (!string.IsNullOrEmpty(profileName)) DataFolder = $"Data_{profileName}";
            if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder);
            if (!File.Exists(SessionsFile)) File.WriteAllText(SessionsFile, "[]");
            if (!File.Exists(FriendsFile)) File.WriteAllText(FriendsFile, "[]");
        }
        public static void DeleteAllData() { lock (_fileLock) { if (Directory.Exists(DataFolder)) { Directory.Delete(DataFolder, true); Initialize(); } } }
        public static UserConfig LoadConfig() { lock (_fileLock) { if (!File.Exists(ConfigFile)) return null; try { return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(ConfigFile)); } catch { return null; } } }
        public static void SaveConfig(UserConfig config) { lock (_fileLock) File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })); }
        public static List<ChatSession> LoadSessions() { lock (_fileLock) { if (!File.Exists(SessionsFile)) return new List<ChatSession>(); try { return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(SessionsFile)) ?? new List<ChatSession>(); } catch { return new List<ChatSession>(); } } }
        public static void UpsertSession(string sessionId, string name)
        {
            lock (_fileLock)
            {
                var list = LoadSessions();
                var existing = list.FirstOrDefault(s => s.SessionId == sessionId);
                if (existing != null) { existing.LastActive = DateTime.Now; if (!string.IsNullOrEmpty(name) && name != "Joining...") existing.Name = name; }
                else list.Add(new ChatSession { SessionId = sessionId, Name = name ?? "Unknown", LastActive = DateTime.Now });
                File.WriteAllText(SessionsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        public static List<Friend> LoadFriends() { lock (_fileLock) { if (!File.Exists(FriendsFile)) return new List<Friend>(); try { return JsonSerializer.Deserialize<List<Friend>>(File.ReadAllText(FriendsFile)) ?? new List<Friend>(); } catch { return new List<Friend>(); } } }
        public static bool AddFriend(string name, string ip, int port)
        {
            lock (_fileLock)
            {
                var list = LoadFriends();
                if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
                list.RemoveAll(x => x.Ip == ip && x.Port == port);
                list.Add(new Friend { Name = name, Ip = ip, Port = port });
                File.WriteAllText(FriendsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
        }
        public static List<ChatPacket> GetMessages(string sessionId) { lock (_fileLock) { string path = Path.Combine(DataFolder, $"msg_{sessionId}.json"); if (!File.Exists(path)) return new List<ChatPacket>(); try { return JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>(); } catch { return new List<ChatPacket>(); } } }
        public static void HandlePacketStorage(ChatPacket p)
        {
            lock (_fileLock)
            {
                if (p.Type == PacketType.Ping || p.Type == PacketType.FriendReq || p.Type == PacketType.FriendRes) return;
                string sessionName = null;
                if (!string.IsNullOrEmpty(p.GroupName)) sessionName = p.GroupName;
                else if (p.Type == PacketType.Invite) sessionName = $"Chat with {p.SenderName}";
                if (p.SessionId != null) UpsertSession(p.SessionId, sessionName);
                string path = Path.Combine(DataFolder, $"msg_{p.SessionId}.json");
                List<ChatPacket> msgs = File.Exists(path) ? (JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>()) : new List<ChatPacket>();
                if (p.Type == PacketType.Message || p.Type == PacketType.Invite || p.Type == PacketType.System) { if (!msgs.Any(x => x.Id == p.Id)) msgs.Add(p); }
                else if (p.Type == PacketType.Edit) { var target = msgs.FirstOrDefault(x => x.Id == p.TargetId); if (target != null) target.Content = p.Content + " (edited)"; }
                else if (p.Type == PacketType.Delete) { var target = msgs.FirstOrDefault(x => x.Id == p.TargetId); if (target != null) msgs.Remove(target); }
                File.WriteAllText(path, JsonSerializer.Serialize(msgs, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        public static string GetSessionName(string sessionId) { var s = LoadSessions().FirstOrDefault(x => x.SessionId == sessionId); return s?.Name; }
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
        static ChatPacket _pendingFriendReq = null;

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
                    _ = Task.Run(() => StartOnlineChecker());
                }
                string selectedSession = await Screen_Dashboard();
                if (selectedSession == "RESET_APP") { _currentUser = null; continue; }
                if (!string.IsNullOrEmpty(selectedSession)) await Screen_ChatRoom(selectedSession);
            }
        }

        static async Task StartOnlineChecker() { /* ... */ while (true) { if (_currentUser != null) { _myFriends = JsonManager.LoadFriends(); foreach (var friend in _myFriends) { try { using (var client = new TcpClient()) { var connectTask = client.ConnectAsync(friend.Ip, friend.Port); var timeoutTask = Task.Delay(500); if (await Task.WhenAny(connectTask, timeoutTask) == connectTask && client.Connected) { friend.IsOnline = true; try { var ping = new ChatPacket { Type = PacketType.Ping }; using var w = new StreamWriter(client.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(ping)); } catch { } } else friend.IsOnline = false; } } catch { friend.IsOnline = false; } } } await Task.Delay(5000); } }
        static async Task Screen_Login() { /* ... */ while (true) { Console.Clear(); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("=== P2P CHAT ==="); Console.ResetColor(); var config = JsonManager.LoadConfig(); if (config != null) { Console.WriteLine($"Welcome {config.Username} (Port: {config.Port})"); Console.WriteLine("[1] Login | [2] Change Info"); if (Console.ReadLine() == "1") { _currentUser = config; return; } } Console.Write("Name: "); string name = Console.ReadLine(); if (!name.Any(char.IsLetter)) continue; Console.Write("Port (e.g., 9000): "); if (int.TryParse(Console.ReadLine(), out int port)) { _currentUser = new UserConfig { Username = name, Port = port }; JsonManager.SaveConfig(_currentUser); return; } } }

        // [DASHBOARD - Đã sửa lỗi Join]
        static async Task<string> Screen_Dashboard()
        {
            _currentSessionId = null;
            while (true)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"=== DASHBOARD: {_currentUser.Username} ===");
                Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"[INFO] IP: {GetLocalIP()} | PORT: {_currentUser.Port}"); Console.ResetColor();
                Console.WriteLine("NEUTRAL COMMAND: [N] New Chat | [J] Join | [RESET] Reset Data | [Ctrl+R] Refresh");
                Console.WriteLine("PHONEBOOK COMMAND: [F] Add friend | [L] List friends");

                if (_pendingFriendReq != null) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"[ALERT] {_pendingFriendReq.SenderInfo} ({_pendingFriendReq.SenderName}) want to add friend (Y/N)."); Console.ResetColor(); }
                Console.WriteLine("-------------------------------------------------------------");
                var sessions = JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList();
                for (int i = 0; i < sessions.Count; i++) Console.WriteLine($"[{i + 1}] {sessions[i].Name} (ID: {sessions[i].SessionId})");
                Console.Write("> ");

                string input = ReadInputWithHotkeys();
                if (input == "CMD_REFRESH") continue; if (string.IsNullOrEmpty(input)) continue;

                if (_pendingFriendReq != null) { if (input.Equals("Y", StringComparison.OrdinalIgnoreCase)) { var parts = _pendingFriendReq.SenderInfo.Split(':'); if (parts.Length == 2) { JsonManager.AddFriend(_pendingFriendReq.SenderName, parts[0], int.Parse(parts[1])); Console.WriteLine($"Added {_pendingFriendReq.SenderName}."); _ = SendFriendResponse(parts[0], int.Parse(parts[1]), true); } _pendingFriendReq = null; Thread.Sleep(1500); continue; } else if (input.Equals("N", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Ignored."); _pendingFriendReq = null; Thread.Sleep(1000); continue; } }

                if (input.Equals("F", StringComparison.OrdinalIgnoreCase)) { Console.Write("Partner IP: "); string ip = Console.ReadLine(); Console.Write("Partner Port: "); int.TryParse(Console.ReadLine(), out int p); Console.WriteLine("Sending request..."); _ = SendFriendRequest(ip, p); Console.WriteLine("Sent!"); Thread.Sleep(2000); }
                else if (input.Equals("L", StringComparison.OrdinalIgnoreCase)) { Console.Clear(); Console.WriteLine("--- Friends List ---"); foreach (var f in _myFriends) { Console.Write($"- {f.Name} ({f.Ip}:{f.Port}): "); if (f.IsOnline) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("ONLINE"); } else { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("OFFLINE"); } Console.ResetColor(); } Console.ReadLine(); }
                else if (input.StartsWith("N ", StringComparison.OrdinalIgnoreCase)) { string n = input.Substring(2).Trim(); string id = GenerateShortId(); JsonManager.UpsertSession(id, string.IsNullOrEmpty(n) ? "New Chat" : n); return id; }
                else if (input.Equals("RESET", StringComparison.OrdinalIgnoreCase)) { Console.Write("RESET? (Y/N): "); if (Console.ReadLine()?.ToUpper() == "Y") { JsonManager.DeleteAllData(); return "RESET_APP"; } }

                // [SỬA LẠI] Logic Join: Nếu thất bại thì không tạo phòng
                else if (input.Equals("J", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("Input: <IP> <Port> <RoomID>: ");
                    string joinCmd = Console.ReadLine();
                    var parts = joinCmd.Split(' ');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int p))
                    {
                        Console.WriteLine("Connecting...");
                        // Chờ kết quả kết nối
                        bool success = await ConnectAndJoin(parts[0], p, parts[2]);

                        if (success)
                        {
                            JsonManager.UpsertSession(parts[2], "Joining...");
                            return parts[2];
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("[ERROR] Connection failed! Cannot join room.");
                            Console.ResetColor();
                            Thread.Sleep(2000);
                            // Quay lại vòng lặp Dashboard, KHÔNG return parts[2]
                        }
                    }
                }
                else if (int.TryParse(input, out int idx) && idx > 0 && idx <= sessions.Count) return sessions[idx - 1].SessionId;
            }
        }

        // [CHAT ROOM - Đã sửa /invitefriend và thông báo]
        static async Task Screen_ChatRoom(string sessionId)
        {
            _currentSessionId = sessionId;
            RedrawChat(sessionId);
            while (true)
            {
                string input = Console.ReadLine(); if (string.IsNullOrWhiteSpace(input)) continue;
                if (input == "/back") return;

                // [SỬA LẠI] Tính năng invite friend chọn số 1,2,3...
                if (input.Trim() == "/invitefriend")
                {
                    var availableFriends = _myFriends.ToList();
                    if (availableFriends.Count == 0) Console.WriteLine("[SYSTEM] Contact list is empty.");
                    else
                    {
                        Console.WriteLine("\n--- SELECT FRIEND TO INVITE ---");
                        for (int i = 0; i < availableFriends.Count; i++)
                        {
                            var f = availableFriends[i];
                            string st = f.IsOnline ? "ONLINE" : "OFFLINE";
                            Console.Write($"[{i + 1}] ");
                            if (f.IsOnline) Console.ForegroundColor = ConsoleColor.Green;
                            else Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine($"{f.Name} ({st})");
                            Console.ResetColor();
                        }
                        Console.WriteLine("[0] Cancel");
                        Console.Write("Enter number: ");
                        string c = Console.ReadLine();

                        if (int.TryParse(c, out int idx) && idx > 0 && idx <= availableFriends.Count)
                        {
                            var t = availableFriends[idx - 1];
                            Console.WriteLine($"Inviting {t.Name}...");
                            bool ok = await ConnectAndJoin(t.Ip, t.Port, sessionId);
                            if (ok) Console.WriteLine($"[SUCCESS] Invited {t.Name} to the room."); // [THÊM] Thông báo thành công
                            else Console.WriteLine($"[ERROR] Cannot connect to {t.Name}.");
                        }
                    }
                    Thread.Sleep(1500); RedrawChat(sessionId); continue;
                }

                // [SỬA LẠI] Invite thường cũng thông báo thành công
                else if (input.StartsWith("/invite "))
                {
                    var p = input.Split(' ');
                    if (p.Length == 3)
                    {
                        bool ok = await ConnectAndJoin(p[1], int.Parse(p[2]), sessionId);
                        if (ok) Console.WriteLine($"[SUCCESS] Invited user at {p[1]}:{p[2]}.");
                        else Console.WriteLine("[ERROR] Invite failed.");
                    }
                }

                else if (input.StartsWith("/join ")) { var p = input.Split(' '); if (p.Length == 4) await ConnectAndJoin(p[1], int.Parse(p[2]), p[3]); }
                else if (input.StartsWith("/edit ")) { var p = input.Split(' ', 3); if (p.Length == 3 && int.TryParse(p[1], out int i)) HandleEditCommand(i, p[2], sessionId); }
                else if (input.StartsWith("/delete ")) { var p = input.Split(' '); if (p.Length == 2 && int.TryParse(p[1], out int i)) HandleDeleteCommand(i, sessionId); }
                else { var packet = new ChatPacket { Id = Guid.NewGuid().ToString(), SessionId = sessionId, Type = PacketType.Message, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = input, Timestamp = DateTime.Now }; ProcessPacket(packet); Broadcast(packet); }
            }
        }

        static void StartServer() { try { if (_server == null) { _server = new TcpListener(IPAddress.Any, _currentUser.Port); _server.Start(); _ = Task.Run(async () => { while (true) { try { var client = await _server.AcceptTcpClientAsync(); lock (_uiLock) _neighbors.Add(client); _ = Task.Run(() => HandleClient(client)); } catch { break; } } }); } } catch { } }
        static async Task HandleClient(TcpClient client) { try { using var reader = new StreamReader(client.GetStream()); while (client.Connected) { string json = await reader.ReadLineAsync(); if (json == null) break; var packet = JsonSerializer.Deserialize<ChatPacket>(json); if (packet.Type == PacketType.Ping) continue; if (packet.Type == PacketType.FriendReq) { _pendingFriendReq = packet; if (_currentSessionId == null) { Console.WriteLine(); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"[ALERT] {packet.SenderInfo} ({packet.SenderName}) want to add friend (Y/N)."); Console.ResetColor(); Console.Write("> "); } continue; } if (packet.Type == PacketType.FriendRes && packet.Content == "YES") { var parts = packet.SenderInfo.Split(':'); if (parts.Length == 2) { JsonManager.AddFriend(packet.SenderName, parts[0], int.Parse(parts[1])); Console.WriteLine(); Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"[SUCCESS] {packet.SenderName} accepted request!"); Console.ResetColor(); if (_currentSessionId == null) Console.Write("> "); } continue; } if (packet.Type == PacketType.Invite) { string realName = JsonManager.GetSessionName(packet.SessionId); if (!string.IsNullOrEmpty(realName) && realName != "Joining...") { var reply = new ChatPacket { Id = Guid.NewGuid().ToString(), SessionId = packet.SessionId, GroupName = realName, Type = PacketType.System, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = $"Welcome to {realName}!", Timestamp = DateTime.Now }; try { var w = new StreamWriter(client.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(reply)); } catch { } } } ProcessPacket(packet); } } catch { } finally { lock (_uiLock) _neighbors.Remove(client); } }
        static async Task SendFriendRequest(string ip, int port) { try { using TcpClient c = new TcpClient(); await c.ConnectAsync(ip, port); var packet = new ChatPacket { Id = Guid.NewGuid().ToString(), Type = PacketType.FriendReq, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Timestamp = DateTime.Now }; using var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(packet)); } catch { Console.WriteLine($"[ERROR] Cannot connect to {ip}:{port}"); } }
        static async Task SendFriendResponse(string ip, int port, bool accepted) { try { using TcpClient c = new TcpClient(); await c.ConnectAsync(ip, port); var packet = new ChatPacket { Id = Guid.NewGuid().ToString(), Type = PacketType.FriendRes, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = accepted ? "YES" : "NO", Timestamp = DateTime.Now }; using var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(packet)); } catch { } }
        static void ProcessPacket(ChatPacket p) { JsonManager.HandlePacketStorage(p); if (_currentSessionId == p.SessionId) RedrawChat(p.SessionId); }
        static void Broadcast(ChatPacket p) { string json = JsonSerializer.Serialize(p); lock (_uiLock) { foreach (var c in _neighbors) { try { var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; w.WriteLine(json); } catch { } } } }
        static void HandleEditCommand(int index, string newContent, string sessionId) { if (index > 0 && index <= _currentViewMap.Count) { var target = _currentViewMap[index - 1]; if (target.SenderName == _currentUser.Username) { var p = new ChatPacket { Id = Guid.NewGuid().ToString(), TargetId = target.Id, SessionId = sessionId, Type = PacketType.Edit, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = newContent, Timestamp = DateTime.Now }; ProcessPacket(p); Broadcast(p); } else Console.WriteLine("[ERROR] Only edit yours."); } }
        static void HandleDeleteCommand(int index, string sessionId) { if (index > 0 && index <= _currentViewMap.Count) { var target = _currentViewMap[index - 1]; if (target.SenderName == _currentUser.Username) { var p = new ChatPacket { Id = Guid.NewGuid().ToString(), TargetId = target.Id, SessionId = sessionId, Type = PacketType.Delete, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Timestamp = DateTime.Now }; ProcessPacket(p); Broadcast(p); } else Console.WriteLine("[ERROR] Only delete yours."); } }
        static void RedrawChat(string sessionId) { lock (_uiLock) { Console.Clear(); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"=== ROOM ID: {sessionId} ==="); Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"[MY INFO] IP: {GetLocalIP()} | Port: {_currentUser.Port}"); Console.ResetColor(); Console.WriteLine("Commands: /back, /invite <IP> <Port>, /edit, /delete, /invitefriend"); Console.WriteLine("---------------------------------------------------------"); var msgs = JsonManager.GetMessages(sessionId).OrderBy(x => x.Timestamp).ToList(); _currentViewMap.Clear(); int idx = 0; foreach (var m in msgs) { _currentViewMap.Add(m); idx++; Console.ForegroundColor = ConsoleColor.Cyan; Console.Write($"[{idx}] "); if (m.Type == PacketType.Invite) { Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine($"[SYSTEM] {m.SenderName}: {m.Content}"); } else if (m.SenderName == _currentUser.Username) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"You: {m.Content}"); } else { Console.ForegroundColor = ConsoleColor.White; Console.WriteLine($"{m.SenderName}: {m.Content}"); } } Console.ResetColor(); Console.WriteLine("---------------------------------------------------------"); Console.Write("> "); } }
        static string ReadInputWithHotkeys() { StringBuilder buffer = new StringBuilder(); while (true) { var keyInfo = Console.ReadKey(true); if (keyInfo.Modifiers == ConsoleModifiers.Control && keyInfo.Key == ConsoleKey.R) return "CMD_REFRESH"; if (keyInfo.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); } if (keyInfo.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) { buffer.Length--; Console.Write("\b \b"); } } else if (!char.IsControl(keyInfo.KeyChar)) { buffer.Append(keyInfo.KeyChar); Console.Write(keyInfo.KeyChar); } } }
        static string GetLocalIP() { var host = Dns.GetHostEntry(Dns.GetHostName()); foreach (var ip in host.AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString(); return "127.0.0.1"; }
        private static string GenerateShortId() { const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"; Random random = new Random(); return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray()); }

        // [HAM CŨ GIỮ NGUYÊN - ĐÃ ĐƯỢC CHUYỂN SANG TRẢ VỀ BOOL]
        static async Task<bool> ConnectAndJoin(string ip, int port, string targetSessionId)
        {
            try
            {
                TcpClient c = new TcpClient();
                // Timeout logic 2s
                var connectTask = c.ConnectAsync(ip, port);
                var timeoutTask = Task.Delay(2000);
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask) { c.Dispose(); return false; }
                await connectTask;

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
                return true;
            }
            catch { return false; }
        }
    }
}