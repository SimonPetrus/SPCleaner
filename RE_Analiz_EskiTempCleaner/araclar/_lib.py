import struct
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
