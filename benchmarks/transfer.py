"""Windows loopback benchmark and integrity checks for Blink server assemblies.

Example: py benchmarks/transfer.py --old C:/Blink8/builds/8.0.0 --new C:/Blink9/builds/9.0.0
Requires Python 3.9+ and the .NET Framework x64 C# compiler. Only synthetic local files are served.
"""
import argparse
import contextlib
import ctypes
import hashlib
import http.client
import itertools
import json
import os
from pathlib import Path
import random
import shutil
import statistics
import subprocess
import tempfile
import time
from urllib.parse import urlsplit
import zipfile

MIB = 1024 * 1024


def fixture(path, mib):
    rng = random.Random(7629)
    digest = hashlib.sha256()
    with path.open('wb') as out:
        for _ in range(mib):
            block = rng.randbytes(MIB)
            out.write(block)
            digest.update(block)
    return digest.hexdigest()


def make_server(app, root, harness):
    root.mkdir()
    for file in app.iterdir():
        if file.is_file() and (file.name in ('Blink.exe', 'Blink.exe.config') or file.suffix.lower() == '.dll'):
            shutil.copy2(file, root / file.name)
    csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    subprocess.run([str(csc), '/nologo', '/optimize+', '/platform:x64', '/r:Blink.exe',
                    '/out:Harness.exe', str(harness)], cwd=root, check=True)
    probe = root / 'Backend.cs'
    probe.write_text('using System; using System.Reflection; class Backend { static void Main(){'
                     'var type=typeof(Session).Assembly.GetType("TransferReader");'
                     'var flag=type==null?null:type.GetProperty("NativeAvailable", '
                     'BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);'
                     'Console.WriteLine(flag==null?"legacy":(bool)flag.GetValue(null,null)?"rust":"managed");}}')
    subprocess.run([str(csc), '/nologo', '/platform:x64', '/r:Blink.exe', '/out:Backend.exe', 'Backend.cs'],
                   cwd=root, check=True)
    return root / 'Harness.exe'


@contextlib.contextmanager
def session(server, sample):
    p = subprocess.Popen([str(server), 'send', str(sample)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    try:
        url = urlsplit(p.stdout.readline().strip())
        if url.hostname != '127.0.0.1' or not url.port:
            raise RuntimeError('Session failed to start')
        yield p, url
    finally:
        try:
            p.communicate('\n', timeout=5)
        except (BrokenPipeError, subprocess.TimeoutExpired):
            p.kill()
            p.wait()


def cpu_seconds(process):
    def count():
        return ctypes.c_ulonglong()
    created, ended, kernel, user = (count() for _ in range(4))
    if not ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(process._handle)),
            *(ctypes.byref(x) for x in (created, ended, kernel, user))):
        raise ctypes.WinError()
    return (kernel.value + user.value) / 10_000_000


def fetch(url, route, sink=None, byte_range=None):
    conn = http.client.HTTPConnection(url.hostname, url.port, timeout=40)
    try:
        conn.request('GET', url.path + route, headers={'Range': byte_range} if byte_range else {})
        response = conn.getresponse()
        if response.status != (206 if byte_range else 200):
            raise RuntimeError(f'unexpected HTTP {response.status}')
        expected = int(response.headers['Content-Length'])
        count = 0
        buf = bytearray(MIB)
        while True:
            n = response.readinto(buf)
            if not n:
                break
            if sink:
                sink(memoryview(buf)[:n])
            count += n
        if count != expected:
            raise RuntimeError('truncated transfer')
        return count
    finally:
        conn.close()


def correctness(server, sample, digest, root):
    with session(server, sample) as (_, url):
        got = hashlib.sha256()
        fetch(url, 'file/0', got.update)
        assert got.hexdigest() == digest
        with tempfile.TemporaryFile(dir=root) as archive:
            fetch(url, 'all.zip', archive.write)
            archive.seek(0)
            with zipfile.ZipFile(archive) as z:
                central_offset = z.start_dir
                assert z.namelist() == [sample.name]
                got = hashlib.sha256()
                with z.open(sample.name) as entry:
                    for block in iter(lambda: entry.read(MIB), b''):
                        got.update(block)
                assert got.hexdigest() == digest
            for lo, hi in [(0, 31), (MIB - 1, MIB + 2), (central_offset, central_offset + 15)]:
                archive.seek(lo)
                want = archive.read(hi - lo + 1)
                result = bytearray()
                fetch(url, 'all.zip', result.extend, f'bytes={lo}-{hi}')
                assert result == want
        tail = bytearray()
        fetch(url, 'file/0', tail.extend, 'bytes=-19')
        with sample.open('rb') as handle:
            handle.seek(-19, 2)
            assert tail == handle.read()


