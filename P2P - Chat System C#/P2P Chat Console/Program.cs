using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace P2PFinalChat
{
    public enum PacketType
    {
        Hello,   // Gói tin chào hỏi (Handshake)
        System,  // Gói tin thông báo hệ thống
        Message, // Tin nhắn thường
        Edit,    // Lệnh sửa
        Delete,  // Lệnh xóa
        Invite   // [MOI] Gói tin mời
    }

    public class ChatPacket
    {
        public string Id { get; set; }
        public PacketType Type { get; set; }
        public string TargetId { get; set; }

        public string SenderName { get; set; }
        public string SenderInfo { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsHistory { get; set; }
    }

    class Program
    {
        private static List<TcpClient> _neighbors = new List<TcpClient>();
        private static List<ChatPacket> _messageHistory = new List<ChatPacket>();

        private static object _lock = new object();
        private static ConcurrentDictionary<string, byte> _seenMessages = new ConcurrentDictionary<string, byte>();

        private static string _username;
        private static string _myServerIp;
        private static int _myServerPort;

        static async Task Main(string[] args)
        {
            Console.InputEncoding = Encoding.Unicode;
            Console.OutputEncoding = Encoding.Unicode;
            Console.Title = "P2P Chat Professional";

            // 1. Cấu hình ban đầu
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=== P2P CHAT PROFESSIONAL ===");
            Console.ResetColor();

            Console.Write("Nhap ten cua ban: ");
            _username = Console.ReadLine();

            TcpListener listener = null;
            while (true)
            {
                Console.Write("Nhap cong (Port) de mo (VD: 9000): ");
                if (int.TryParse(Console.ReadLine(), out _myServerPort))
                {
                    try
                    {
                        listener = new TcpListener(IPAddress.Any, _myServerPort);
                        listener.Start();
                        break;
                    }
                    catch { Console.WriteLine("[LOI] Port ban chon khong kha dung."); }
                }
            }

            _myServerIp = GetLocalIPAddress();

            // Khởi chạy Server lắng nghe
            _ = Task.Run(() => AcceptClientsAsync(listener));

            // Vẽ giao diện lần đầu tiên
            RedrawConsole();

            // 2. Vòng lặp chính
            while (true)
            {
                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;

                if (input.StartsWith("/connect"))
                {
                    var parts = input.Split(' ');
                    if (parts.Length == 3)
                        await ConnectToPeer(parts[1], int.Parse(parts[2]));
                }
                else if (input.Trim() == "/list")
                {
                    ShowNeighbors();
                }
                else if (input.StartsWith("/edit "))
                {
                    var parts = input.Split(' ', 3);
                    if (parts.Length == 3 && int.TryParse(parts[1], out int index))
                        HandleEditSpecificMessage(index, parts[2]);
                }
                else if (input.StartsWith("/delete"))
                {
                    var parts = input.Split(' ');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int index))
                        HandleDeleteSpecificMessage(index);
                }
                // --- [MOI] CÁC LỆNH INVITE / TOKEN ---
                else if (input.StartsWith("/invite "))
                {
                    var parts = input.Split(' ');
                    if (parts.Length == 3)
                    {
                        // Gọi hàm kết nối kiểu Invite
                        await ConnectAndInvite(parts[1], int.Parse(parts[2]));
                    }
                    else AddSystemLog("Sai cu phap. Dung: /invite <IP> <Port>");
                }
                else if (input.Trim() == "/token")
                {
                    GenerateInviteToken();
                }
                else if (input.StartsWith("/join "))
                {
                    string token = input.Substring(6).Trim();
                    JoinByToken(token);
                }
                // -------------------------------------
                else
                {
                    // GỬI TIN NHẮN THƯỜNG
                    var packet = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = PacketType.Message,
                        SenderName = _username,
                        SenderInfo = $"{_myServerIp}:{_myServerPort}",
                        Content = input,
                        Timestamp = DateTime.Now,
                        IsHistory = false
                    };

                    ProcessIncomingPacket(packet);
                    BroadcastJson(JsonSerializer.Serialize(packet), null);
                }
            }
        }

        // --- HANDSHAKE (GIỮ NGUYÊN) ---
        private static void SendHelloPacket(TcpClient client)
        {
            try
            {
                var helloPacket = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = PacketType.Hello,
                    SenderName = _username,
                    SenderInfo = $"{_myServerIp}:{_myServerPort}",
                    Timestamp = DateTime.Now
                };

                NetworkStream stream = client.GetStream();
                StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(helloPacket));
            }
            catch { }
        }

        // --- [MOI] GỬI GÓI TIN MỜI (INVITE) ---
        private static void SendInvitePacket(TcpClient client)
        {
            try
            {
                var invitePacket = new ChatPacket
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = PacketType.Invite, // Đánh dấu là Invite
                    SenderName = _username,
                    SenderInfo = $"{_myServerIp}:{_myServerPort}",
                    Content = "da moi ban tham gia chat!",
                    Timestamp = DateTime.Now
                };

                NetworkStream stream = client.GetStream();
                StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(invitePacket));
            }
            catch { }
        }

        // --- [MOI] KẾT NỐI VÀ MỜI (TƯƠNG TỰ CONNECT NHƯNG GỬI INVITE) ---
        private static async Task ConnectAndInvite(string ip, int port)
        {
            try
            {
                TcpClient client = new TcpClient();
                await client.ConnectAsync(ip, port);
                AddNeighbor(client);

                // Gửi Invite thay vì Hello
                SendInvitePacket(client);

                _ = Task.Run(() => SendHistoryToClient(client));
                _ = Task.Run(() => HandleClient(client));

                AddSystemLog($"Da gui loi moi den {ip}:{port}");
            }
            catch (Exception ex)
            {
                AddSystemLog($"[LOI INVITE] {ex.Message}");
            }
        }

        // --- [MOI] TẠO VÀ JOIN TOKEN ---
        private static void GenerateInviteToken()
        {
            try
            {
                string raw = $"{_myServerIp}:{_myServerPort}";
                byte[] bytes = Encoding.UTF8.GetBytes(raw);
                string token = Convert.ToBase64String(bytes);

                AddSystemLog($"MA MOI CUA BAN: {token}");
                AddSystemLog("Gui ma nay cho ban be de ho /join");
            }
            catch { }
        }

        private static void JoinByToken(string token)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(token);
                string raw = Encoding.UTF8.GetString(bytes);
                var parts = raw.Split(':');
                if (parts.Length == 2)
                {
                    AddSystemLog($"Dang ket noi bang Token den {raw}...");
                    _ = ConnectToPeer(parts[0], int.Parse(parts[1]));
                }
            }
            catch
            {
                AddSystemLog("Ma Token khong hop le.");
            }
        }

        // --- NETWORK HANDLERS ---
        private static async Task AcceptClientsAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                AddNeighbor(client);
                _ = Task.Run(() => SendHistoryToClient(client));
                _ = Task.Run(() => HandleClient(client));
            }
        }

        private static async Task ConnectToPeer(string ip, int port)
        {
            try
            {
                TcpClient client = new TcpClient();
                await client.ConnectAsync(ip, port);
                AddNeighbor(client);

                // Gửi chào khi kết nối thành công
                SendHelloPacket(client);

                _ = Task.Run(() => SendHistoryToClient(client));
                _ = Task.Run(() => HandleClient(client));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOI] {ex.Message}");
            }
        }

        private static async Task HandleClient(TcpClient client)
        {
            try
            {
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    while (client.Connected)
                    {
                        string jsonString = await reader.ReadLineAsync();
                        if (jsonString == null) break;

                        try
                        {
                            var packet = JsonSerializer.Deserialize<ChatPacket>(jsonString);

                            // Xử lý gói tin Hello (Handshake)
                            if (packet.Type == PacketType.Hello)
                            {
                                string sysMsg = $"[SYSTEM] Nguoi dung {packet.SenderName} ({packet.SenderInfo}) da ket noi!";
                                AddSystemLog(sysMsg);
                                continue;
                            }
                            // [MOI] Xử lý gói tin Invite
                            else if (packet.Type == PacketType.Invite)
                            {
                                string sysMsg = $"[SYSTEM] *** BAN DA DUOC {packet.SenderName} MOI VAO NHOM! ***";
                                AddSystemLog(sysMsg);
                                continue;
                            }

                            if (_seenMessages.ContainsKey(packet.Id)) continue;

                            ProcessIncomingPacket(packet);

                            if (packet.IsHistory == false && packet.Type != PacketType.System)
                            {
                                BroadcastJson(jsonString, client);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally { RemoveNeighbor(client); }
        }

        private static void AddSystemLog(string content)
        {
            var sysPacket = new ChatPacket
            {
                Id = Guid.NewGuid().ToString(),
                Type = PacketType.System,
                Content = content,
                Timestamp = DateTime.Now
            };
            lock (_lock) { _messageHistory.Add(sysPacket); }
            RedrawConsole();
        }

        private static void ProcessIncomingPacket(ChatPacket packet)
        {
            _seenMessages.TryAdd(packet.Id, 0);

            lock (_lock)
            {
                if (packet.Type == PacketType.Message)
                {
                    if (!_messageHistory.Any(x => x.Id == packet.Id)) _messageHistory.Add(packet);
                }
                else if (packet.Type == PacketType.Edit)
                {
                    var targetMsg = _messageHistory.FirstOrDefault(m => m.Id == packet.TargetId);
                    if (targetMsg != null) targetMsg.Content = packet.Content + " (edited)";
                }
                else if (packet.Type == PacketType.Delete)
                {
                    var targetMsg = _messageHistory.FirstOrDefault(m => m.Id == packet.TargetId);
                    if (targetMsg != null) _messageHistory.Remove(targetMsg);
                }

                _messageHistory = _messageHistory.OrderBy(x => x.Timestamp).ToList();
            }
            RedrawConsole();
        }

        private static void HandleEditSpecificMessage(int userIndex, string newContent)
        {
            lock (_lock)
            {
                var myMessages = _messageHistory
                    .Where(x => x.SenderInfo == $"{_myServerIp}:{_myServerPort}" && x.Type == PacketType.Message)
                    .ToList();

                if (userIndex > 0 && userIndex <= myMessages.Count)
                {
                    var targetMsg = myMessages[userIndex - 1];
                    var editPacket = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = PacketType.Edit,
                        TargetId = targetMsg.Id,
                        SenderName = _username,
                        SenderInfo = $"{_myServerIp}:{_myServerPort}",
                        Content = newContent,
                        Timestamp = DateTime.Now
                    };
                    ProcessIncomingPacket(editPacket);
                    BroadcastJson(JsonSerializer.Serialize(editPacket), null);
                }
                else AddSystemLog("Khong tim thay tin nhan nay de sua.");
            }
        }

        private static void HandleDeleteSpecificMessage(int userIndex)
        {
            lock (_lock)
            {
                var myMessages = _messageHistory
                    .Where(x => x.SenderInfo == $"{_myServerIp}:{_myServerPort}" && x.Type == PacketType.Message)
                    .ToList();

                if (userIndex > 0 && userIndex <= myMessages.Count)
                {
                    var targetMsg = myMessages[userIndex - 1];
                    var deletePacket = new ChatPacket
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = PacketType.Delete,
                        TargetId = targetMsg.Id,
                        SenderName = _username,
                        SenderInfo = $"{_myServerIp}:{_myServerPort}",
                        Timestamp = DateTime.Now
                    };
                    ProcessIncomingPacket(deletePacket);
                    BroadcastJson(JsonSerializer.Serialize(deletePacket), null);
                }
                else AddSystemLog("Khong tim thay tin nhan nay de xoa.");
            }
        }

        // --- VẼ LẠI MÀN HÌNH ---
        private static void RedrawConsole()
        {
            lock (_lock)
            {
                Console.Clear();

                // 1. IN LẠI PHẦN THÔNG TIN (INFO) ĐẦU TRANG
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("=== P2P CHAT PROFESSIONAL - " + _username + " ===");
                Console.ResetColor();
                Console.WriteLine($"[INFO] IP: {_myServerIp} | Port: {_myServerPort}");
                Console.WriteLine("-----------------------------------------------------------------------");
                Console.WriteLine("LENH KET NOI:");
                Console.WriteLine(" - Tu ket noi: /connect <IP> <Port>");
                Console.WriteLine(" - Moi nguoi:  /invite <IP> <Port>");
                Console.WriteLine(" - Tao ma moi: /token");
                Console.WriteLine(" - Vao nhom:   /join <Ma_Token>");
                Console.WriteLine("LENH CHAT:");
                Console.WriteLine(" - Sua tin:    /edit <STT_Vang> <noi dung>");
                Console.WriteLine(" - Xoa tin:    /delete <STT_Vang>");
                Console.WriteLine("-----------------------------------------------------------------------");

                // 2. IN LỊCH SỬ CHAT
                int myMsgIndex = 0;
                int globalCount = 0; // Bộ đếm tổng bên trái

                foreach (var msg in _messageHistory)
                {
                    globalCount++;

                    // In bộ đếm tổng (Màu xanh da trời)
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[{globalCount:D2}] ");

                    if (msg.Type == PacketType.System)
                    {
                        // Hiển thị thông báo hệ thống (kết nối...)
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"[SYSTEM] {msg.Timestamp:HH:mm:ss}: {msg.Content}");
                    }
                    else if (msg.SenderInfo == $"{_myServerIp}:{_myServerPort}")
                    {
                        myMsgIndex++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"[#{myMsgIndex}] "); // STT riêng của user để dùng cho edit/delete

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[{msg.Timestamp:HH:mm:ss}] You: {msg.Content}");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write($"     [{msg.Timestamp:HH:mm:ss}] {msg.SenderName} ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($"({msg.SenderInfo})");
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine($": {msg.Content}");
                    }
                }
                Console.ResetColor();

                Console.WriteLine("-----------------------------------------------------------------------");
                Console.Write("> ");
            }
        }

        // --- UTILS ---
        private static void SendHistoryToClient(TcpClient client)
        {
            List<ChatPacket> historyCopy;
            lock (_lock) { historyCopy = new List<ChatPacket>(_messageHistory.Where(x => x.Type == PacketType.Message).ToList()); }
            if (historyCopy.Count == 0) return;

            try
            {
                NetworkStream stream = client.GetStream();
                StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
                foreach (var msg in historyCopy)
                {
                    msg.IsHistory = true;
                    writer.WriteLine(JsonSerializer.Serialize(msg));
                    Thread.Sleep(10);
                }
            }
            catch { }
        }

        private static void BroadcastJson(string json, TcpClient excludeClient)
        {
            List<TcpClient> deadClients = new List<TcpClient>();
            lock (_lock)
            {
                foreach (var neighbor in _neighbors)
                {
                    if (neighbor == excludeClient) continue;
                    try
                    {
                        if (neighbor.Connected)
                        {
                            NetworkStream stream = neighbor.GetStream();
                            StreamWriter writer = new StreamWriter(stream) { AutoFlush = true };
                            writer.WriteLine(json);
                        }
                        else deadClients.Add(neighbor);
                    }
                    catch { deadClients.Add(neighbor); }
                }
                foreach (var c in deadClients) _neighbors.Remove(c);
            }
        }

        private static void AddNeighbor(TcpClient client) { lock (_lock) _neighbors.Add(client); }
        private static void RemoveNeighbor(TcpClient client) { lock (_lock) { if (_neighbors.Contains(client)) _neighbors.Remove(client); } }

        private static void ShowNeighbors() { lock (_lock) AddSystemLog($"Dang ket noi voi {_neighbors.Count} nut."); }

        private static string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !ip.ToString().StartsWith("127.")) return ip.ToString();
                return "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }
    }
}