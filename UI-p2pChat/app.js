let currentUser = null;
let currentRoom = null;

const sessions = {};
const sessionList = document.getElementById("sessionList");
const messagesDiv = document.getElementById("messages");
const roomTitle = document.getElementById("roomTitle");

function login() {
    const name = username.value.trim();
    const port = port.value.trim();

    if (!name || !port) {
        alert("Enter username & port");
        return;
    }

    currentUser = name;
    alert(`Logged in as ${name}`);
}

function newRoom() {
    const id = Math.random().toString(36).substring(2, 8).toUpperCase();
    sessions[id] = [];
    renderSessions();
}

function renderSessions() {
    sessionList.innerHTML = "";
    Object.keys(sessions).forEach(id => {
        const li = document.createElement("li");
        li.textContent = id;
        li.className = id === currentRoom ? "active" : "";
        li.onclick = () => joinRoom(id);
        sessionList.appendChild(li);
    });
}

function joinRoom(id) {
    currentRoom = id;

    roomTitle.innerHTML = `
        <span>Room ${id}</span>
        <button class="invite-btn" onclick="inviteFriend()">
            Invite friend
        </button>
    `;

    renderSessions();
    renderMessages();
}

function sendMessage() {
    if (!currentRoom) return;

    const text = messageInput.value.trim();
    if (!text) return;

    sessions[currentRoom].push({
        sender: currentUser,
        content: text
    });

    messageInput.value = "";
    renderMessages();
}

function renderMessages() {
    messagesDiv.innerHTML = "";

    sessions[currentRoom].forEach(m => {
        const div = document.createElement("div");
        div.className = "message " + (m.sender === currentUser ? "me" : "other");
        div.textContent = `${m.sender}: ${m.content}`;
        messagesDiv.appendChild(div);
    });

    messagesDiv.scrollTop = messagesDiv.scrollHeight;
}
function addFriend() {
    document.getElementById("addFriendModal")
        .classList.remove("hidden");
}

function closeAddFriend() {
    document.getElementById("addFriendModal")
        .classList.add("hidden");

    document.getElementById("friendId").value = "";
    document.getElementById("friendPort").value = "";
}

function confirmAddFriend() {
    const id = document.getElementById("friendId").value.trim();
    const port = document.getElementById("friendPort").value.trim();

    if (!id || !port) {
        alert("Please enter Partner ID and Port");
        return;
    }

    console.log("Add friend:", id, port);

    alert(`Friend request sent to ${id}:${port}`);

    closeAddFriend();
}

function joinRoomButton() {
    document.getElementById("joinRoomModal")
        .classList.remove("hidden");
}

function closeJoinRoom() {
    document.getElementById("joinRoomModal")
        .classList.add("hidden");
    document.getElementById("joinIp").value = "";
    document.getElementById("joinPort").value = "";
    document.getElementById("joinRoomId").value = "";
}



function confirmJoinRoom() {
    const ip = document.getElementById("joinIp").value.trim();
    const port = document.getElementById("joinPort").value.trim();
    const roomId = document.getElementById("joinRoomId").value.trim();


    console.log("JOIN ROOM:", ip, port, roomId);

    //   alert(`Joining room ${roomId}\n${ip}:${port}`);

    closeJoinRoom();

    console.log(document.getElementById("joinIp").value = "");
    document.getElementById("joinPort").value = "";
    document.getElementById("joinRoomId").value = "";
}

function inviteFriend(){
     document.getElementById("inviteFriendModal")
        .classList.remove("hidden");
}

function closeInviteFriend(){
    document.getElementById("inviteFriendModal")
        .classList.add("hidden");
    document.getElementById("joinIp").value = "";
    document.getElementById("joinPort").value = "";
}