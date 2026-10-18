"use strict";

const root = window.location.pathname.endsWith("/")
  ? window.location.pathname
  : `${window.location.pathname}/`;
const elements = {
  connection: document.querySelector("#connection"),
  recordingDot: document.querySelector("#recording-dot"),
  recordingLabel: document.querySelector("#recording-label"),
  elapsed: document.querySelector("#elapsed"),
  game: document.querySelector("#game"),
  status: document.querySelector("#status"),
  start: document.querySelector("#start"),
  stop: document.querySelector("#stop"),
  automatic: document.querySelector("#automatic"),
  protect: document.querySelector("#protect"),
  marker: document.querySelector("#marker")
};
const commandButtons = Array.from(document.querySelectorAll("[data-command]"));
let currentState = null;
let commandPending = false;
let refreshPending = false;
let lastStateAt = 0;

function formatElapsed(totalSeconds) {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(seconds / 3600).toString().padStart(2, "0");
  const minutes = Math.floor((seconds % 3600) / 60).toString().padStart(2, "0");
  const remainder = (seconds % 60).toString().padStart(2, "0");
  return `${hours}:${minutes}:${remainder}`;
}

function render(state) {
  currentState = state;
  lastStateAt = Date.now();
  elements.connection.textContent = "LOCAL";
  elements.connection.classList.remove("offline");
  elements.recordingDot.classList.toggle("active", state.isRecording);
  elements.recordingLabel.textContent = state.isRecording ? "RECORDING" : "STANDBY";
  elements.elapsed.textContent = formatElapsed(state.elapsedSeconds);
  elements.game.textContent = state.currentGame || "No game detected";
  elements.status.textContent = state.status || "Ready";
  elements.start.disabled = commandPending || !state.canStart;
  elements.stop.disabled = commandPending || !state.canStop;
  elements.automatic.checked = state.isAutomaticCaptureEnabled;
  elements.automatic.disabled = commandPending || !state.canToggleAutomaticCapture;
  elements.protect.disabled = commandPending || !state.canProtectRecent;
  elements.marker.disabled = commandPending || !state.canAddMarker;
  commandButtons.forEach((button) => {
    if (button !== elements.start &&
        button !== elements.stop &&
        button !== elements.protect &&
        button !== elements.marker) {
      button.disabled = commandPending;
    }
  });
}

function renderOffline(message) {
  currentState = null;
  elements.connection.textContent = "OFFLINE";
  elements.connection.classList.add("offline");
  elements.recordingDot.classList.remove("active");
  elements.recordingLabel.textContent = "UNAVAILABLE";
  elements.elapsed.textContent = "00:00:00";
  elements.status.textContent = message;
  elements.automatic.checked = false;
  elements.automatic.disabled = true;
  commandButtons.forEach((button) => {
    button.disabled = true;
  });
}

async function readResponse(response) {
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.error || "Blackbox rejected the request.");
  }

  return payload;
}

async function refresh() {
  if (commandPending || refreshPending) {
    return;
  }

  refreshPending = true;
  try {
    const response = await fetch(`${root}api/state`, {
      cache: "no-store",
      credentials: "same-origin"
    });
    render(await readResponse(response));
  } catch (error) {
    renderOffline(error instanceof Error ? error.message : "Blackbox is not running.");
  } finally {
    refreshPending = false;
  }
}

async function sendCommand(command) {
  if (commandPending) {
    return;
  }

  commandPending = true;
  if (currentState) {
    render(currentState);
  }

  try {
    const response = await fetch(`${root}api/command`, {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Content-Type": "application/json",
        "X-Blackbox-Dock": "1"
      },
      body: JSON.stringify({ command })
    });
    commandPending = false;
    render(await readResponse(response));
  } catch (error) {
    commandPending = false;
    if (currentState) {
      render(currentState);
      elements.status.textContent = error instanceof Error
        ? error.message
        : "The command failed.";
    } else {
      renderOffline("Blackbox is not running.");
    }
  }
}

commandButtons.forEach((button) => {
  button.addEventListener("click", () => sendCommand(button.dataset.command));
});

elements.automatic.addEventListener("change", () => {
  sendCommand("ToggleAutomaticCapture");
});

window.addEventListener("obsRecordingStarted", refresh);
window.addEventListener("obsRecordingStopped", refresh);
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) {
    refresh();
  }
});

setInterval(() => {
  if (currentState?.isRecording) {
    const elapsed = currentState.elapsedSeconds + (Date.now() - lastStateAt) / 1000;
    elements.elapsed.textContent = formatElapsed(elapsed);
  }
}, 250);
setInterval(refresh, 1500);
refresh();
