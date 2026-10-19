# TempCleaner_x64.exe sonuna gomulu (degistirilmis AutoIt) verisini cozer.
import struct, sys
M=0xFFFFFFFF
def rotl(x,n): x&=M; return ((x<<n)|(x>>(32-n)))&M
class Lame:
    def __init__(s,seed):
        s.g=[0]*17; v=seed&0xffff
        for i in range(17):
            v=(v*((-0x53a9b4fb)&M)+1)&M; s.g[i]=v
        s.c0=0; s.c1=10
        for _ in range(9): s.step()
    def step(s):
        r=(rotl(s.g[s.c0],9)+rotl(s.g[s.c1],13))&M; s.g[s.c0]=r
        s.c0=16 if s.c0==0 else s.c0-1
        s.c1=16 if s.c1==0 else s.c1-1
        lo=(r<<20)&M; hi=(r>>12)|0x3ff00000
        return struct.unpack('<d',struct.pack('<II',lo,hi))[0]-1.0
    def byte(s):
        s.step(); v=int(s.step()*256.0); return min(v,255)
def dec(buf,key):
    l=Lame(key&0xffff); return bytes(b^l.byte() for b in buf)
if __name__=='__main__':
    data=open(sys.argv[1],'rb').read()
    p=0xcc600+24
    print('header16', dec(data[p-24+0:p-24+16] if False else data[p-24:p-24+0] or data[0xcc600:0xcc600+0], 0).hex())
    entries=[]; p=0xcc600+24
    hdr=dec(data[p:p+16],0x839c); p+=16
    while True:
        mk=dec(data[p:p+4],0xc890); p+=4
        if mk!=bytes.fromhex('e021a1b1'): print('bitis', mk.hex(), hex(p)); break
        n1=struct.unpack_from('<I',data,p)[0]^0x3cd117d8; p+=4
        t=dec(data[p:p+n1*2],(n1+0x8aa3)&0xffff).decode('utf-16le'); p+=n1*2
        n2=struct.unpack_from('<I',data,p)[0]^0x21da691d; p+=4
        name=dec(data[p:p+n2*2],(n2+0x2cc0)&0xffff).decode('utf-16le'); p+=n2*2
        comp=data[p]; p+=1
        dsz=struct.unpack_from('<I',data,p)[0]^0x87bc; p+=4
        usz=struct.unpack_from('<I',data,p)[0]^0x87bc; p+=4
        crc=struct.unpack_from('<I',data,p)[0]^0xa685; p+=4
        p+=16
        raw=data[p:p+dsz]; p+=dsz
        print(repr(t),repr(name),'comp',comp,'dsz',dsz,'usz',usz)
        entries.append((t,name,comp,raw,usz,crc))
    import pickle; pickle.dump(entries,open(sys.argv[2],'wb'))
