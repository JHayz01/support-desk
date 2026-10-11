import os,sys,ssl,subprocess,time,urllib.request,shutil,json,threading
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
root=Path(__file__).resolve().parents[1]
workspace=root.parent
dotnet=os.environ.get("SUPPORT_DOTNET") or shutil.which("dotnet")
fixture=Path(sys.argv[1])
env=os.environ.copy();env.update(SUPPORT_HOME=str(fixture),SUPPORT_DB=str(fixture/'test.sqlite3'),SUPPORT_CERT=str(fixture/'test.crt'),SUPPORT_KEY=str(fixture/'test.key'),SUPPORT_PORT='18444')
embedded=[]
class ModelFixture(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_POST(self):
        assert self.request_version=='HTTP/1.0'
        size=int(self.headers.get('Content-Length','0'));assert 0<size<=60000
        texts=json.loads(self.rfile.read(size))['Texts'];embedded.extend(texts)
        body=json.dumps(dict(Model='synthetic-fixture',Vectors=[[1,0,0] for _ in texts])).encode()
        self.send_response(200);self.send_header('Content-Length',str(len(body)));self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
model=ThreadingHTTPServer(('127.0.0.1',18446),ModelFixture)
threading.Thread(target=model.serve_forever,daemon=True).start()
env['SUPPORT_EMBED_URL']='http://127.0.0.1:18446'
log=open(fixture/'api-test.log','w')
p=subprocess.Popen([dotnet,str(root/'src/SupportDesk.Api/bin/Release/net10.0/SupportDesk.Api.dll')],env=env,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
try:
    for i in range(100):
        if p.poll() is not None: raise RuntimeError((fixture/'api-test.log').read_text())
        try:
            urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=ssl._create_unverified_context())).open('https://127.0.0.1:18444/health',timeout=1).close();break
        except Exception:time.sleep(.1)
    else:raise RuntimeError('API did not start')
    subprocess.run([sys.executable,str(Path(__file__).with_name('api_integration.py')),'https://127.0.0.1:18444'],check=True)
    assert any('Другой дословный ответ' in text for text in embedded),'Alternative was never embedded'
    print('PASS: background index embeds later variants over bounded HTTP/1.0 requests')
finally:
    p.terminate();p.wait(timeout=10);log.close();model.shutdown();model.server_close()
