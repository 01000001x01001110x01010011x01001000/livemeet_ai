from flask import Flask, request, jsonify
import whisper, tempfile, os, traceback, time, sys

app = Flask(__name__)

print("Loading Whisper model (this can take a while)...", flush=True)
model = whisper.load_model("base")  # 'base' is more accurate than 'tiny'
print("Model loaded.", flush=True)

@app.route("/inference", methods=["POST"])
def inference():
    file = request.files.get("file")
    if file is None:
        return jsonify({"error":"no file"}), 400

    # save incoming file to a temp path we can inspect
    ts = int(time.time()*1000)
    saved = f"C:/Temp/whisper_in_{ts}.wav"
    os.makedirs(os.path.dirname(saved), exist_ok=True)
    file.save(saved)
    print(f"Saved incoming file to {saved}", flush=True)

    try:
        result = model.transcribe(saved)
        print(f"Transcribed: {result.get('text','(no text)')[:120]}", flush=True)
        return {"text": result["text"]}
    except Exception as e:
        # save traceback and file info for debugging
        tb = traceback.format_exc()
        errfile = saved + ".error.txt"
        with open(errfile, "w", encoding="utf-8") as f:
            f.write("EXCEPTION:\n")
            f.write(tb)
            f.write("\n")
        print(f"Transcription failed. Saved error to: {errfile}", flush=True)
        # respond with helpful info for diagnosis
        return jsonify({"error":"transcription_failed", "detail": str(e), "saved": saved, "errfile": errfile}), 500

if __name__ == "__main__":
    # run the server (set host to 127.0.0.1 and port to 8080)
    print("Starting Flask server on http://127.0.0.1:8080", flush=True)
    # threaded=True allows multiple requests; use debug=False in production
    app.run(host="127.0.0.1", port=8080, threaded=True, debug=False)
