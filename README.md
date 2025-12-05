# LiveMeet AI

A powerful real-time AI transcription and meeting assistant that captures system audio, transcribes it using a local Whisper model, and sends it to ChatGPT for analysis.

## 🚀 Features

*   **Real-time Transcription**: Uses OpenAI's Whisper model (running locally via Python) to transcribe system and microphone audio.
*   **ChatGPT Integration**: Automatically sends transcribed text to ChatGPT via a controlled **Brave Browser** instance.
*   **Smart buffering**: Aggregates speech into complete sentences before sending to avoid spamming the AI.
*   **Robust Error Handling**: Filters out technical logs and errors, ensuring only clean text reaches the AI.
*   **Resilient Connectivity**: auto-attaches to an existing Brave window if open, preserving your login session.
*   **Hotkeys**: Global keyboard shortcuts to control capture.

## 📋 Prerequisites

1.  **Windows OS** (Primary support)
2.  **.NET 7.0 or 8.0 SDK** ([Download](https://dotnet.microsoft.com/en-us/download))
3.  **Python 3.8+** ([Download](https://www.python.org/downloads/))
4.  **Brave Browser** installed at default location (`C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe`)
5.  **FFmpeg** installed and added to your system PATH (Required for Whisper).

## 🛠️ Installation

### 1. Setup the Python Server (Speech-to-Text)

The project uses a local Python server for transcription to avoid API costs and ensure privacy.

```bash
# In the root 'livemeet_ai' folder
pip install flask openai-whisper
```
*Note: You may also need to install `torch` manually if `openai-whisper` doesn't pull the correct version for your hardware.*

### 2. Setup the C# Application

```bash
cd LiveMeetAI.App
dotnet restore
```

## ▶️ How to Run

You need to run **two** separate terminals.

### Terminal 1: Python STT Server
This process listens for audio chunks and transcribes them.

```bash
# In the root 'livemeet_ai' folder
python whisper_server.py
```
*Wait until you see "Model loaded" and "Starting Flask server".*

### Terminal 2: Main Application
This runs the UI, captures audio, and talks to ChatGPT.

```bash
cd LiveMeetAI.App
dotnet run
```

## 🎮 Usage & Hotkeys

| Hotkey | Action |
| :--- | :--- |
| `Ctrl + Shift + S` | **Start System Audio** (Captures meeting/video audio) |
| `Ctrl + Shift + M` | **Start Microphone** (Captures your voice) |
| `Ctrl + Shift + P` | **Stop / Pause** Capture |

1.  Launch the app (`dotnet run`).
2.  It will automatically open **Brave Browser** and navigate to ChatGPT.
3.  **Log in** to ChatGPT if prompted. (The app waits for you).
4.  Press `Ctrl + Shift + S` to start capturing your meeting audio.
5.  Watch ChatGPT analyze the conversation in real-time!

## 🔧 Troubleshooting

### "No connection could be made..."
*   **Cause**: The Python server is not running or crashed.
*   **Fix**: Check Terminal 1. If it crashed, restart `python whisper_server.py`.

### "Verify you are human" / Cloudflare Loop
*   **Cause**: Automated browser detection.
*   **Fix**: The app uses a "Remote Debugging" pattern to minimize this. If it happens, just solve the CAPTCHA manually. The app will wait. **Do not close the browser**; just solve it.

### Double Browser Windows
*   **Fix**: If you already have Brave open with the specialized port (9222), the app will attach to it. If you have "normal" Brave open, it may launch a separate instance. Ideally, close all Brave windows before starting the app to ensure a clean session.

## 📁 Project Structure

*   `whisper_server.py`: The Python STT engine.
*   `LiveMeetAI.App/`: Main C# Application source.
    *   `BraveChatController.cs`: Handles ChatGPT interaction.
    *   `LocalWhisperTranscriptionService.cs`: Sends audio to the Python server.
    *   `AudioCaptureService.cs`: Captures system/mic audio.

## 🛡️ Privacy
All transcription is done **locally** on your machine using Whisper. No audio is sent to the cloud (except the text sent to ChatGPT for processing).
