import sys,struct
d=open(sys.argv[1],'rb').read()
OPS={0x40:",",0x41:"=",0x42:">",0x43:"<",0x44:"<>",0x45:">=",0x46:"<=",0x47:"(",0x48:")",0x49:"+",0x4A:"-",0x4B:"/",0x4C:"*",0x4D:"&",0x4E:"[",0x4F:"]",0x50:"==",0x51:"^",0x52:"+=",0x53:"-=",0x54:"/=",0x55:"*=",0x56:"&=",0x57:"?",0x58:":"}
p=0; nlines=struct.unpack_from('<I',d,p)[0]; p+=4
lines=[]; cur=[]
def s():
    global p
    n=struct.unpack_from('<I',d,p)[0]; p+=4
    raw=d[p:p+n*2]; p+=n*2
    k=n&0xff
    return bytes(b^k if i%2==0 else b^((n>>8)&0xff) for i,b in enumerate(raw)).decode('utf-16le',errors='replace')
while p<len(d):
    t=d[p]; p+=1
    if t==0x7f: lines.append(cur); cur=[]
    elif t in (0x00,0x01): v=struct.unpack_from('<I',d,p)[0]; p+=4; cur.append(f'<{"KW" if t==0 else "FN"}{v}>')
    elif t==0x05: cur.append(str(struct.unpack_from('<i',d,p)[0])); p+=4
    elif t==0x10: cur.append(str(struct.unpack_from('<q',d,p)[0])); p+=8
    elif t==0x20: cur.append(repr(struct.unpack_from('<d',d,p)[0])); p+=8
    elif 0x30<=t<=0x3f:
        v=s(); cur.append({0x32:'@'+v,0x33:'$'+v,0x35:'.'+v,0x36:'"'+v.replace('"','""')+'"'}.get(t,v))
    elif t in OPS: cur.append(OPS[t])
    else: cur.append(f'<?{t:x}>')
out=[]
for l in lines:
    x=''
    for i,tok in enumerate(l):
        if i and not (tok in ('(',')','[',']',',') or x.endswith(('(','['))): x+=' '
        if tok==',': x+=','; continue
        x+=tok
    out.append(x)
open(sys.argv[2],'w',encoding='utf-8').write('\n'.join(out))
print('satir',len(lines))
