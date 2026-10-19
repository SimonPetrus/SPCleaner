import re,sys
src=open(sys.argv[1],encoding='utf-8').read()
raw=open(sys.argv[2],'rb').read().decode('latin-1')
parts=raw.split('oC4t'); OS=['%d'%len(parts)]+parts
def h(x):
    try: return bytes.fromhex(x).decode('utf-8',errors='replace')
    except Exception: return None
dec={i:h(v) for i,v in enumerate(OS) if i>0}
q=lambda s:'"'+s.replace('"','""')+'"'
var={}
for m in re.finditer(r'\$(A\w+) = A1100004838\(\$OS\[(\d+)\]\)',src): var[m.group(1)]=dec[int(m.group(2))]
out=re.sub(r'A1100004838\(\$OS\[(\d+)\]\)',lambda m:q(dec[int(m.group(1))]),src)
out=re.sub(r'A1100004838\("([0-9A-Fa-f]*)"\)',lambda m:q(h(m.group(1))),out)
def rep(m):
    n=m.group(1); return q(var[n]) if n in var else m.group(0)
lines=[]
for line in out.split('\n'):
    if line.startswith('GLOBAL $A5010F03429') or line.startswith('GLOBAL $A198010240B'):
        lines.append('; [string tablosu buraya gomuldu]'); continue
    lines.append(re.sub(r'\$(A\w+)(?!\w)',rep,line))
open(sys.argv[3],'w',encoding='utf-8').write('\n'.join(lines))
print('string tablosu girdisi:',len([k for k in dec if dec[k] is not None]))
