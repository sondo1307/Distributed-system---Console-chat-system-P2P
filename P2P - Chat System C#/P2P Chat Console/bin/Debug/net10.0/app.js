let socket;
let currentUserConfig = null;
let currentRoom = null;
let sessions = [];
let friends = [];

function initWebSocket() {
    socket = new WebSocket("ws://localhost:8080/ws");

    socket.onopen = function() { console.log("Connected to C# Backend"); };

    socket.onmessage = function(event) {
        const msg = JSON.parse(event.data);
        const data = msg.data;

        switch (msg.cmd) {
            case "INIT_USER":
                currentUserConfig = data;
                document.querySelector(".profile").innerHTML = `
                    <h3>${data.Username}</h3>
                    <p>Port: ${data.Port}</p>
                `;
                break;
            case "UPDATE_SESSIONS":
                sessions = data;
                renderSessions();
                break;
            case "UPDATE_MESSAGES":
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
            case "RESET_SUCCESS":
                alert("Dữ liệu đã được xóa thành công. Ứng dụng sẽ khởi động lại.");
                location.reload();
                break;
            
            // [NEW] Xử lý khi nhận được lời mời kết bạn
            case "FRIEND_REQ_RECEIVED":
                if (confirm(`Bạn nhận được lời mời kết bạn từ ${data.name} (${data.ip}:${data.port}).\nĐồng ý kết bạn?`)) {
                    sendCmd("RESPOND_FRIEND_REQ", { ...data, accepted: true });
                } else {
                    sendCmd("RESPOND_FRIEND_REQ", { ...data, accepted: false });
                }
                break;
        }
    };
}

initWebSocket();

function login() {
    const name = document.getElementById("username").value.trim();
    const port = parseInt(document.getElementById("port").value.trim());
    if (!name || !port) return alert("Enter valid info");
    sendCmd("LOGIN", { username: name, port: port });
}

function sendCmd(cmd, data) {
    if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ cmd: cmd, data: data }));
    } else {
        alert("Connection lost");
    }
}

// --- ROOMS ---
function newRoom() { 
    document.getElementById("newRoomModel").classList.remove("hidden");
    document.getElementById("newRoomId").placeholder = "Enter Room Name (e.g. Chat Nhóm)";
    document.getElementById("newRoomId").value = ""; 
}
function closeNewRoom() { document.getElementById("newRoomModel").classList.add("hidden"); }
function confirmNewRoom() {
    const name = document.getElementById("newRoomId").value.trim();
    if (!name) return alert("Please enter a room name!");
    sendCmd("CREATE_ROOM", { roomName: name });
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
        <div>
            <button onclick="inviteFriend()">Invite Friend</button>
            <button onclick="inviteByIp()">Invite IP</button>
        </div>
    `;
    renderSessions(); 
    sendCmd("GET_MESSAGES", { sessionId: id });
}

// --- MESSAGES (EDIT & DELETE) ---
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

        // [MODIFIED] Thêm Menu Edit/Delete cho tin nhắn của mình
        let menuHtml = "";
        if (isMe && m.Type === 2) { // Type 2 is Message
            menuHtml = `
            <div class="message-actions">
                <span class="dots">⋮</span>
                <div class="dropdown">
                    <div onclick="editMessage('${m.Id}', '${m.Content.replace(/'/g, "\\'")}')">Edit</div>
                    <div onclick="deleteMessage('${m.Id}')">Delete</div>
                </div>
            </div>`;
        }

        wrapper.innerHTML = `
            <div class="message-bubble">
                <div class="sender-name">${m.SenderName}</div>
                ${contentHtml}
                ${menuHtml}
            </div>
        `;
        div.appendChild(wrapper);
    });
    div.scrollTop = div.scrollHeight;
}

function editMessage(id, oldContent) {
    const newContent = prompt("Edit message:", oldContent);
    if (newContent && newContent !== oldContent) {
        sendCmd("EDIT_MSG", { msgId: id, newContent: newContent, sessionId: currentRoom });
    }
}

function deleteMessage(id) {
    if (confirm("Delete this message?")) {
        sendCmd("DELETE_MSG", { msgId: id, sessionId: currentRoom });
    }
}

// --- JOIN & INVITE ---
function joinRoomButton() { document.getElementById("joinRoomModal").classList.remove("hidden"); }
function closeJoinRoom() { document.getElementById("joinRoomModal").classList.add("hidden"); }
function confirmJoinRoom() {
    const ip = document.getElementById("joinIp").value;
    const port = document.getElementById("joinPort").value;
    const rid = document.getElementById("joinRoomId").value;
    sendCmd("JOIN_ROOM", { ip: ip, port: parseInt(port), roomId: rid });
    closeJoinRoom();
}

// Invite Friend Modal
function inviteFriend() { document.getElementById("inviteFriendModal").classList.remove("hidden"); }
function closeInviteFriend() { document.getElementById("inviteFriendModal").classList.add("hidden"); }
function selectFriend(ip, port) {
    if(!currentRoom) return alert("Select room first");
    if(confirm(`Invite friend at ${ip}:${port}?`)) {
        sendCmd("INVITE_FRIEND", { ip: ip, port: parseInt(port), roomId: currentRoom });
        closeInviteFriend();
    }
}

// [NEW] Invite By IP directly
function inviteByIp() {
    document.getElementById("inviteIpModal").classList.remove("hidden");
}
function closeInviteIp() { document.getElementById("inviteIpModal").classList.add("hidden"); }
function confirmInviteIp() {
    const ip = document.getElementById("invIp").value;
    const port = document.getElementById("invPort").value;
    if (ip && port && currentRoom) {
        sendCmd("INVITE_BY_IP", { ip: ip, port: parseInt(port), roomId: currentRoom });
        closeInviteIp();
    } else {
        alert("Please enter IP, Port and join a room first.");
    }
}

// --- FRIENDS ---
function addFriend() { document.getElementById("addFriendModal").classList.remove("hidden"); }
function closeAddFriend() { document.getElementById("addFriendModal").classList.add("hidden"); }
function confirmAddFriend() {
    const ip = document.getElementById("friendId").value;
    const port = document.getElementById("friendPort").value;
    sendCmd("ADD_FRIEND_REQ", { ip: ip, port: parseInt(port) });
    closeAddFriend();
}

function renderFriends() {
    const list = document.getElementById("friendList"); // Sidebar list
    const modalList = document.querySelector(".modal-right ul"); // Invite modal list
    
    // [MODIFIED] Sidebar list: KHÔNG CÓ onclick (theo yêu cầu)
    if(list) {
        list.innerHTML = friends.map(f => `
            <li>
                <span>${f.Name}</span>
                <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
            </li>
        `).join("");
    }

    // Modal list: CÓ onclick để chọn mời
    if(modalList) {
        modalList.innerHTML = friends.map(f => `
            <li onclick="selectFriend('${f.Ip}', ${f.Port})">
                <span>${f.Name}</span>
                <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
            </li>
        `).join("");
    }
}

function refreshData() { location.reload(); }
function resetData() {
    if (confirm("CẢNH BÁO: Xóa TOÀN BỘ dữ liệu?")) {
        sendCmd("RESET_APP", {});
    }
}