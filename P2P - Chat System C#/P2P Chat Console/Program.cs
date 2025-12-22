using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace P2PFinalJson
{
    // --- MODELS GIỮ NGUYÊN ---
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
    public class UICommand { public string Cmd { get; set; } public object Data { get; set; } }

    public static class JsonManager
    {
        private static string DataFolder = "Data";
        private static string ConfigFile => Path.Combine(DataFolder, "config.json");
        private static string SessionsFile => Path.Combine(DataFolder, "sessions.json");
        private static string FriendsFile => Path.Combine(DataFolder, "friends.json");
        private static readonly object _fileLock = new object();
        public static void Initialize(string profileName = null) { if (!string.IsNullOrEmpty(profileName)) DataFolder = $"Data_{profileName}"; if (!Directory.Exists(DataFolder)) Directory.CreateDirectory(DataFolder); if (!File.Exists(SessionsFile)) File.WriteAllText(SessionsFile, "[]"); if (!File.Exists(FriendsFile)) File.WriteAllText(FriendsFile, "[]"); }
        public static void DeleteAllData() { lock (_fileLock) { if (Directory.Exists(DataFolder)) { Directory.Delete(DataFolder, true); Initialize(); } } }
        public static UserConfig LoadConfig() { lock (_fileLock) { if (!File.Exists(ConfigFile)) return null; try { return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(ConfigFile)); } catch { return null; } } }
        public static void SaveConfig(UserConfig config) { lock (_fileLock) File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })); }
        public static List<ChatSession> LoadSessions() { lock (_fileLock) { if (!File.Exists(SessionsFile)) return new List<ChatSession>(); try { return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(SessionsFile)) ?? new List<ChatSession>(); } catch { return new List<ChatSession>(); } } }
        public static void UpsertSession(string sessionId, string name) { lock (_fileLock) { var list = LoadSessions(); var existing = list.FirstOrDefault(s => s.SessionId == sessionId); if (existing != null) { existing.LastActive = DateTime.Now; if (!string.IsNullOrEmpty(name) && name != "Joining...") existing.Name = name; } else { list.Add(new ChatSession { SessionId = sessionId, Name = name ?? "Unknown", LastActive = DateTime.Now }); } File.WriteAllText(SessionsFile, JsonSerializer.Serialize(list)); } }
        public static List<Friend> LoadFriends() { lock (_fileLock) { if (!File.Exists(FriendsFile)) return new List<Friend>(); try { return JsonSerializer.Deserialize<List<Friend>>(File.ReadAllText(FriendsFile)) ?? new List<Friend>(); } catch { return new List<Friend>(); } } }
        public static bool AddFriend(string name, string ip, int port) { lock (_fileLock) { var list = LoadFriends(); if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false; list.RemoveAll(x => x.Ip == ip && x.Port == port); list.Add(new Friend { Name = name, Ip = ip, Port = port }); File.WriteAllText(FriendsFile, JsonSerializer.Serialize(list)); return true; } }
        public static List<ChatPacket> GetMessages(string sessionId) { lock (_fileLock) { string path = Path.Combine(DataFolder, $"msg_{sessionId}.json"); if (!File.Exists(path)) return new List<ChatPacket>(); try { return JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>(); } catch { return new List<ChatPacket>(); } } }
        public static string GetSessionName(string sessionId) { var s = LoadSessions().FirstOrDefault(x => x.SessionId == sessionId); return s?.Name; }
        public static void HandlePacketStorage(ChatPacket p)
        {
            lock (_fileLock)
            {
                if (p.Type == PacketType.Ping || p.Type == PacketType.FriendReq || p.Type == PacketType.FriendRes) return;
                // Ưu tiên cập nhật tên phòng từ gói tin nếu có
                string updateName = !string.IsNullOrEmpty(p.GroupName) ? p.GroupName : (p.Type == PacketType.Invite ? $"Chat with {p.SenderName}" : null);
                if (p.SessionId != null) UpsertSession(p.SessionId, updateName);

                string path = Path.Combine(DataFolder, $"msg_{p.SessionId}.json");
                List<ChatPacket> msgs = File.Exists(path) ? (JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>()) : new List<ChatPacket>();
                if (p.Type == PacketType.Message || p.Type == PacketType.Invite || p.Type == PacketType.System) { if (!msgs.Any(x => x.Id == p.Id)) msgs.Add(p); }
                else if (p.Type == PacketType.Edit) { var target = msgs.FirstOrDefault(x => x.Id == p.TargetId); if (target != null) target.Content = p.Content + " (edited)"; }
                else if (p.Type == PacketType.Delete) { var target = msgs.FirstOrDefault(x => x.Id == p.TargetId); if (target != null) msgs.Remove(target); }
                File.WriteAllText(path, JsonSerializer.Serialize(msgs));
            }
        }
    }

    class Program
    {
        static UserConfig _currentUser;
        static TcpListener _server;
        static List<TcpClient> _neighbors = new List<TcpClient>();
        static WebSocket _uiSocket;
        static int _uiPort = 8080;

        static async Task Main(string[] args)
        {
            JsonManager.Initialize(args.Length > 0 ? args[0] : null);
            _currentUser = JsonManager.LoadConfig();
            _ = Task.Run(() => StartWebServer());
            if (_currentUser != null) { _ = Task.Run(() => StartServer()); _ = Task.Run(() => StartOnlineChecker()); }
            Console.WriteLine($"=== SERVER RUNNING: http://localhost:{_uiPort} ===");
            Console.ReadLine();
        }

        static async Task StartWebServer()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{_uiPort}/");
            listener.Start();
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest) ProcessWebSocket(ctx);
                else ServeStaticFiles(ctx);
            }
        }

        static void ServeStaticFiles(HttpListenerContext context)
        {
            string path = context.Request.Url.AbsolutePath == "/" ? "index.html" : context.Request.Url.AbsolutePath.TrimStart('/');
            if (File.Exists(path)) { byte[] buf = File.ReadAllBytes(path); context.Response.ContentLength64 = buf.Length; context.Response.OutputStream.Write(buf, 0, buf.Length); }
            else context.Response.StatusCode = 404;
            context.Response.OutputStream.Close();
        }

        static async void ProcessWebSocket(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            _uiSocket = wsContext.WebSocket;
            if (_currentUser != null)
            {
                await SendToUI("INIT_USER", _currentUser);
                await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList());
                await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
            }
            byte[] buffer = new byte[4096];
            try { while (_uiSocket.State == WebSocketState.Open) { var result = await _uiSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); if (result.MessageType == WebSocketMessageType.Close) break; HandleUICommand(Encoding.UTF8.GetString(buffer, 0, result.Count)); } } catch { }
        }

        static async void HandleUICommand(string json)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cmdObj = JsonSerializer.Deserialize<UICommand>(json, options);
                if (cmdObj == null || cmdObj.Data == null) return;
                JsonElement data = (JsonElement)cmdObj.Data;

                switch (cmdObj.Cmd)
                {
                    case "LOGIN":
                        if (data.ValueKind == JsonValueKind.Undefined) return;
                        string u = data.GetProperty("username").GetString();
                        int p = data.GetProperty("port").ValueKind == JsonValueKind.String ? int.Parse(data.GetProperty("port").GetString()) : data.GetProperty("port").GetInt32();
                        _currentUser = new UserConfig { Username = u, Port = p };
                        JsonManager.SaveConfig(_currentUser);
                        if (_server == null) _ = Task.Run(() => StartServer());
                        _ = Task.Run(() => StartOnlineChecker());
                        await SendToUI("INIT_USER", _currentUser);
                        await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
                        break;

                    case "RESET_APP":
                        try { JsonManager.DeleteAllData(); _currentUser = null; await SendToUI("RESET_SUCCESS", "Done"); }
                        catch (Exception ex) { await SendToUI("ALERT", "Lỗi: " + ex.Message); }
                        break;

                    case "GET_MESSAGES":
                        string sid = data.GetProperty("sessionId").GetString();
                        await SendToUI("UPDATE_MESSAGES", JsonManager.GetMessages(sid));
                        break;

                    case "SEND_MSG":
                        string tSid = data.GetProperty("sessionId").GetString();
                        string content = data.GetProperty("content").GetString();
                        // [FIX] Lấy tên Group hiện tại để gửi kèm, giúp bên kia cập nhật tên phòng
                        string currentGroupName = JsonManager.GetSessionName(tSid);
                        var pkt = new ChatPacket { Id = Guid.NewGuid().ToString(), SessionId = tSid, GroupName = currentGroupName, Type = PacketType.Message, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = content, Timestamp = DateTime.Now };
                        ProcessPacket(pkt); Broadcast(pkt);
                        break;

                    case "CREATE_ROOM":
                        string nameEntered = "";
                        if (data.TryGetProperty("roomName", out var n)) nameEntered = n.GetString();
                        else if (data.TryGetProperty("roomId", out var i)) nameEntered = i.GetString();
                        if (!string.IsNullOrEmpty(nameEntered))
                        {
                            string newId = Guid.NewGuid().ToString().Substring(0, 6).ToUpper();
                            JsonManager.UpsertSession(newId, nameEntered);
                            await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
                        }
                        break;

                    case "JOIN_ROOM":
                        // Logic tham gia phòng
                        string jIp = data.GetProperty("ip").GetString();
                        int jPort = data.GetProperty("port").GetInt32();
                        string jRid = data.GetProperty("roomId").GetString();
                        // Mặc định tên là Joining..., sẽ được update khi nhận tin nhắn lịch sử
                        bool j = await ConnectAndJoin(jIp, jPort, jRid);
                        if (j) { JsonManager.UpsertSession(jRid, "Joining..."); await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions()); await SendToUI("ALERT", "Joined! Waiting for sync..."); }
                        else await SendToUI("ALERT", "Join failed.");
                        break;

                    case "ADD_FRIEND_REQ":
                        string fIp = data.GetProperty("ip").GetString();
                        int fPort = data.GetProperty("port").GetInt32();
                        await SendFriendRequest(fIp, fPort);
                        await SendToUI("ALERT", "Request sent!");
                        break;

                    case "RESPOND_FRIEND_REQ":
                        // [NEW] Xử lý khi người dùng bấm Accept/Deny trên giao diện
                        string rIp = data.GetProperty("ip").GetString();
                        int rPort = data.GetProperty("port").GetInt32();
                        bool accepted = data.GetProperty("accepted").GetBoolean();
                        string rName = data.GetProperty("name").GetString();

                        if (accepted)
                        {
                            JsonManager.AddFriend(rName, rIp, rPort);
                            await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
                        }
                        await SendFriendResponse(rIp, rPort, accepted);
                        break;

                    case "INVITE_FRIEND":
                    case "INVITE_BY_IP": // Dùng chung logic
                        string iIp = data.GetProperty("ip").GetString();
                        int iPort = data.GetProperty("port").GetInt32();
                        string iRoom = data.GetProperty("roomId").GetString();
                        bool invited = await ConnectAndJoin(iIp, iPort, iRoom);
                        await SendToUI("ALERT", invited ? "Invited!" : "Failed to invite.");
                        break;

                    case "EDIT_MSG":
                        string editId = data.GetProperty("msgId").GetString();
                        string newTxt = data.GetProperty("newContent").GetString();
                        string sessId = data.GetProperty("sessionId").GetString();
                        var editPkt = new ChatPacket { Id = Guid.NewGuid().ToString(), TargetId = editId, SessionId = sessId, Type = PacketType.Edit, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = newTxt, Timestamp = DateTime.Now };
                        ProcessPacket(editPkt); Broadcast(editPkt);
                        break;

                    case "DELETE_MSG":
                        string delId = data.GetProperty("msgId").GetString();
                        string sId = data.GetProperty("sessionId").GetString();
                        var delPkt = new ChatPacket { Id = Guid.NewGuid().ToString(), TargetId = delId, SessionId = sId, Type = PacketType.Delete, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Timestamp = DateTime.Now };
                        ProcessPacket(delPkt); Broadcast(delPkt);
                        break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CMD ERROR]: {ex.Message}"); }
        }

        static async Task SendToUI(string cmd, object data) { if (_uiSocket != null && _uiSocket.State == WebSocketState.Open) await _uiSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd, data }))), WebSocketMessageType.Text, true, CancellationToken.None); }

        static void StartServer() { try { if (_server == null) { _server = new TcpListener(IPAddress.Any, _currentUser.Port); _server.Start(); _ = Task.Run(async () => { while (true) { try { var c = await _server.AcceptTcpClientAsync(); _neighbors.Add(c); _ = Task.Run(() => HandleClient(c)); } catch { break; } } }); } } catch { } }

        // [MODIFIED] Sửa logic xử lý gói tin kết bạn
        static async Task HandleClient(TcpClient c)
        {
            try
            {
                using var r = new StreamReader(c.GetStream()); while (c.Connected)
                {
                    string j = await r.ReadLineAsync(); if (j == null) break; var p = JsonSerializer.Deserialize<ChatPacket>(j);
                    if (p.Type == PacketType.Ping) continue;

                    if (p.Type == PacketType.FriendReq)
                    {
                        // Không auto add nữa, gửi yêu cầu lên UI
                        var reqData = new { name = p.SenderName, ip = p.SenderInfo.Split(':')[0], port = int.Parse(p.SenderInfo.Split(':')[1]) };
                        await SendToUI("FRIEND_REQ_RECEIVED", reqData);
                        continue;
                    }

                    if (p.Type == PacketType.FriendRes)
                    {
                        // Nhận phản hồi, nếu YES thì add vào list friend
                        if (p.Content == "YES")
                        {
                            JsonManager.AddFriend(p.SenderName, p.SenderInfo.Split(':')[0], int.Parse(p.SenderInfo.Split(':')[1]));
                            await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
                            await SendToUI("ALERT", $"{p.SenderName} accepted your friend request!");
                        }
                        continue;
                    }

                    ProcessPacket(p);
                }
            }
            catch { }
            finally { _neighbors.Remove(c); }
        }

        static void ProcessPacket(ChatPacket p) { JsonManager.HandlePacketStorage(p); _ = SendToUI("UPDATE_MESSAGES", JsonManager.GetMessages(p.SessionId)); _ = SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions()); }
        static void Broadcast(ChatPacket p) { string j = JsonSerializer.Serialize(p); foreach (var c in _neighbors.ToList()) { try { var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; w.WriteLine(j); } catch { } } }
        static async Task StartOnlineChecker() { while (true) { if (_currentUser != null) { var fs = JsonManager.LoadFriends(); foreach (var f in fs) f.IsOnline = await PingUser(f.Ip, f.Port); await SendToUI("UPDATE_FRIENDS", fs); } await Task.Delay(5000); } }
        static async Task<bool> PingUser(string ip, int port) { try { using (var c = new TcpClient()) { var t = c.ConnectAsync(ip, port); if (await Task.WhenAny(t, Task.Delay(500)) == t) return true; } } catch { } return false; }
        static async Task SendFriendRequest(string ip, int port) { try { using TcpClient c = new TcpClient(); await c.ConnectAsync(ip, port); var p = new ChatPacket { Id = Guid.NewGuid().ToString(), Type = PacketType.FriendReq, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Timestamp = DateTime.Now }; using var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(p)); } catch { } }
        static async Task SendFriendResponse(string ip, int port, bool accepted) { try { using TcpClient c = new TcpClient(); await c.ConnectAsync(ip, port); var p = new ChatPacket { Id = Guid.NewGuid().ToString(), Type = PacketType.FriendRes, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = accepted ? "YES" : "NO", Timestamp = DateTime.Now }; using var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(p)); } catch { } }
        static string GetLocalIP() { var host = Dns.GetHostEntry(Dns.GetHostName()); foreach (var ip in host.AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString(); return "127.0.0.1"; }

        static async Task<bool> ConnectAndJoin(string ip, int port, string tSid)
        {
            try
            {
                TcpClient c = new TcpClient(); await c.ConnectAsync(ip, port); _neighbors.Add(c);
                // Gửi GroupName hiện tại trong gói Invite để bên kia biết tên phòng luôn
                string gName = JsonManager.GetSessionName(tSid);
                var p = new ChatPacket { Id = Guid.NewGuid().ToString(), SessionId = tSid, GroupName = gName, Type = PacketType.Invite, SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = "Joined", Timestamp = DateTime.Now }; var w = new StreamWriter(c.GetStream()) { AutoFlush = true }; await w.WriteLineAsync(JsonSerializer.Serialize(p)); var h = JsonManager.GetMessages(tSid); foreach (var m in h) if (m.Type == PacketType.Message) { await Task.Delay(10); await w.WriteLineAsync(JsonSerializer.Serialize(m)); }
                _ = Task.Run(() => HandleClient(c)); return true;
            }
            catch { return false; }
        }
    }
}