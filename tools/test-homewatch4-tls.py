import json
import os
import socket
import sqlite3
import subprocess
import tempfile
import shutil
import threading
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

root = Path(__file__).resolve().parents[1]
scratch = tempfile.TemporaryDirectory(dir=root.parent, ignore_cleanup_errors=True)
data = Path(scratch.name)
now = datetime.now(timezone.utc).replace(microsecond=0)
start = now - timedelta(seconds=30)
end = now - timedelta(seconds=10)

def alert(port=50000, address='192.0.2.1', stamp=start, remote='198.51.100.2', **changes):
    event = dict(src_ip=remote, src_port='443', dest_ip=address, dest_port=port,
                 timestamp=stamp.strftime('%Y-%m-%dT%H:%M:%S.000000%z'), proto='TCP',
                 tls=dict(sni='suricata.example.net', version='TLS 1.3',
                          ja3=dict(hash='client-fingerprint'), ja3s=dict(hash='server-fingerprint'),
                          subject='CN=suricata.example.net', issuerdn='CN=example CA'))
    return event | changes

valid = alert(remote='::ffff:198.51.100.2', stamp=start-timedelta(seconds=2), flow_id=12345)
rows = [valid, valid, alert(port=50001, stamp=start - timedelta(minutes=2)),
        alert(port=50002, address='192.0.2.3'), alert(port=50003),
        alert(port=50004, proto='UDP'), alert(port=50005),
        alert(port=50006, src_port=8443), alert(port=50007, tls=None, proto='UDP',
              quic=dict(sni='quic.example.net', version='1', ja4='quic-ja4'))]
unavailable = False

