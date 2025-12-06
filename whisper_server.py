from flask import Flask, request, jsonify
import whisper, tempfile, os, traceback, time, sys, threading
import numpy as np

app = Flask(__name__)
model_lock = threading.Lock()

print("Loading Whisper model (this can take a while)...", flush=True)
model = whisper.load_model("small")  # Upgrade from 'base' to 'small' for better accuracy
print("Model loaded.", flush=True)

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
            
            # Threshold: 0.008 is a reasonable cutoff for empty background noise
            if rms < 0.008:
                print("Detected silence. Skipping.", flush=True)
                return jsonify({'text': ''})

            # bias towards English/Hindi mixed usage, prevent silence hallucinations
            # Force English. Using a generic clear prompt often works better than forcing an accent.
            prompt_text = "This is a clean transcript of a conversation in professional English. The topic is programming, specifically Flutter, Dart, mixins, state management, Provider, Riverpod, Bloc, widgets, stateless, stateful, code, syntax, and anagrams."
            result = model.transcribe(
                saved, 
                fp16=False,
                language="en", 
                initial_prompt=prompt_text,
                condition_on_previous_text=False,
                temperature=0.0
            )

            # Fix leakage: If Whisper just repeats the prompt, discard it.
            txt = result.get('text', '').strip()
            if prompt_text.lower() in txt.lower() or "transcript of a" in txt.lower():
                 print(f"Ignored prompt leakage: {txt}", flush=True)
                 result['text'] = ""
            
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
