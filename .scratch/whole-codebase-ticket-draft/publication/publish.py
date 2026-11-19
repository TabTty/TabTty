import argparse, hashlib, json, re, subprocess
from pathlib import Path

root=Path(__file__).resolve().parent.parent
pub=root/'publication'
manifest=json.loads((root/'manifest.json').read_text(encoding='utf-8'))
repo='TabTty/TabTty'
assert manifest['repository']==repo and manifest['status'] in ('approved-for-publication','published')
parser=argparse.ArgumentParser()
parser.add_argument('--max-items',type=int,default=6)
args=parser.parse_args()

def gh(*args):
    return subprocess.run(['gh',*args],capture_output=True,text=True,encoding='utf-8')

def read_json(*args):
    r=gh(*args)
    if r.returncode: raise SystemExit(r.stderr or r.stdout)
    return json.loads(r.stdout)

actual=read_json('repo','view','--json','nameWithOwner')['nameWithOwner']
if actual!=repo: raise SystemExit('Unexpected repository: '+actual)
journal_path=pub/'journal.json'
journal=json.loads(journal_path.read_text(encoding='utf-8')) if journal_path.exists() else {'repository':repo,'approval':manifest['approval'],'entries':{}}
assert journal['repository']==repo

def save_journal():
    tmp=pub/'journal.json.tmp'
    tmp.write_text(json.dumps(journal,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    tmp.replace(journal_path)

items=[dict(manifest['map'],id='map',blockedBy=[])]+manifest['tickets']
created=0
for item in items:
    key=item['id']
    prior=journal['entries'].get(key)
    if prior:
        if prior.get('status')=='created': continue
        raise SystemExit('Prior incomplete publication requires read-only reconciliation: '+key)
    if created>=args.max_items: break
    if key=='map':
        body=(root/item['file']).read_text(encoding='utf-8')
        parent=None; blockers=[]
    else:
        parent=journal['entries']['map']['number']
        parent_url=journal['entries']['map']['url']
        body=(root/item['file']).read_text(encoding='utf-8')
        old='Whole-codebase ownership implementation plan (new map issue; identifier assigned on publication).'
        if body.count(old)!=1: raise SystemExit('Invalid parent placeholder: '+key)
        body=body.replace(old,f'[{manifest["map"]["title"]}]({parent_url})',1)
        if body.count('\n## Blocked by\n')!=1: raise SystemExit('Invalid blocker section: '+key)
        body=body.split('\n## Blocked by\n',1)[0].rstrip()+'\n'
        blockers=[journal['entries'][b]['number'] for b in item['blockedBy']]
    if 'Draft ticket ' in body or 'identifier assigned on publication' in body: raise SystemExit('Unresolved placeholder: '+key)
    body_path=pub/f'{key}.md'
    body_path.write_text(body,encoding='utf-8')
    record={'title':item['title'],'status':'issuing','bodyFile':body_path.name,'bodySha256':hashlib.sha256(body.encode('utf-8')).hexdigest(),'labels':item['labels'],'parent':parent,'blockedBy':blockers}
    journal['entries'][key]=record
    save_journal()
    command=['issue','create','--repo',repo,'--title',item['title'],'--body-file',str(body_path)]
    for label in item['labels']: command+=['--label',label]
    if parent is not None: command+=['--parent',str(parent)]
    if blockers: command+=['--blocked-by',','.join(map(str,blockers))]
    r=gh(*command)
    urls=re.findall(r'https://github\.com/TabTty/TabTty/issues/(\d+)',r.stdout)
    if len(urls)==1:
        record.update(number=int(urls[0]),url='https://github.com/TabTty/TabTty/issues/'+urls[0],status='created' if r.returncode==0 else 'created-needs-reconciliation')
    else:
        record['status']='uncertain'
    if r.returncode or len(urls)!=1:
        record['error']=(r.stderr+'\n'+r.stdout).strip()
        save_journal()
        raise SystemExit('Publication stopped; inspect journal before any retry: '+key+'\n'+record['error'])
    save_journal()
    print(f'{key}: #{record["number"]} {item["title"]}',flush=True)
    created+=1
print(f'Created this batch: {created}; journal created total: {sum(x.get("status")=="created" for x in journal["entries"].values())}/{len(items)}',flush=True)
