"""Real ONNX + API integration, using only synthetic answers and an isolated DB."""
import os,sys,ssl,subprocess,time,urllib.request,json,shutil,uuid
from pathlib import Path
root=Path(__file__).resolve().parents[1];fixture=Path(sys.argv[1])
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))
def request(method,path,body=None,token=None):
    h={'Content-Type':'application/json'}
    if token:h['Authorization']='Bearer '+token
    req=urllib.request.Request('https://127.0.0.1:18444'+path,data=None if body is None else json.dumps(body).encode(),headers=h,method=method)
    with opener.open(req,timeout=30) as r:return json.load(r)
env=os.environ.copy();env['SUPPORT_MODEL_DIR']=str(root/'models')
python=os.environ.get('SUPPORT_MODEL_PYTHON',sys.executable)
model=subprocess.Popen([str(python),str(root/'server/embedding.py')],env=env,creationflags=subprocess.CREATE_NO_WINDOW)
env.update(SUPPORT_HOME=str(fixture),SUPPORT_DB=str(fixture/('semantic-'+uuid.uuid4().hex+'.sqlite3')),SUPPORT_CERT=str(fixture/'test.crt'),SUPPORT_KEY=str(fixture/'test.key'),SUPPORT_PORT='18444')
log=open(fixture/'semantic-api.log','w')
api=subprocess.Popen([os.environ.get('SUPPORT_DOTNET','dotnet'),str(root/'src/SupportDesk.Api/bin/Release/net10.0/SupportDesk.Api.dll')],env=env,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
try:
    for i in range(100):
        try:request('GET','/health');break
        except Exception:time.sleep(.1)
    t=request('POST','/v2/auth/login',{'Password':'test-shared-password','Device':'semantic-fixture'})['AccessToken']
    titles=['Лаунчер закрывается после запуска','Не получается оплатить покупку картой','Забыл пароль от аккаунта']
    for i,title in enumerate(titles):request('PUT','/v2/answers/semantic-'+str(i),{'BaseVersion':0,'Answer':{'Id':'semantic-'+str(i),'Title':title,'Category':'Тест','Body':title,'Platform':'Все'}},t)
    for attempt in range(45):
        try:
            r=request('POST','/v2/search',{'Query':'после нажатия играть окно исчезает','Limit':3},t)
            if r['Hits'] and r['Hits'][0]['Id']=='semantic-0':break
        except urllib.error.HTTPError:pass
        time.sleep(1)
    else:raise RuntimeError('Index not ready')
    for query,expected in [('после нажатия играть окно исчезает','semantic-0'),('банк отклоняет платеж','semantic-1'),('не помню данные для входа','semantic-2')]:
        result=request('POST','/v2/search',{'Query':query,'Limit':3},t)
        assert result['Hits'][0]['Id']==expected,(query,result)
    print('PASS: real API+ONNX semantic indexing and Russian paraphrase ranking')
finally:
    api.terminate();model.terminate();api.wait(timeout=10);model.wait(timeout=10);log.close()
