let socket;
let currentUserConfig = null;
let currentRoom = null;
let sessions = [];
let friends = [];

function initWebSocket() {
    socket = new WebSocket("ws://localhost:8080/ws");
    socket.onopen = function() { console.log("Connected to C# Backend"); };
    socket.onclose = function() { console.log("Disconnected"); };

    socket.onmessage = function(event) {
        const msg = JSON.parse(event.data);
        const data = msg.data;

        switch (msg.cmd) {
            case "INIT_USER":
                currentUserConfig = data;
                document.querySelector(".profile").innerHTML = `
                    <h3>${data.Username}</h3>
                    <p>Port: ${data.Port}</p>
                    <button onclick="logout()">Logout / Refresh</button>
                `;
                break;
            case "UPDATE_SESSIONS":
                sessions = data;
                renderSessions();
                break;
            case "UPDATE_MESSAGES":
                // Chỉ render lại nếu đang ở đúng phòng hoặc nếu là data rỗng (clear chat)
                if ((data.length > 0 && data[0].SessionId === currentRoom) || data.length === 0) {
                    renderMessages(data);
                }
                break;
            case "UPDATE_FRIENDS":
                friends = data;
                renderFriends();
                break;
            case "ALERT":
                alert(data);
                break;
        }
    };
}

initWebSocket();

// --- BASIC COMMANDS ---
// --- XỬ LÝ ĐĂNG NHẬP ---

function login() {
    // 1. Lấy dữ liệu từ ô input
    const nameInput = document.getElementById("username");
    const portInput = document.getElementById("port");

    const name = nameInput.value.trim();
    const port = parseInt(portInput.value.trim());

    // 2. Validate (Kiểm tra dữ liệu rỗng)
    if (!name) {
        alert("Vui lòng nhập Username!");
        return;
    }
    if (!port || isNaN(port)) {
        alert("Vui lòng nhập Port hợp lệ (số)!");
        return;
    }

    // 3. Gửi lệnh LOGIN về C# qua WebSocket
    // Cấu trúc data khớp với UserConfig bên C#
    sendCmd("LOGIN", { username: name, port: port });
}

function logout() {
    // Reload trang để reset lại kết nối
    if(confirm("Bạn muốn đăng xuất?")) {
        location.reload(); 
    }
}

// Hàm cập nhật giao diện sau khi C# phản hồi đăng nhập thành công
// Hàm này được gọi trong socket.onmessage khi cmd == "INIT_USER"
function updateProfileUI(user) {
    if (!user) return;

    // Lưu config vào biến toàn cục
    currentUserConfig = user;

    // Ẩn form đăng nhập
    document.getElementById("login-form").classList.add("hidden");
    
    // Hiện thông tin user
    document.getElementById("user-info").classList.remove("hidden");
    document.getElementById("display-name").textContent = user.Username;
    document.getElementById("display-port").textContent = user.Port;
}

// --- SỬA LẠI socket.onmessage ĐỂ GỌI updateProfileUI ---
// Tìm đoạn socket.onmessage trong initWebSocket() và đảm bảo case INIT_USER như sau:

/* socket.onmessage = function(event) {
       const msg = JSON.parse(event.data);
       const data = msg.data;

       switch (msg.cmd) {
           case "INIT_USER":
               // GỌI HÀM CẬP NHẬT GIAO DIỆN Ở ĐÂY
               updateProfileUI(data); 
               break;
           
           // ... các case khác giữ nguyên
       }
   };
*/

// [TÍNH NĂNG MỚI] RESET DATA
function resetData() {
    if (confirm("Are you sure you want to delete all data and reset?")) {
        sendCmd("RESET_APP", {}); // Gửi object rỗng để tránh lỗi null bên C#
    }
}

// [TÍNH NĂNG MỚI] REFRESH (Thực ra C# tự đẩy data, nhưng nút này có thể dùng để reload trang)
function refreshData() {
    location.reload();
}

function sendCmd(cmd, data) {
    if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ cmd: cmd, data: data }));
    } else {
        alert("Server not connected.");
    }
}

// --- ROOMS ---
function newRoom() { document.getElementById("newRoomModel").classList.remove("hidden"); }
function closeNewRoom() { document.getElementById("newRoomModel").classList.add("hidden"); }
function confirmNewRoom() {
    const id = document.getElementById("newRoomId").value.trim();
    if (id) sendCmd("CREATE_ROOM", { roomId: id });
    closeNewRoom();
}

function renderSessions() {
    const list = document.getElementById("sessionList");
    list.innerHTML = "";
    sessions.forEach(s => {
        const li = document.createElement("li");
        li.textContent = `${s.Name} (${s.SessionId})`;
        li.className = s.SessionId === currentRoom ? "active" : "";
        li.onclick = () => joinChat(s.SessionId, s.Name);
        list.appendChild(li);
    });
}