def check_revocation(server, sample):
    # Stop the session once a real download has begun; leave most of the response unread.
    with session(server, sample) as (process, url):
        conn = http.client.HTTPConnection(url.hostname, url.port, timeout=10)
        try:
            conn.request('GET', url.path + 'file/0')
            response = conn.getresponse()
            expected = int(response.headers['Content-Length'])
            delivered = len(response.read(1024))
            process.stdin.write('\n')
            process.stdin.flush()
            process.wait(timeout=5)
            try:
                while True:
                    part = response.read(MIB)
                    if not part:
                        break
                    delivered += len(part)
            except (ConnectionError, http.client.IncompleteRead):
                pass
            assert delivered < expected, 'revoked session delivered the entire file'
        finally:
            conn.close()


def run():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--old', type=Path, required=True)
    parser.add_argument('--new', type=Path, required=True)
    parser.add_argument('--managed', type=Path, help='Optional 3rd build with the Rust path disabled')
    parser.add_argument('--mib', type=int, default=256)
    parser.add_argument('--rounds', type=int, default=7)
    parser.add_argument('--json', type=Path)
    args = parser.parse_args()
    if args.mib < 2 or args.rounds < 3:
        parser.error('use at least 2 MiB and 3 rounds')
    harness = Path(__file__).resolve().parents[1] / 'source/Harness.cs'
    rows = []
    with tempfile.TemporaryDirectory(prefix='blink-bench-') as scratch:
        work = Path(scratch)
        sample = work / 'sample.bin'
        digest = fixture(sample, args.mib)
        variants = [('old', args.old), ('new', args.new)]
        if args.managed:
            variants.append(('managed', args.managed))
        servers = {name: make_server(path.resolve(), work / name, harness) for name, path in variants}
        backends = {name: subprocess.check_output([str(server.parent / 'Backend.exe')], text=True).strip()
                    for name, server in servers.items()}
        if args.managed and (backends['new'] != 'rust' or backends['managed'] != 'managed'):
            raise RuntimeError(f'Unexpected benchmark backends: {backends}')
        orders = list(itertools.permutations(servers))
        for name, server in servers.items():
            correctness(server, sample, digest, work)
            if args.mib >= 32:
                check_revocation(server, sample)
        for i in range(args.rounds + 1):
            for route in ['file/0', 'all.zip']:
                for name in orders[i % len(orders)]:
                    with session(servers[name], sample) as (process, url):
                        cpu = cpu_seconds(process)
                        start = time.perf_counter()
                        received = fetch(url, route)
                        seconds = time.perf_counter() - start
                        record = dict(round=i, version=name, route=route, seconds=seconds,
                                      mib_s=received / MIB / seconds, server_cpu_s=cpu_seconds(process) - cpu)
                        if i:
                            rows.append(record)
        binary_hashes = {name: hashlib.sha256((server.parent / 'Blink.exe').read_bytes()).hexdigest()
                         for name, server in servers.items()}
        result = {'fixture_mib': args.mib, 'rounds': args.rounds, 'sha256': digest, 'backends': backends,
                  'executable_sha256': binary_hashes, 'samples': rows, 'medians': {}}
        for name in servers:
            result['medians'][name] = {}
            for route in ['file/0', 'all.zip']:
                data = [r for r in rows if r['version'] == name and r['route'] == route]
                result['medians'][name][route] = {k: statistics.median(r[k] for r in data)
                                                   for k in ['mib_s', 'server_cpu_s']}
        print(json.dumps(result['medians'], indent=2))
        if args.json:
            args.json.write_text(json.dumps(result, indent=2) + '\n', encoding='utf8')


if __name__ == '__main__':
    run()
