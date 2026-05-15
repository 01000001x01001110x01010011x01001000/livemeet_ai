from flask import Flask, request, jsonify
import whisper, os, traceback, time, threading
import numpy as np
import torch

app = Flask(__name__)
model_lock = threading.Lock()

device = "cuda" if torch.cuda.is_available() else "cpu"
print(f"Using device: {device}", flush=True)
if device == "cuda":
    print(f"GPU: {torch.cuda.get_device_name(0)}", flush=True)

print("Loading Whisper model (this can take a while)...", flush=True)
model_name = os.environ.get("WHISPER_MODEL", "medium")
model = whisper.load_model(model_name, device=device)
print(f"Model '{model_name}' loaded on {device}.", flush=True)

@app.route("/inference", methods=["POST"])
def inference():
    try:
        if 'file' not in request.files:
            return jsonify({"error":"no file"}), 400
        file = request.files['file']

        # save incoming file to a temp path we can inspect
        ts = int(time.time()*1000)
        saved = f"C:/Temp/whisper_in_{ts}.wav"
        os.makedirs(os.path.dirname(saved), exist_ok=True)
        file.save(saved)
        print(f"Saved incoming file to {saved}", flush=True)

        # Check for empty file
        if os.path.getsize(saved) < 100:
            return jsonify({"error":"file_too_small"}), 400

        # Lock is CRITICAL because whisper model state is not thread-safe (KV cache)
        with model_lock:
             # 1. Silence Detection to prevent hallucinations
            # Load audio using Whisper's internal tool (resamples to 16k, normalizes to -1..1)
            audio = whisper.load_audio(saved)
            
            # Check RMS amplitude
            rms = np.sqrt(np.mean(audio**2))
            print(f"Audio RMS: {rms:.4f}", flush=True)
            
            # Keep this threshold conservative so quieter voices are not skipped.
            if rms < 0.003:
                print("Detected silence. Skipping.", flush=True)
                return jsonify({'text': ''})

            # Avoid a strong initial prompt because it can inject unrelated words.
            result = model.transcribe(
                audio,
                fp16=(device == "cuda"),
                language="en",
                task="transcribe",
                condition_on_previous_text=False,
                temperature=0.0,
                beam_size=5,
                best_of=5,
                no_speech_threshold=0.45,
                compression_ratio_threshold=2.4,
                logprob_threshold=-1.0
            )
            
        print(f"Transcribed: {result.get('text','(no text)')[:120]}", flush=True)
        return {"text": result["text"]}
    except Exception as e:
        # save traceback and file info for debugging
        tb = traceback.format_exc()
        try:
            errfile = saved + ".error.txt"
            with open(errfile, "w", encoding="utf-8") as f:
                f.write("EXCEPTION:\n")
                f.write(tb)
                f.write("\n")
            print(f"Transcription failed. Saved error to: {errfile}", flush=True)
            return jsonify({"error":"transcription_failed", "detail": str(e), "saved": saved, "errfile": errfile}), 500
        except: 
            print("Transcription failed drastically.")
            return jsonify({"error":"transcription_failed", "detail": str(e)}), 500

if __name__ == "__main__":
    # run the server (set host to 127.0.0.1 and port to 8080)
    print("Starting Flask server on http://127.0.0.1:8080", flush=True)
    # threaded=False to prevent race conditions on model state
    app.run(host="127.0.0.1", port=8080, threaded=False, debug=False)
