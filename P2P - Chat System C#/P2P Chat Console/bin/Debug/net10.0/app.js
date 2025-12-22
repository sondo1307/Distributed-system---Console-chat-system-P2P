let socket;
let currentUserConfig = null;
let currentRoom = null;
let sessions = [];
let friends = [];

function initWebSocket() {
    // Nếu socket đang mở thì thôi
    if (socket && socket.readyState === WebSocket.OPEN) return;

    socket = new WebSocket("ws://localhost:8080/ws");

    socket.onopen = function() {
        console.log("Connected to C# Backend");
        // Nếu đã từng login trước đó (có biến currentUserConfig), hãy gửi lại lệnh Login để sync
        if(currentUserConfig) {
             sendCmd("LOGIN", currentUserConfig); 
        }
    };

    // [FIX] Tự động kết nối lại nếu bị ngắt
    socket.onclose = function() {
        console.log("Disconnected. Reconnecting in 2s...");
        setTimeout(initWebSocket, 2000);
    };

    socket.onerror = function() {
        socket.close();
    };

    socket.onmessage = function(event) {
        const msg = JSON.parse(event.data);
        const data = msg.data;

        switch (msg.cmd) {
            case "INIT_USER":
		currentUserConfig = data;
    
    		// [ĐÃ SỬA] Thêm dòng hiển thị IP vào giữa Tên và Port
    		document.querySelector(".profile").innerHTML = `
        		<div id="user-info">
            			<h3>${data.Username}</h3>
            			<p>IP: <span style="font-weight:bold; color:#4CAF50">${data.Ip}</span></p>
            			<p>Port: ${data.Port}</p>
        		</div>
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
            case "ALERT": alert(data); break;
            case "RESET_SUCCESS": alert("Reset Done"); location.reload(); break;
            case "FRIEND_REQ_RECEIVED":
                if (confirm(`Friend Request from ${data.name} (${data.ip}:${data.port}). Accept?`)) {
                    sendCmd("RESPOND_FRIEND_REQ", { ...data, accepted: true });
                } else {
                    sendCmd("RESPOND_FRIEND_REQ", { ...data, accepted: false });
                }
                break;
        }
    };
}

// Khởi động
initWebSocket();

function login() {
    const name = document.getElementById("username").value.trim();
    const port = parseInt(document.getElementById("port").value.trim());
    if (!name || !port) return alert("Enter valid info");
    
    // [FIX] Kiểm tra kết nối trước khi gửi
    if (socket.readyState !== WebSocket.OPEN) {
        alert("Connecting to server... Please try again in a second.");
        initWebSocket();
        return;
    }
    
    sendCmd("LOGIN", { username: name, port: port });
}

function sendCmd(cmd, data) {
    if (socket && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ cmd: cmd, data: data }));
    } else {
        console.warn("Socket not open. Cmd:", cmd);
    }
}

// --- GIỮ NGUYÊN CÁC HÀM XỬ LÝ GIAO DIỆN KHÁC (render, room, invite...) ---
// (Copy phần còn lại từ file app.js cũ của bạn vào đây, chỉ thay đổi phần initWebSocket và login ở trên)

// [FIX] Cập nhật lại renderFriends để hiển thị màu xanh chuẩn
function renderFriends() {
    const list = document.getElementById("friendList");
    const modalList = document.querySelector(".modal-right ul");
    
    if(list) {
        list.innerHTML = friends.map(f => `
            <li>
                <span>${f.Name}</span>
                <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
            </li>
        `).join("");
    }

    if(modalList) {
        modalList.innerHTML = friends.map(f => `
            <li onclick="selectFriend('${f.Ip}', ${f.Port})">
                <span>${f.Name}</span>
                <span class="status ${f.IsOnline ? 'online' : 'offline'}">●</span>
            </li>
        `).join("");
    }
}

// --- CÁC HÀM CŨ (Để bạn copy paste cho đủ file nếu cần) ---
function newRoom() { document.getElementById("newRoomModel").classList.remove("hidden"); }
function closeNewRoom() { document.getElementById("newRoomModel").classList.add("hidden"); }
function confirmNewRoom() { const n = document.getElementById("newRoomId").value.trim(); if(!n)return; sendCmd("CREATE_ROOM",{roomName:n}); closeNewRoom(); }
function renderSessions() { const l=document.getElementById("sessionList"); l.innerHTML=""; sessions.forEach(s=>{ const li=document.createElement("li"); li.textContent=`${s.Name} (${s.SessionId})`; li.className=s.SessionId===currentRoom?"active":""; li.onclick=()=>joinChat(s.SessionId,s.Name); l.appendChild(li); }); }
function joinChat(id,name) { currentRoom=id; document.getElementById("roomTitle").innerHTML=`<span>${name} (${id})</span><div><button onclick="inviteFriend()">Invite Friend</button><button onclick="inviteByIp()">Invite IP</button></div>`; renderSessions(); sendCmd("GET_MESSAGES",{sessionId:id}); }
function sendMessage() { if(!currentRoom)return; const i=document.getElementById("messageInput"); const t=i.value.trim(); if(!t)return; sendCmd("SEND_MSG",{sessionId:currentRoom,content:t}); i.value=""; }
function renderMessages(m) { const d=document.getElementById("messages"); d.innerHTML=""; m.forEach(x=>{ const me=currentUserConfig&&x.SenderName===currentUserConfig.Username; const w=document.createElement("div"); w.className="message-wrapper "+(me?"me":"other"); let c=`<span class="message-text">${x.Content}</span>`; if(x.Type===5){w.className="message-wrapper system";c=`<small><i>${x.SenderName} invited you.</i></small>`;} let mn=""; if(me&&x.Type===2){ mn=`<div class="message-actions"><span class="dots">⋮</span><div class="dropdown"><div onclick="editMessage('${x.Id}','${x.Content.replace(/'/g,"\\'")}')">Edit</div><div onclick="deleteMessage('${x.Id}')">Delete</div></div></div>`; } w.innerHTML=`<div class="message-bubble"><div class="sender-name">${x.SenderName}</div>${c}${mn}</div>`; d.appendChild(w); }); d.scrollTop=d.scrollHeight; }
function editMessage(id,old){ const n=prompt("Edit:",old); if(n&&n!==old) sendCmd("EDIT_MSG",{msgId:id,newContent:n,sessionId:currentRoom}); }
function deleteMessage(id){ if(confirm("Delete?")) sendCmd("DELETE_MSG",{msgId:id,sessionId:currentRoom}); }
function joinRoomButton() { document.getElementById("joinRoomModal").classList.remove("hidden"); }
function closeJoinRoom() { document.getElementById("joinRoomModal").classList.add("hidden"); }
function confirmJoinRoom() { const i=document.getElementById("joinIp").value, p=document.getElementById("joinPort").value, r=document.getElementById("joinRoomId").value; sendCmd("JOIN_ROOM",{ip:i,port:parseInt(p),roomId:r}); closeJoinRoom(); }
function inviteFriend() { document.getElementById("inviteFriendModal").classList.remove("hidden"); }
function closeInviteFriend() { document.getElementById("inviteFriendModal").classList.add("hidden"); }
function selectFriend(i,p) { if(!currentRoom)return alert("Select room"); if(confirm(`Invite ${i}:${p}?`)) { sendCmd("INVITE_FRIEND",{ip:i,port:parseInt(p),roomId:currentRoom}); closeInviteFriend(); } }
function inviteByIp(){ document.getElementById("inviteIpModal").classList.remove("hidden"); }
function closeInviteIp(){ document.getElementById("inviteIpModal").classList.add("hidden"); }
function confirmInviteIp(){ const i=document.getElementById("invIp").value, p=document.getElementById("invPort").value; if(i&&p&&currentRoom){ sendCmd("INVITE_BY_IP",{ip:i,port:parseInt(p),roomId:currentRoom}); closeInviteIp(); } }
function addFriend(){ document.getElementById("addFriendModal").classList.remove("hidden"); }
function closeAddFriend(){ document.getElementById("addFriendModal").classList.add("hidden"); }
function confirmAddFriend(){ const i=document.getElementById("friendId").value, p=document.getElementById("friendPort").value; sendCmd("ADD_FRIEND_REQ",{ip:i,port:parseInt(p)}); closeAddFriend(); }
function refreshData(){ location.reload(); }
function resetData(){ if(confirm("Reset All?")) sendCmd("RESET_APP",{}); }