"""CPU-only local embeddings. Listens on loopback; never sends text externally."""
import json, os, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

root = Path(os.environ.get('SUPPORT_MODEL_DIR', '/opt/cristalix-support/models'))
tokenizer = Tokenizer.from_file(str(root / 'tokenizer.json'))
tokenizer.enable_truncation(max_length=128)
tokenizer.enable_padding(pad_id=0, pad_token='[PAD]')
options = ort.SessionOptions()
options.intra_op_num_threads = 2
options.inter_op_num_threads = 1
session = ort.InferenceSession(str(root / 'model_quantized.onnx'), options, providers=['CPUExecutionProvider'])
lock = threading.Lock()
manifest = json.loads((root / 'manifest.json').read_text())
model = 'paraphrase-multilingual-MiniLM-L12-v2-onnx-int8:' + manifest['Revision']

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_POST(self):
        try:
            size = int(self.headers.get('Content-Length', '0'))
            if self.path != '/embed' or not 0 < size <= 60000: raise ValueError()
            data = json.loads(self.rfile.read(size))
            texts = data.get('Texts', data.get('texts'))
            if not isinstance(texts, list) or not 1 <= len(texts) <= 8 or any(not isinstance(t,str) or len(t)>5000 for t in texts): raise ValueError()
            with lock:
                batch = tokenizer.encode_batch(texts)
                values = {'input_ids': np.array([e.ids for e in batch],dtype=np.int64), 'attention_mask': np.array([e.attention_mask for e in batch],dtype=np.int64), 'token_type_ids': np.array([e.type_ids for e in batch],dtype=np.int64)}
                outputs = session.run(None, {i.name:values[i.name] for i in session.get_inputs()})
                mask = values['attention_mask'][...,None]
                vectors = (outputs[0]*mask).sum(axis=1)/np.maximum(mask.sum(axis=1),1)
                vectors /= np.maximum(np.linalg.norm(vectors,axis=1,keepdims=True),1e-12)
                body = json.dumps({'Vectors':vectors.tolist(),'Model':model}).encode()
            self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
        except (ValueError,KeyError,TypeError): self.send_error(400)
        except Exception: self.send_error(503)
    def setup(self): super().setup(); self.connection.settimeout(45)

if __name__ == '__main__':
    server = ThreadingHTTPServer(('127.0.0.1',8766),Handler)
    server.daemon_threads = True
    server.serve_forever()
