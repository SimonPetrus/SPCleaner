import sys,pickle,struct
sys.path.insert(0,sys.argv[1]); from _lib import dec
class BR:
    def __init__(s,d): s.d=d; s.p=0; s.acc=0; s.n=0
    def bits(s,k):
        s.acc&=0xffff
        for _ in range(k):
            if s.n==0:
                hi=s.d[s.p] if s.p<len(s.d) else 0; lo=s.d[s.p+1] if s.p+1<len(s.d) else 0
                s.acc|=(hi<<8)|lo; s.p+=2; s.n=16
            s.acc=(s.acc<<1)&0xffffffff; s.n-=1
        return s.acc>>16
def length(b):
    v=b.bits(2); a=0
    if v==3:
        v=b.bits(3); a=3
        if v==7:
            a=10; v=b.bits(5)
            if v==0x1f:
                a=0x29; v=b.bits(8)
                if v==0xff:
                    a=0x128
                    while True:
                        v=b.bits(8)
                        if v!=0xff: break
                        a+=0xff
    return v+3+a
def unlz(d):
    assert d[:4]==bytes.fromhex('e9794725'); n=struct.unpack('>I',d[4:8])[0]
    b=BR(d[8:]); o=bytearray()
    while len(o)<n:
        if b.bits(1)==1: o.append(b.bits(8))
        else:
            off=b.bits(15); ln=length(b); st=len(o)-off
            for i in range(ln): o.append(o[st+i])
    return bytes(o[:n])
E=pickle.load(open(sys.argv[2],'rb'))
for i,(t,name,comp,raw,usz,crc) in enumerate(E):
    out=unlz(dec(raw,0xf008)); open(f'{sys.argv[3]}/entry{i}.bin','wb').write(out)
    print(i,len(out))
