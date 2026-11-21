from contextlib import closing
import sqlite3
import tempfile
import unittest
from pathlib import Path
from read_reports import DNS, execute
from backup import copy_verified
from datetime import datetime
import json
from unittest.mock import MagicMock, patch
from operations_notify import notify


class Reports(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(dir=Path.cwd())
        self.root=Path(self.temp.name)
        self.db=self.root/"dns_all.sqlite"
        with closing(sqlite3.connect(self.db)) as db, db:
            db.execute("CREATE TABLE dns_all(id INTEGER PRIMARY KEY,start_time INTEGER,"+",".join(DNS[2:])+")")
            db.executemany("INSERT INTO dns_all(id,start_time,query) VALUES(?,?,?)",[(1,1730611800123,'ONE.example.'),(2,1730615400123,'two.example')])
    def tearDown(self):
        self.temp.cleanup()
    def test_paging_and_anchor(self):
        req=dict(operation="batch",source="dns",cursor=0,limit=1)
        first=execute(req,self.root)
        self.assertEqual(first['records'][0]['query'],'ONE.example.')
        second=execute(req|dict(cursor=1),self.root)
        self.assertEqual(second['anchor'],1730611800123)
        self.assertEqual([x['id'] for x in second['records']],[2])
        self.assertEqual(execute(req|dict(cursor=2),self.root)['records'],[])
        self.assertEqual(execute(req,self.root),first)
    def test_restricted(self):
        req=dict(operation="batch",source="dns",cursor=0,limit=1)
        for extra in [dict(limit=501),dict(cursor=True),dict(cursor=-1),dict(operation=".shell"),dict(sql="DELETE"),dict(source="../etc")]:
            with self.assertRaises(ValueError):execute(req|extra,self.root)
        self.assertEqual([c['name'] for c in execute(dict(operation="schema",source="dns"),self.root)['columns']],list(DNS))
    def test_connections_and_redacted_http(self):
        for kind,table in [('connections','conn_all'),('http','http_all')]:
            with closing(sqlite3.connect(self.root/(table+'.sqlite'))) as db,db:
                db.execute('CREATE TABLE '+table+'(id INTEGER PRIMARY KEY,start_time INTEGER,app_category TEXT,uri TEXT,src_username TEXT)')
                db.execute('INSERT INTO '+table+' VALUES(1,123,"Streaming","/video/sample?secret=x","private-user")')
            row=execute(dict(source=kind,operation="batch",cursor=0,limit=2),self.root)['records'][0]
            self.assertNotIn('src_username',row)
            if kind=='http':self.assertEqual(row['uri'],'/video/sample')
            else:self.assertEqual(row['app_category'],'Streaming')
    def test_external_notification_preferences_and_deduplication(self):
        config=self.root/"ntfy-notifications.json"
        settings={"Enabled":True,"QuietStart":22,"QuietEnd":6,"Events":{"service-failure":{"Enabled":True,"Priority":"max"}}}
        config.write_text(json.dumps(settings))
        reply=MagicMock()
        reply.__enter__.return_value.status=200
        with patch("operations_notify.urlopen",return_value=reply) as send:
            notify(self.root,"service-failure","https://example.invalid/topic",datetime(2026,1,1,23))
            send.assert_not_called()
            for _ in range(2):notify(self.root,"service-failure","https://example.invalid/topic",datetime(2026,1,1,12))
            send.assert_called_once()
            self.assertEqual(send.call_args.args[0].get_header("Priority"),"max")
        with closing(sqlite3.connect(self.root/"ntfy-delivery.db")) as db:
            self.assertEqual(db.execute("SELECT event,outcome FROM delivery").fetchall(),[("service-failure","delivered")])

    def test_backup_restore(self):
        snapshot=self.root/"backup.sqlite"
        copy_verified(self.db,snapshot)
        restored=self.root/"restored.sqlite"
        copy_verified(snapshot,restored)
        with closing(sqlite3.connect(restored)) as db:self.assertEqual(db.execute("SELECT count(*) FROM dns_all").fetchone()[0],2)


if __name__=="__main__":unittest.main()
