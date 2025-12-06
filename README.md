# LiveMeet AI

A powerful real-time AI transcription and meeting assistant that captures system audio, transcribes it using a local Whisper model, and sends it to ChatGPT for analysis.

## 🚀 Features

### Core
*   **Real-time Transcription**: Uses OpenAI's **Whisper Small** model (running locally via Python) for high-accuracy English transcription.
*   **ChatGPT Integration**: Automatically sends transcribed text to ChatGPT via a controlled **Brave Browser** instance.
*   **Stereo Capture**: Natively supports modern Bluetooth headsets (like realme Buds) in 2-channel mode to prevent audio corruption.

### Smart Automation ("Stealth Mode")
*   **Cloudflare Evasion**: Uses advanced Selenium flags (masked User-Agent, disabled automation features) to bypass "Verify you are human" checks.
*   **Natural Interactions**: Browser launches in a standard tab configuration to mimic human behavior.
*   **Resilient Connectivity**: Auto-attaches to an existing Brave window if open, preserving your logic session.

### Intelligent Processing
*   **Message Aggregation**: Waits for the final part of your sentence (e.g., "...in Dart") to be transcribed before sending, ensuring ChatGPT gets ONE complete context.
*   **Silence Detection**: RMS-based filtering allows the AI to ignore background static and only process voice.
*   **Flush-on-Stop**: Guarantees the last 3 seconds of audio are sent immediately when you release the hotkey.

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
pip install flask openai-whisper numpy
```
*Note: The first run will download the 'small' model (~500MB).*

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
2.  It will open **Brave Browser** in "Stealth Mode".
3.  **Log in** to ChatGPT if prompted.
    *   *Note: If a CAPTCHA appears, the app waits **60 seconds** for you to solve it.*
4.  Press `Ctrl + Shift + M` to talk.
5.  Release the keys when done. The app will wait for the final word, aggregate the sentence, and send it to ChatGPT.

## 🔧 Troubleshooting

### "Verify you are human" / Cloudflare Loop
*   **Cause**: Automated browser detection.
*   **Fix**: The app uses "Stealth Mode" (User-Agent spoofing) to minimize this.
*   **Solution**: **Log In** to ChatGPT. Cloudflare trusts logged-in users more than anonymous ones.

### "No connection could be made..."
*   **Fix**: Check Terminal 1. If the Python server crashed, restart `python whisper_server.py`.

### "Cut off words"
*   **Fixed**: The app now flushes the audio buffer separately when you stop recording. Ensure you are running the latest version.

## 📁 Project Structure

*   `whisper_server.py`: The Python STT engine (Flask + Whisper).
*   `LiveMeetAI.App/`: Main C# Application.
    *   `BraveChatController.cs`: Handles Selenium automation & Stealth Mode.
    *   `AudioCaptureService.cs`: NAudio capture, Stereo handling, & Buffer flushing.
    *   `MainWindow.xaml.cs`: UI & Hotkey logic.

## 🛡️ Privacy
All transcription is done **locally** on your machine using Whisper. No audio is sent to the cloud. Only the final text is sent to ChatGPT for analysis.
