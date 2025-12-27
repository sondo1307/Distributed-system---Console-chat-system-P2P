using System;
using System.Collections.Concurrent;
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
    public enum PacketType
    {
        Hello,
        System,
        Message,
        Edit,
        Delete,
        Invite,
        Ping,
        FriendReq,
        FriendRes
    }

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

    public class Friend
    {
        public string Name { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public bool IsOnline { get; set; }
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

    public class UICommand
    {
        public string Cmd { get; set; }
        public object Data { get; set; }
    }

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
                try
                {
                    return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(ConfigFile));
                }
                catch
                {
                    return null;
                }
            }
        }

        public static void SaveConfig(UserConfig config)
        {
            lock (_fileLock)
                File.WriteAllText(ConfigFile,
                    JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static List<ChatSession> LoadSessions()
        {
            lock (_fileLock)
            {
                if (!File.Exists(SessionsFile)) return new List<ChatSession>();
                try
                {
                    return JsonSerializer.Deserialize<List<ChatSession>>(File.ReadAllText(SessionsFile)) ??
                           new List<ChatSession>();
                }
                catch
                {
                    return new List<ChatSession>();
                }
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
                    bool isPlaceholder = existing.Name == "Joining..." || existing.Name == "New Chat" ||
                                         existing.Name == "Unknown" || existing.Name.StartsWith("Chat with");
                    bool isNewNameValid = !string.IsNullOrEmpty(name) && name != "Joining..." && name != "Unknown";
                    if (isPlaceholder && isNewNameValid) existing.Name = name;
                }
                else
                    list.Add(new ChatSession
                        { SessionId = sessionId, Name = name ?? "Unknown", LastActive = DateTime.Now });

                File.WriteAllText(SessionsFile, JsonSerializer.Serialize(list));
            }
        }

        public static List<Friend> LoadFriends()
        {
            lock (_fileLock)
            {
                if (!File.Exists(FriendsFile)) return new List<Friend>();
                try
                {
                    return JsonSerializer.Deserialize<List<Friend>>(File.ReadAllText(FriendsFile)) ??
                           new List<Friend>();
                }
                catch
                {
                    return new List<Friend>();
                }
            }
        }

        public static bool AddFriend(string name, string ip, int port)
        {
            lock (_fileLock)
            {
                var list = LoadFriends();
                if (list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
                list.RemoveAll(x => x.Ip == ip && x.Port == port);
                list.Add(new Friend { Name = name, Ip = ip, Port = port });
                File.WriteAllText(FriendsFile, JsonSerializer.Serialize(list));
                return true;
            }
        }

        public static List<ChatPacket> GetMessages(string sessionId)
        {
            lock (_fileLock)
            {
                string path = Path.Combine(DataFolder, $"msg_{sessionId}.json");
                if (!File.Exists(path)) return new List<ChatPacket>();
                try
                {
                    return JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ??
                           new List<ChatPacket>();
                }
                catch
                {
                    return new List<ChatPacket>();
                }
            }
        }

        public static string GetSessionName(string sessionId)
        {
            var s = LoadSessions().FirstOrDefault(x => x.SessionId == sessionId);
            return s?.Name;
        }

        public static void HandlePacketStorage(ChatPacket p)
        {
            lock (_fileLock)
            {
                if (p.Type == PacketType.Ping || p.Type == PacketType.FriendReq ||
                    p.Type == PacketType.FriendRes) return;
                string updateName = !string.IsNullOrEmpty(p.GroupName)
                    ? p.GroupName
                    : (p.Type == PacketType.Invite ? $"Chat with {p.SenderName}" : null);
                if (p.SessionId != null) UpsertSession(p.SessionId, updateName);
                string path = Path.Combine(DataFolder, $"msg_{p.SessionId}.json");
                List<ChatPacket> msgs = File.Exists(path)
                    ? (JsonSerializer.Deserialize<List<ChatPacket>>(File.ReadAllText(path)) ?? new List<ChatPacket>())
                    : new List<ChatPacket>();

                // [MESH FIX] Chỉ lưu nếu chưa có
                if (!msgs.Any(x => x.Id == p.Id))
                {
                    if (p.Type == PacketType.Message || p.Type == PacketType.Invite || p.Type == PacketType.System)
                        msgs.Add(p);
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

                    File.WriteAllText(path, JsonSerializer.Serialize(msgs));
                }
            }
        }
    }

    class Program
    {
        static UserConfig _currentUser;
        static TcpListener _server;
        static List<TcpClient> _neighbors = new List<TcpClient>();

        static ConcurrentDictionary<TcpClient, HashSet<string>> _socketSubscriptions =
            new ConcurrentDictionary<TcpClient, HashSet<string>>();

        // [MESH FIX 1] Cache lưu các Message ID đã xử lý để tránh lặp vô tận
        static ConcurrentDictionary<string, byte> _processedPacketIds = new ConcurrentDictionary<string, byte>();

        static WebSocket _uiSocket;
        static int _uiPort = 8080;

        static async Task Main(string[] args)
        {
            JsonManager.Initialize(args.Length > 0 ? args[0] : null);
            _currentUser = JsonManager.LoadConfig();
            _ = Task.Run(() => StartWebServer());
            if (_currentUser != null)
            {
                _ = Task.Run(() => StartServer());
                _ = Task.Run(() => StartOnlineChecker());
            }

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
            string path = context.Request.Url.AbsolutePath == "/"
                ? "index.html"
                : context.Request.Url.AbsolutePath.TrimStart('/');
            if (File.Exists(path))
            {
                byte[] buf = File.ReadAllBytes(path);
                context.Response.ContentLength64 = buf.Length;
                context.Response.OutputStream.Write(buf, 0, buf.Length);
            }
            else context.Response.StatusCode = 404;

            context.Response.OutputStream.Close();
        }

        static async void ProcessWebSocket(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            _uiSocket = wsContext.WebSocket;
            if (_currentUser != null)
            {
                var userInfo = new { _currentUser.Username, _currentUser.Port, Ip = GetLocalIP() };
                await SendToUI("INIT_USER", userInfo);
                await SendToUI("UPDATE_SESSIONS",
                    JsonManager.LoadSessions().OrderByDescending(x => x.LastActive).ToList());
                await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
            }

            byte[] buffer = new byte[4096];
            try
            {
                while (_uiSocket.State == WebSocketState.Open)
                {
                    var result = await _uiSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    HandleUICommand(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch
            {
            }
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
                        int p = data.GetProperty("port").ValueKind == JsonValueKind.String
                            ? int.Parse(data.GetProperty("port").GetString())
                            : data.GetProperty("port").GetInt32();
                        _currentUser = new UserConfig { Username = u, Port = p };
                        JsonManager.SaveConfig(_currentUser);
                        if (_server == null) _ = Task.Run(() => StartServer());
                        _ = Task.Run(() => StartOnlineChecker());
                        var loginInfo = new { _currentUser.Username, _currentUser.Port, Ip = GetLocalIP() };
                        await SendToUI("INIT_USER", loginInfo);
                        await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
                        await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
                        break;
                    case "RESET_APP":
                        try
                        {
                            JsonManager.DeleteAllData();
                            _currentUser = null;
                            _processedPacketIds.Clear();
                            await SendToUI("RESET_SUCCESS", "Done");
                        }
                        catch (Exception ex)
                        {
                            await SendToUI("ALERT", "Error: " + ex.Message);
                        }

                        break;
                    case "GET_MESSAGES":
                        await SendToUI("UPDATE_MESSAGES",
                            JsonManager.GetMessages(data.GetProperty("sessionId").GetString()));
                        break;
                    case "SEND_MSG":
                        string tSid = data.GetProperty("sessionId").GetString();
                        string content = data.GetProperty("content").GetString();
                        string gName = JsonManager.GetSessionName(tSid);
                        var pkt = new ChatPacket
                        {
                            Id = Guid.NewGuid().ToString(), SessionId = tSid, GroupName = gName,
                            Type = PacketType.Message, SenderName = _currentUser.Username,
                            SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = content,
                            Timestamp = DateTime.Now
                        };

                        // Xử lý gói tin của chính mình
                        _processedPacketIds.TryAdd(pkt.Id, 0); // Đánh dấu đã xử lý
                        ProcessPacket(pkt);
                        Broadcast(pkt, null); // Gửi cho tất cả hàng xóm
                        break;
                    case "CREATE_ROOM":
                        string rName = data.GetProperty("roomName").GetString();
                        if (!string.IsNullOrEmpty(rName))
                        {
                            JsonManager.UpsertSession(Guid.NewGuid().ToString().Substring(0, 6).ToUpper(), rName);
                            await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
                        }

                        break;
                    case "JOIN_ROOM":
                        string jRid = data.GetProperty("roomId").GetString();
                        if (await ConnectAndJoin(data.GetProperty("ip").GetString(),
                                data.GetProperty("port").GetInt32(), jRid))
                        {
                            JsonManager.UpsertSession(jRid, "Joining...");
                            await SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
                            await SendToUI("ALERT", "Joined!");
                        }
                        else await SendToUI("ALERT", "Failed.");

                        break;
                    case "ADD_FRIEND_REQ":
                        await SendFriendRequest(data.GetProperty("ip").GetString(),
                            data.GetProperty("port").GetInt32());
                        await SendToUI("ALERT", "Sent!");
                        break;
                    case "RESPOND_FRIEND_REQ":
                        string rIp = data.GetProperty("ip").GetString();
                        int rPort = data.GetProperty("port").GetInt32();
                        bool acc = data.GetProperty("accepted").GetBoolean();
                        if (acc)
                        {
                            JsonManager.AddFriend(data.GetProperty("name").GetString(), rIp, rPort);
                            await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
                        }

                        await SendFriendResponse(rIp, rPort, acc);
                        break;
                    case "INVITE_FRIEND":
                    case "INVITE_BY_IP":
                        string iIp = data.GetProperty("ip").GetString();
                        int iPort = data.GetProperty("port").GetInt32();
                        string iRoom = data.GetProperty("roomId").GetString();
                        await SendToUI("ALERT", await ConnectAndJoin(iIp, iPort, iRoom) ? "Invited!" : "Failed.");
                        break;
                    case "EDIT_MSG":
                        string eId = data.GetProperty("msgId").GetString();
                        string nTx = data.GetProperty("newContent").GetString();
                        string sId = data.GetProperty("sessionId").GetString();
                        var ePkt = new ChatPacket
                        {
                            Id = Guid.NewGuid().ToString(), TargetId = eId, SessionId = sId, Type = PacketType.Edit,
                            SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                            Content = nTx, Timestamp = DateTime.Now
                        };
                        _processedPacketIds.TryAdd(ePkt.Id, 0);
                        ProcessPacket(ePkt);
                        Broadcast(ePkt, null);
                        break;
                    case "DELETE_MSG":
                        string dId = data.GetProperty("msgId").GetString();
                        string dsId = data.GetProperty("sessionId").GetString();
                        var dPkt = new ChatPacket
                        {
                            Id = Guid.NewGuid().ToString(), TargetId = dId, SessionId = dsId, Type = PacketType.Delete,
                            SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                            Timestamp = DateTime.Now
                        };
                        _processedPacketIds.TryAdd(dPkt.Id, 0);
                        ProcessPacket(dPkt);
                        Broadcast(dPkt, null);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CMD ERR: " + ex.Message);
            }
        }

        static async Task SendToUI(string cmd, object data)
        {
            if (_uiSocket != null && _uiSocket.State == WebSocketState.Open)
                await _uiSocket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd, data }))),
                    WebSocketMessageType.Text, true, CancellationToken.None);
        }

        static void StartServer()
        {
            try
            {
                if (_server == null)
                {
                    _server = new TcpListener(IPAddress.Any, _currentUser.Port);
                    _server.Start();
                    _ = Task.Run(async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                var c = await _server.AcceptTcpClientAsync();
                                _neighbors.Add(c);
                                _ = Task.Run(() => HandleClient(c));
                            }
                            catch
                            {
                                break;
                            }
                        }
                    });
                }
            }
            catch
            {
            }
        }

        static async Task HandleClient(TcpClient c)
        {
            _socketSubscriptions.TryAdd(c, new HashSet<string>());
            try
            {
                using var r = new StreamReader(c.GetStream());
                while (c.Connected)
                {
                    string j = await r.ReadLineAsync();
                    if (j == null) break;
                    var p = JsonSerializer.Deserialize<ChatPacket>(j);

                    if (p.Type == PacketType.Ping) continue;

                    // Cập nhật subscription
                    if (!string.IsNullOrEmpty(p.SessionId))
                    {
                        if (_socketSubscriptions.ContainsKey(c)) _socketSubscriptions[c].Add(p.SessionId);
                    }

                    // Handshake logic (Invite, FriendReq, FriendRes...)
                    if (p.Type == PacketType.Invite)
                    {
                        var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                        string hostRoomName = JsonManager.GetSessionName(p.SessionId);
                        if (!string.IsNullOrEmpty(hostRoomName) && hostRoomName != "Joining..." &&
                            hostRoomName != "Unknown")
                        {
                            var infoPkt = new ChatPacket
                            {
                                Id = Guid.NewGuid().ToString(), SessionId = p.SessionId, GroupName = hostRoomName,
                                Type = PacketType.System, SenderName = "System",
                                Content = $"Joined room: {hostRoomName}", Timestamp = DateTime.Now
                            };
                            await w.WriteLineAsync(JsonSerializer.Serialize(infoPkt));
                        }

                        var history = JsonManager.GetMessages(p.SessionId);
                        foreach (var oldMsg in history)
                        {
                            if (oldMsg.Type == PacketType.Message || oldMsg.Type == PacketType.System)
                            {
                                await Task.Delay(5);
                                await w.WriteLineAsync(JsonSerializer.Serialize(oldMsg));
                            }
                        }
                    }

                    if (p.Type == PacketType.FriendReq)
                    {
                        await SendToUI("FRIEND_REQ_RECEIVED",
                            new
                            {
                                name = p.SenderName, ip = p.SenderInfo.Split(':')[0],
                                port = int.Parse(p.SenderInfo.Split(':')[1])
                            });
                        continue;
                    }

                    if (p.Type == PacketType.FriendRes)
                    {
                        if (p.Content == "YES")
                        {
                            JsonManager.AddFriend(p.SenderName, p.SenderInfo.Split(':')[0],
                                int.Parse(p.SenderInfo.Split(':')[1]));
                            await SendToUI("UPDATE_FRIENDS", JsonManager.LoadFriends());
                            await SendToUI("ALERT", $"{p.SenderName} accepted!");
                        }

                        continue;
                    }

                    // [MESH FIX 2] LOGIC ROUTING / RELAYING
                    // Chỉ relay các gói tin Chat/Edit/Delete
                    if (p.Type == PacketType.Message || p.Type == PacketType.Edit || p.Type == PacketType.Delete ||
                        p.Type == PacketType.System)
                    {
                        // Kiểm tra xem đã xử lý gói tin này chưa (tránh vòng lặp)
                        if (_processedPacketIds.ContainsKey(p.Id)) continue;

                        // Đánh dấu đã xử lý
                        _processedPacketIds.TryAdd(p.Id, 0);

                        // 1. Xử lý cho bản thân (Lưu + Hiện lên UI)
                        ProcessPacket(p);

                        // 2. [QUAN TRỌNG] Forward (Relay) cho tất cả các kết nối KHÁC
                        // (Trừ thằng gửi gói tin này cho mình - là thằng 'c')
                        Broadcast(p, c);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _neighbors.Remove(c);
                _socketSubscriptions.TryRemove(c, out _);
            }
        }

        static void ProcessPacket(ChatPacket p)
        {
            JsonManager.HandlePacketStorage(p);
            _ = SendToUI("UPDATE_MESSAGES", JsonManager.GetMessages(p.SessionId));
            _ = SendToUI("UPDATE_SESSIONS", JsonManager.LoadSessions());
        }

        // [MESH FIX 3] Broadcast loại trừ người gửi (excludeClient)
        static void Broadcast(ChatPacket p, TcpClient excludeClient)
        {
            string j = JsonSerializer.Serialize(p);
            foreach (var c in _neighbors.ToList())
            {
                // Không gửi ngược lại cho người vừa gửi tin cho mình
                if (c == excludeClient) continue;

                try
                {
                    bool shouldSend = true;
                    if (!string.IsNullOrEmpty(p.SessionId))
                    {
                        if (_socketSubscriptions.TryGetValue(c, out var sessions))
                        {
                            if (!sessions.Contains(p.SessionId)) shouldSend = false;
                        }
                        else shouldSend = false;
                    }

                    if (shouldSend)
                    {
                        var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                        w.WriteLine(j);
                    }
                }
                catch
                {
                }
            }
        }

        static async Task StartOnlineChecker()
        {
            while (true)
            {
                if (_currentUser != null)
                {
                    var fs = JsonManager.LoadFriends();
                    await Task.WhenAll(fs.Select(async f => f.IsOnline = await PingUser(f.Ip, f.Port)));
                    await SendToUI("UPDATE_FRIENDS", fs);
                }

                await Task.Delay(5000);
            }
        }

        static async Task<bool> PingUser(string ip, int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(ip, port);
                    if (await Task.WhenAny(t, Task.Delay(500)) == t) return true;
                }
            }
            catch
            {
            }

            return false;
        }

        static async Task SendFriendRequest(string ip, int port)
        {
            try
            {
                using TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                var p = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(), Type = PacketType.FriendReq, SenderName = _currentUser.Username,
                    SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Timestamp = DateTime.Now
                };
                using var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(p));
            }
            catch
            {
            }
        }

        static async Task SendFriendResponse(string ip, int port, bool accepted)
        {
            try
            {
                using TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                var p = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(), Type = PacketType.FriendRes, SenderName = _currentUser.Username,
                    SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}", Content = accepted ? "YES" : "NO",
                    Timestamp = DateTime.Now
                };
                using var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(p));
            }
            catch
            {
            }
        }

        static string GetLocalIP()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            return "127.0.0.1";
        }

        static async Task<bool> ConnectAndJoin(string ip, int port, string tSid)
        {
            try
            {
                TcpClient c = new TcpClient();
                await c.ConnectAsync(ip, port);
                _neighbors.Add(c);
                _socketSubscriptions.TryAdd(c, new HashSet<string> { tSid });
                string gName = JsonManager.GetSessionName(tSid);
                var p = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(), SessionId = tSid, GroupName = gName, Type = PacketType.Invite,
                    SenderName = _currentUser.Username, SenderInfo = $"{GetLocalIP()}:{_currentUser.Port}",
                    Content = "Joined", Timestamp = DateTime.Now
                };
                var w = new StreamWriter(c.GetStream()) { AutoFlush = true };
                await w.WriteLineAsync(JsonSerializer.Serialize(p));
                var h = JsonManager.GetMessages(tSid);
                foreach (var m in h)
                    if (m.Type == PacketType.Message)
                    {
                        await Task.Delay(10);
                        await w.WriteLineAsync(JsonSerializer.Serialize(m));
                    }

                _ = Task.Run(() => HandleClient(c));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}