class Ids(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != '/api/ids/service/queryAlerts':
            self.send_error(404)
            return
        self.send_response(503 if unavailable else 200)
        self.end_headers()
        self.wfile.write(json.dumps(dict(rows=rows)).encode())
    def log_message(self, *args):
        pass

server = ThreadingHTTPServer(('127.0.0.1', 0), Ids)
threading.Thread(target=server.serve_forever, daemon=True).start()
with socket.socket() as sock:
    sock.bind(('127.0.0.1',0))
    app_port=sock.getsockname()[1]
env = os.environ | {
    'HomeWatch__ListenUrl': f'http://127.0.0.1:{app_port}',
    'Ntopng__Enabled':'false',
    'HomeWatch__DataPath': str(data),
    'Authentication__Enabled': 'false',
    'Opnsense__BaseUrl': f'http://127.0.0.1:{server.server_port}',
    'Opnsense__ApiKey': 'local-test',
    'Opnsense__ApiSecret': 'local-test',
}
base = env['HomeWatch__ListenUrl']

def get(path):
    with urllib.request.urlopen(base + path, timeout=5) as response:
        return json.load(response)

def date(value):
    return value.strftime('%Y-%m-%d %H:%M:%S')

with (data / 'app.log').open('w') as log:
    app = subprocess.Popen([os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet'),
                            root / 'HomeWatch3/bin/Release/net8.0/HomeWatch3.dll'],
                           cwd=root / 'HomeWatch3', env=env, stdout=log, stderr=log)
    try:
        for _ in range(50):
            try:
                get('/api/status')
                break
            except OSError:
                if app.poll() is not None:
                    raise RuntimeError('HomeWatch3 exited')
                time.sleep(.2)
        else:
            raise RuntimeError('HomeWatch3 did not start')
        with sqlite3.connect(data / 'homewatch3.db') as db:
            db.execute('DELETE FROM TrafficEvents')
            db.execute('INSERT OR IGNORE INTO Devices (Id, Name, LastIpAddress, FirstSeenUtc, LastSeenUtc) VALUES (501, ?, ?, ?, ?)',
                       ('Fixture device', '192.0.2.1', date(start), date(end)))
            for id, port in enumerate([50000, 50001, 50002, 50003, 50003, 50004, None, 50006, 50007, 50008, 50009, 50010], 501):
                db.execute('''INSERT INTO TrafficEvents
                    (Id, Source, DeviceId, SourceIp, SourcePort, DestinationIp, DestinationPort,
                     TimestampUtc, StartedUtc, LastSeenUtc, Encrypted, Visibility, Confidence,
                     Blocked, Protocol, BytesDown, TlsVersion)
                    VALUES (?, 'ntopng-flow', 501, '192.0.2.1', ?, '198.51.100.2', 443,
                            ?, ?, ?, 1, 'encrypted', 45, 0, ?, 100, ?)''',
                           (id, port, date(end), date(start if port is not None else end), date(end),
                            'UDP/QUIC' if port in (50007,50008) else 'UDP' if port == 50009 else 'TCP/TLS', 'native' if id == 501 else None))
        path = '/api/tls-inspection/flows?minutes=60&deviceId=501'
        result = get(path)
        flows = {flow['id']: flow for flow in result['flows']}
        assert len(flows) == result['summary']['encryptedFlows'] == 12
        assert result['summary']['bytesDown'] == 1200
        assert {id for id, flow in flows.items() if flow['suricataEnriched']} == {501, 509}
        assert flows[501]['hostname'] == 'suricata.example.net' and flows[501]['tlsVersion'] == 'native'
        assert flows[501]['tlsServerFingerprint'] == 'server-fingerprint'
        assert flows[501]['certificateIssuer'] == 'CN=example CA'
        assert flows[509]['tlsClientFingerprint'] == 'quic-ja4'
        assert [flow['id'] for flow in get(path + '&search=suricata.example.net')['flows']] == [501]
        with sqlite3.connect(data / 'homewatch3.db') as db:
            assert db.execute('SELECT count(*), count(TlsServerName) FROM TrafficEvents').fetchone() == (12, 0)
        def status(id): return flows[id]['enrichment']['status']
        assert status(502)=='unmatched' and 'time' in flows[502]['enrichment']['reason']
        assert status(504)==status(505)=='unmatched' and 'Multiple' in flows[504]['enrichment']['reason']
        assert status(507)=='unknown' and status(510)=='quic' and status(511)=='unobserved'
        assert flows[509]['tlsVersion'] is None
        assert status(508)=='unmatched' and 'ports' in flows[508]['enrichment']['reason']
        assert result['collection']['matches']==2, 'duplicate alert counted more than once'
        # Translated remote endpoint alone cannot authorize enrichment.
        rows[:]=[alert(port=50010,remote='203.0.113.77')]
        assert not next(f for f in get(path)['flows'] if f['id']==512)['suricataEnriched']
        rows[:]=[alert(port=50010, tls=dict(sni='partial.example.net'))]
        only_sni=next(f for f in get(path)['flows'] if f['id']==512)
        assert only_sni['enrichment']['status']=='enriched' and only_sni['tlsVersion'] is None
        with sqlite3.connect(data / 'homewatch3.db') as db:
            db.execute("UPDATE Devices SET MacAddress='02:00:00:00:05:01' WHERE Id=501")
        with sqlite3.connect(data / 'zenarmor.db') as db:
            for n,port,mac in [(1,50010,'02:00:00:00:05:01'),(2,50003,'02:00:00:00:05:01'),(3,50001,'02:00:00:00:05:02')]:
                record=dict(src_hwaddr=mac,ip_src_saddr='192.0.2.1',ip_dst_saddr='198.51.100.2',ip_dst_port=443,
                    ip_src_port=port,transport_proto='TCP',tls_version='TLS 1.3',ja4='zen-fixture',sni='zen.fixture')
                db.execute("INSERT INTO dns VALUES('tls',0,?,?,'zen.fixture','','fixture',0,?,'fixture')",(n,int(start.timestamp()*1000),json.dumps(record)))
        enriched=get(path+'&search=zen-fixture')['flows']
        assert len(enriched)==1 and enriched[0]['id']==512 and enriched[0]['zenarmorHandshake']['version']=='TLS 1.3'
        assert get(path)['summary']['bytesDown']==1200
        rows[:]=[alert(alert={"signature":"Fixture detection","category":"fixture","severity":2,"action":"blocked"})]*2
        get('/api/opnsense/ids/alerts?limit=500')
        get('/api/opnsense/ids/alerts?limit=500')
        retained=get('/api/security/history?search=Fixture%20detection')
        assert len(retained)==1 and retained[0]['source']=='suricata' and retained[0]['blocked']
        note_url=base+'/api/zenarmor/investigation/suricata/'+str(retained[0]['epoch'])+'/'+str(retained[0]['id'])
        req=urllib.request.Request(note_url,data=json.dumps(dict(acknowledged=True,note='Checked fixture')).encode(),
            headers={'Content-Type':'application/json'},method='POST')
        with urllib.request.urlopen(req) as resp: assert resp.status==200
        assert get('/api/security/history?search=Fixture%20detection')[0]['acknowledged']
        unavailable = True
        result = get(path)
        assert len(result['flows']) == 12 and not result['collection']['suricataAvailable']
        assert result['collection']['suricataLastSuccessUtc']
        assert next(f for f in result['flows'] if f['id']==501)['suricataEnriched']
        print('PASS: TLS endpoint enrichment, QUIC, search, ambiguity, tuple and time mismatches, legacy rows, deduplication, and IDS outage')
    finally:
        app.terminate()
        app.wait(timeout=5)
        server.shutdown()
        server.server_close()
scratch.cleanup()
