let socket;
let currentUserConfig = null;
let currentRoom = null;
let sessions = [];
let friends = [];

function initWebSocket() {
    socket = new WebSocket("ws://localhost:8080/ws");

    socket.onopen = function() {
        console.log("Connected to C# Backend");
    };

    socket.onmessage = function(event) {
        const msg = JSON.parse(event.data);
        const data = msg.data;

        switch (msg.cmd) {
            case "INIT_USER":
                currentUserConfig = data;
                document.querySelector(".profile").innerHTML = `
                    <h3>${data.Username}</h3>
                    <p>Port: ${data.Port}</p>
                    <!--<button onclick="logout()" class="btn-cancel">Logout</button>-->
                `;
                break;
            case "UPDATE_SESSIONS":
                sessions = data;
                renderSessions();
                break;
            case "UPDATE_MESSAGES":
                if (data.length > 0 && data[0].SessionId === currentRoom) {
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
       		location.reload(); // Tự động F5 lại trang để về màn hình Login
       		break;
        }
    };
}

// Khởi động
initWebSocket();

// --- AUTH ---
function login() {
    const name = document.getElementById("username").value.trim();
    const port = parseInt(document.getElementById("port").value.trim());
    if (!name || !port) return alert("Enter valid info");
    
    sendCmd("LOGIN", { username: name, port: port });
}

function logout() {
    // Reset app đơn giản bằng reload
    location.reload();
}

// --- COMMANDS ---
function sendCmd(cmd, data) {
    if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ cmd: cmd, data: data }));
    } else {
        alert("Connection lost");
    }
}

// --- ROOMS ---
// --- app.js ---

// 1. Sửa hàm newRoom để hiển thị đúng placeholder (thẩm mỹ)
function newRoom() { 
    document.getElementById("newRoomModel").classList.remove("hidden");
    document.getElementById("newRoomId").placeholder = "Enter Room Name (e.g. Chat Nhóm)";
    document.getElementById("newRoomId").value = ""; // Reset giá trị cũ
}

// 2. Sửa logic confirmNewRoom
function confirmNewRoom() {
    // Lấy tên phòng từ ô input
    const name = document.getElementById("newRoomId").value.trim();
    
    if (!name) {
        alert("Please enter a room name!");
        return;
    }
    
    // Gửi lệnh CREATE_ROOM với dữ liệu là roomName
    // Lưu ý: Key gửi đi là 'roomName' để bên C# dễ phân biệt
    sendCmd("CREATE_ROOM", { roomName: name });
    
    closeNewRoom();
}

function closeNewRoom() { document.getElementById("newRoomModel").classList.add("hidden"); }

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
        <button onclick="inviteFriend()">+ Invite</button>
    `;
    renderSessions(); // update active class
    sendCmd("GET_MESSAGES", { sessionId: id });
}

// --- MESSAGES ---
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
        const isMe = m.SenderName === currentUserConfig.Username;
        const wrapper = document.createElement("div");
        wrapper.className = "message-wrapper " + (isMe ? "me" : "other");
        
        let contentHtml = `<span class="message-text">${m.Content}</span>`;
        if (m.Type === 5) { // Invite
             wrapper.className = "message-wrapper system";
             contentHtml = `<small><i>${m.SenderName} invited you.</i></small>`;
        }

        wrapper.innerHTML = `
            <div class="message-bubble">
                <div class="sender-name">${m.SenderName}</div>
                ${contentHtml}
            </div>
        `;
        div.appendChild(wrapper);
    });
    div.scrollTop = div.scrollHeight;
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

function inviteFriend() { document.getElementById("inviteFriendModal").classList.remove("hidden"); }
function closeInviteFriend() { document.getElementById("inviteFriendModal").classList.add("hidden"); }
function selectFriend(ip, port) {
    if(!currentRoom) return alert("Select room first");
    if(confirm(`Invite friend at ${ip}:${port}?`)) {
        sendCmd("INVITE_FRIEND", { ip: ip, port: parseInt(port), roomId: currentRoom });
        closeInviteFriend();
    }
}

// --- FRIENDS ---
function addFriend() { document.getElementById("addFriendModal").classList.remove("hidden"); }
function closeAddFriend() { document.getElementById("addFriendModal").classList.add("hidden"); }
function confirmAddFriend() {
    const ip = document.getElementById("friendId").value; // Input label là "Partner IP"
    const port = document.getElementById("friendPort").value;
    sendCmd("ADD_FRIEND_REQ", { ip: ip, port: parseInt(port) });
    closeAddFriend();
}

function renderFriends() {
    const list = document.getElementById("friendList"); // Sidebar list
    const modalList = document.querySelector(".modal-right ul"); // Invite modal list
    
    const html = friends.map(f => `
        <li onclick="selectFriend('${f.Ip}', ${f.Port})">
            <span>${f.Name}</span>
            <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
        </li>
    `).join("");

    if(list) list.innerHTML = html;
    if(modalList) modalList.innerHTML = html;
}

function resetData() {
    // 1. Hỏi xác nhận (Quan trọng vì xóa là mất hết)
    const confirmed = confirm("CẢNH BÁO: Bạn có chắc chắn muốn xóa TOÀN BỘ dữ liệu?\n\n- Tất cả tin nhắn sẽ mất.\n- Danh sách bạn bè sẽ mất.\n- Bạn sẽ bị đăng xuất.\n\nHành động này không thể hoàn tác!");
    
    if (confirmed) {
        // 2. Gửi lệnh RESET_APP về C#
        // Gửi data rỗng {} để tránh lỗi null bên C#
        sendCmd("RESET_APP", {});
    }
}