function joinChat(id, name) {
    currentRoom = id;
    document.getElementById("roomTitle").innerHTML = `
        <span>${name} (${id})</span>
        <button class="invite-btn" onclick="inviteFriend()">+ Invite</button>
    `;
    renderSessions();
    sendCmd("GET_MESSAGES", { sessionId: id });
}

// --- MESSAGES (SEND, EDIT, DELETE) ---
function sendMessage() {
    if (!currentRoom) return;
    const input = document.getElementById("messageInput");
    const text = input.value.trim();
    if (!text) return;
    sendCmd("SEND_MSG", { sessionId: currentRoom, content: text });
    input.value = "";
}

function renderMessages(msgs) {
    const div = document.getElementById("messages");
    div.innerHTML = "";
    msgs.forEach(m => {
        const isMe = currentUserConfig && m.SenderName === currentUserConfig.Username;
        const wrapper = document.createElement("div");
        wrapper.className = "message-wrapper " + (isMe ? "me" : "other");

        let contentHtml = `<span class="message-text">${m.Content}</span>`;
        if (m.Type === 5) { // Invite
            wrapper.className = "message-wrapper system";
            contentHtml = `<small><i>${m.SenderName} invited you.</i></small>`;
        }

        // [QUAN TRỌNG] Gắn ID tin nhắn vào hàm edit/delete
        // Chú ý dấu nháy đơn bao quanh ID: editMessage('${m.Id}')
        wrapper.innerHTML = `
            <div class="message-bubble">
                <div class="sender-name">${m.SenderName}</div>
                ${contentHtml}
                ${isMe && m.Type === 2 ? `
                <div class="message-actions">
                  <span class="dots">⋮</span>
                  <div class="dropdown">
                    <div onclick="editMessage('${m.Id}', '${m.Content}')">Edit</div>
                    <div onclick="deleteMessage('${m.Id}')">Delete</div>
                  </div>
                </div>` : ""}
            </div>
        `;
        div.appendChild(wrapper);
    });
    div.scrollTop = div.scrollHeight;
}

// [TÍNH NĂNG MỚI] XỬ LÝ EDIT
function editMessage(msgId, oldContent) {
    const newContent = prompt("Edit message:", oldContent);
    if (newContent !== null && newContent !== oldContent) {
        sendCmd("EDIT_MSG", { msgId: msgId, newContent: newContent, sessionId: currentRoom });
    }
}

// [TÍNH NĂNG MỚI] XỬ LÝ DELETE
function deleteMessage(msgId) {
    if (confirm("Delete this message?")) {
        sendCmd("DELETE_MSG", { msgId: msgId, sessionId: currentRoom });
    }
}

// --- JOIN & INVITE & FRIEND ---
function joinRoomButton() { document.getElementById("joinRoomModal").classList.remove("hidden"); }
function closeJoinRoom() { document.getElementById("joinRoomModal").classList.add("hidden"); }
function confirmJoinRoom() {
    const ip = document.getElementById("joinIp").value;
    const port = document.getElementById("joinPort").value;
    const rid = document.getElementById("joinRoomId").value;
    if(ip && port && rid) sendCmd("JOIN_ROOM", { ip: ip, port: parseInt(port), roomId: rid });
    closeJoinRoom();
}

function inviteFriend() { document.getElementById("inviteFriendModal").classList.remove("hidden"); }
function closeInviteFriend() { document.getElementById("inviteFriendModal").classList.add("hidden"); }
function selectFriend(ip, port) {
    if(!currentRoom) return alert("Select room first");
    if(confirm(`Invite friend at ${ip}:${port}?`)) {
        sendCmd("INVITE_FRIEND", { ip: ip, port: parseInt(port), roomId: currentRoom });
        closeInviteFriend();
    }
}

function addFriend() { document.getElementById("addFriendModal").classList.remove("hidden"); }
function closeAddFriend() { document.getElementById("addFriendModal").classList.add("hidden"); }
function confirmAddFriend() {
    const ip = document.getElementById("friendId").value;
    const port = document.getElementById("friendPort").value;
    if(ip && port) sendCmd("ADD_FRIEND_REQ", { ip: ip, port: parseInt(port) });
    closeAddFriend();
}

function renderFriends() {
    const list = document.getElementById("friendList");
    const modalList = document.querySelector(".modal-right ul");
    
    const html = friends.map(f => `
        <li onclick="selectFriend('${f.Ip}', ${f.Port})">
            <span>${f.Name}</span>
            <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
        </li>
    `).join("");

    if(list) list.innerHTML = html;
    if(modalList) modalList.innerHTML = html;
}