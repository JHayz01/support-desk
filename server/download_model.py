"""Download model assets once. No custom model code is executed."""
import hashlib,json,os,urllib.request
from pathlib import Path
root=Path(os.environ.get('SUPPORT_MODEL_DIR','/opt/cristalix-support/models'));root.mkdir(parents=True,exist_ok=True)
repo='Xenova/paraphrase-multilingual-MiniLM-L12-v2'
revision=json.load(urllib.request.urlopen('https://huggingface.co/api/models/'+repo))['sha']
manifest={'Repository':repo,'Revision':revision,'Files':{}}
for source,target in [('tokenizer.json','tokenizer.json'),('onnx/model_quantized.onnx','model_quantized.onnx')]:
    url=f'https://huggingface.co/{repo}/resolve/{revision}/{source}'
    temp=root/(target+'.tmp')
    urllib.request.urlretrieve(url,temp)
    temp.replace(root/target)
    manifest['Files'][target]=hashlib.sha256((root/target).read_bytes()).hexdigest()
(root/'manifest.json').write_text(json.dumps(manifest,indent=2))
print('Model assets downloaded; revision:',revision)
