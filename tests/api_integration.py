"""Integration test against an isolated TLS fixture. Never uses production data."""
import json,ssl,urllib.request,urllib.error,sys,time
url=sys.argv[1]
context=ssl._create_unverified_context()  # isolated self-signed test fixture only
def call(method,path,body=None,token=None):
    data=None if body is None else json.dumps(body).encode()
    headers={'Content-Type':'application/json'}
    if token: headers['Authorization']='Bearer '+token
    try:
        with urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=context)).open(urllib.request.Request(url+path,data=data,headers=headers,method=method),timeout=15) as r:return r.status,json.load(r)
    except urllib.error.HTTPError as e:return e.code,e.read().decode()
assert call('GET','/v2/answers')[0]==401
assert call('GET','/v2/changes?since=-1')[0]==401
assert call('POST','/v2/search',{'Query':'test'})[0]==401
assert call('POST','/v2/auth/login',{'Password':'wrong','Device':'fixture'})[0]==401
code,session=call('POST','/v2/auth/login',{'Password':'test-shared-password','Device':'fixture'});assert code==200
t=session['AccessToken']
code,snap=call('GET','/v2/answers',token=t);assert code==200 and snap['Answers']==[]
a={'Id':'fixture-a','Title':'Проблема','Category':'Тест','Body':'Дословный ответ\nhttps://example.com','Platform':'Linux','Kind':'reply','Notes':'','Source':'','NeedsReview':False}
assert call('PUT','/v2/answers/fixture-a',{'BaseVersion':0,'Answer':a},t)[0]==200
code,delta=call('GET','/v2/changes?since=3',token=t);assert code==200 and delta['Answers'][0]['Body']==a['Body']
assert call('PUT','/v2/answers/fixture-a',{'BaseVersion':0,'Answer':a},t)[0]==409
code,s=call('POST','/v2/auth/refresh',{'RefreshToken':session['RefreshToken']});assert code==200
assert call('GET','/v2/answers',token=t)[0]==401
t=s['AccessToken']
assert call('POST','/v2/auth/refresh',{'RefreshToken':session['RefreshToken']})[0]==401
a['Responses']=[{'Id':'first','Title':'Шаг проверки','Body':a['Body'],'Step':1,'When':''},{'Id':'second','Title':'Вариант','Body':'Другой дословный ответ\nЁ Ъ','Step':2,'When':'Если проверка не помогла'}]
assert call('PUT','/v2/answers/fixture-a',{'BaseVersion':1,'Answer':a},t)[0]==200
code,snap=call('GET','/v2/answers',token=t);assert snap['Answers'][0]['Responses'][1]['Body']==a['Responses'][1]['Body']
old=dict(a);old.pop('Responses');old['Body']='Правка из старого клиента'
assert call('PUT','/v2/answers/fixture-a',{'BaseVersion':2,'Answer':old},t)[0]==200
code,snap=call('GET','/v2/answers',token=t);assert snap['Answers'][0]['Responses'][0]['Body']==old['Body'] and len(snap['Answers'][0]['Responses'])==2
if '127.0.0.1:18444' in url:
    for _ in range(60):
        code,result=call('POST','/v2/search',{'Query':'Другой дословный ответ'},t)
        if code==200 and not result['Indexing']:
            assert result['Hits'][0]['Id']=='fixture-a';break
        time.sleep(.5)
    else:raise AssertionError('Semantic alternatives index did not become ready')
assert call('DELETE','/v2/answers/fixture-a',{'BaseVersion':3},t)[0]==200
assert call('POST','/v2/auth/logout',{},t)[0]==200
assert call('GET','/v2/answers',token=t)[0]==401
print('PASS: anonymous isolation, TLS, login, CRUD, CAS, incremental sync, refresh replay, logout')
