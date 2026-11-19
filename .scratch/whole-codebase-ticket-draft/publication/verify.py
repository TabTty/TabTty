import json, subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
root=Path(__file__).resolve().parent.parent
pub=root/'publication'
manifest=json.loads((root/'manifest.json').read_text(encoding='utf-8'))
journal=json.loads((pub/'journal.json').read_text(encoding='utf-8'))
repo=manifest['repository']
assert repo==journal['repository']=='TabTty/TabTty'
assert len(journal['entries'])==28 and all(x['status']=='created' for x in journal['entries'].values())
(pub/'snapshots').mkdir(exist_ok=True)

def fetch(pair):
    key,item=pair
    r=subprocess.run(['gh','issue','view',str(item['number']),'--repo',repo,'--json','number,title,body,state,url,labels,parent,blockedBy,subIssues,subIssuesSummary,assignees'],capture_output=True,text=True,encoding='utf-8')
    if r.returncode: raise RuntimeError(f'{key}: {r.stderr}')
    data=json.loads(r.stdout)
    (pub/'snapshots'/f'{key}.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return key,data

with ThreadPoolExecutor(max_workers=4) as pool:
    actual=dict(pool.map(fetch,journal['entries'].items()))
issues=[]
for key,expected in journal['entries'].items():
    data=actual[key]
    for name in ['number','title','url']:
        if data[name]!=expected[name]: issues.append(f'{key}: {name} mismatch')
    if data['state']!='OPEN' or data['assignees']: issues.append(f'{key}: not open and unassigned')
    if sorted(x['name'] for x in data['labels'])!=sorted(expected['labels']): issues.append(f'{key}: labels mismatch')
    parent=data.get('parent')
    if (parent['number'] if parent else None)!=expected['parent']: issues.append(f'{key}: parent mismatch')
    blockers=data.get('blockedBy') or {'nodes':[],'totalCount':0}
    got=sorted(x['number'] for x in blockers['nodes'])
    if got!=sorted(expected['blockedBy']) or blockers['totalCount']!=len(got): issues.append(f'{key}: blockers mismatch')
    body=(pub/expected['bodyFile']).read_text(encoding='utf-8')
    if data['body'].replace('\r\n','\n').rstrip()!=body.rstrip(): issues.append(f'{key}: body mismatch')
    if 'Draft ticket ' in data['body'] or 'identifier assigned on publication' in data['body'] or '## Blocked by' in data['body']: issues.append(f'{key}: unresolved draft section')
children=actual['map']['subIssues']
expected_children=[x['number'] for key,x in journal['entries'].items() if key!='map']
if sorted(x['number'] for x in children['nodes'])!=sorted(expected_children) or children['totalCount']!=27: issues.append('map: sub-issues mismatch')
if actual['map']['subIssuesSummary']['total']!=27: issues.append('map: summary mismatch')
frontier=[x['number'] for key,x in journal['entries'].items() if key!='map' and not x['blockedBy']]
result={'repository':repo,'map':journal['entries']['map']['url'],'children':27,'nativeParentEdges':27,'nativeBlockingEdges':sum(len(x['blockedBy']) for x in journal['entries'].values()),'initialFrontier':frontier,'issues':issues,'snapshots':'publication/snapshots'}
(pub/'verification.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,indent=2))
raise SystemExit(bool(issues))
