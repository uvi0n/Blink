import contextlib,subprocess,tempfile,urllib.request as u,urllib.error,hashlib,os,pathlib
@contextlib.contextmanager
def run(mode,path):
    with subprocess.Popen(['Harness.exe',mode,str(path)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True) as proc:
        url=proc.stdout.readline().strip()
        assert url,proc.poll()
        try:yield url
        finally:proc.communicate('\n')
def fetch(url,headers={},data=None):
    try:
        with u.urlopen(u.Request(url,headers=headers,data=data),timeout=8) as r:return r.status,r.read()
    except urllib.error.HTTPError as e:return e.code,e.read()
with tempfile.TemporaryDirectory(dir='.') as tmp:
    path=pathlib.Path(tmp).absolute()/'test.bin'
    path.touch()
    subprocess.run(['fsutil','sparse','setflag',str(path)],check=True)
    with path.open('r+b') as f:f.seek(50*1024**3-4);f.write(b'TAIL')
    with run('send',path) as url:
        assert fetch(url)[0]==200
        assert fetch(url+'file',{'Range':'bytes=-4'})==(206,b'TAIL')
        assert fetch(url+'file',{'Range':'bytes=53687091196-'})==(206,b'TAIL')
        assert fetch(url+'file',{'Range':'bytes=53687091200-'})[0]==416
        assert fetch(url.split('/',3)[0]+'//'+url.split('/')[2]+'/bad/')[0]==404
    print('50 GiB length, suffix/offset ranges, invalid ranges and secret path OK')
    for parts in [[os.urandom(4194304),os.urandom(4194304),b'end'],[b'']]:
        dest=path.parent/('out'+str(len(parts)))
        with run('receive',dest) as url:
            position=0
            for part in parts:
                headers={'X-Size':str(sum(map(len,parts))),'X-Offset':str(position),'X-SHA256':'bad'}
                assert fetch(url+'chunk',headers,part)[0]==400
                headers['X-SHA256']=hashlib.sha256(part).hexdigest()
                position+=len(part)
                for i in range(2):assert fetch(url+'chunk',headers,part)==(200,str(position).encode())
            assert dest.read_bytes()==b''.join(parts)
    print('Chunk verification, exact output, retry including final acknowledgement, empty file OK')


    with run('receive',path.parent) as url:
        data=os.urandom(65539)
        h={'X-Size':str(len(data)),'X-Offset':'0','X-SHA256':hashlib.sha256(data).hexdigest(),'X-Name':'..%5cescape.txt'}
        assert fetch(url+'chunk',h,data)[0]==400
        from urllib.parse import quote
        name='Ролик.mp4'
        h['X-Name']=quote(name)
        (path.parent/name).write_bytes(b'original')
        assert fetch(url+'chunk',h,data)[0]==200
        assert (path.parent/name).read_bytes()==b'original'
        assert (path.parent/'Ролик (1).mp4').read_bytes()==data
    print('Folder save: exact bytes, Unicode original name, collision handling and path validation OK')
