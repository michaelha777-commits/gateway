from contextlib import closing
from pathlib import Path
import json
import os
import sqlite3
import tempfile
import unittest
from nas_database import discover, configured


class NasDatabaseTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.app=self.root/"app"
        self.app.mkdir()
        self.out=self.app/"bin/Release/net8.0"
        self.out.mkdir(parents=True)
        self.process=self.root/"process"
        self.process.mkdir()
        (self.process/"environ").write_bytes(b"")

    def test_idle_configuration_precedence(self):
        (self.app/"appsettings.json").write_text('{"HomeWatch":{"DataPath":"base"}, // comment\n"Url":"http://example",}')
        (self.app/"appsettings.Production.json").write_text('{"HomeWatch":{"DataPath":"override"}}')
        def result(*args): return configured(self.process,self.app,args,self.out)
        self.assertEqual(result(),self.app/"override/homewatch3.db")
        (self.process/"environ").write_bytes(b"HomeWatch__DataPath=env\0")
        self.assertEqual(result(),self.app/"env/homewatch3.db")
        self.assertEqual(result("--HomeWatch:DataPath","cli"),self.app/"cli/homewatch3.db")
        (self.process/"environ").write_bytes(b"HomeWatch__DataPath=\0")
        self.assertEqual(result(),self.out/"data/homewatch3.db")

    @unittest.skipUnless(os.name=="posix","Linux procfs links")
    def test_launcher_and_idle_worker(self):
        proc=self.root/"proc"
        launcher=self.root/"launcher";launcher.touch()
        host=self.out/"HomeWatch3";host.touch()
        for pid,exe,children in [(100,launcher,"101"),(101,host,"")]:
            task=proc/str(pid)
            (task/"fd").mkdir(parents=True)
            (task/"task"/str(pid)).mkdir(parents=True)
            (task/"task"/str(pid)/"children").write_text(children)
            (task/"exe").symlink_to(exe)
            (task/"cwd").symlink_to(self.app)
            (task/"cmdline").write_bytes(os.fsencode(exe)+b"\0")
            (task/"environ").write_bytes(b"")
            (task/"maps").touch()
        data=self.root/"actual data";data.mkdir()
        dbpath=data/"homewatch3.db"
        (self.app/"appsettings.json").write_text(json.dumps({"HomeWatch":{"DataPath":str(data)}}))
        with self.assertRaisesRegex(ValueError,"existing homewatch3.db"):
            discover(100,self.app,proc)
        self.assertFalse(dbpath.exists())
        with closing(sqlite3.connect(dbpath)) as db:
            db.execute("CREATE TABLE Devices(id)")
            with self.assertRaisesRegex(ValueError,"core tables"):
                discover(100,self.app,proc)
            db.execute("CREATE TABLE TrafficEvents(id)")
            db.execute("CREATE TABLE Alerts(id)")
        self.assertEqual(discover(100,self.app,proc),data)
        (proc/"100/task/100/children").unlink()
        (proc/"101/status").write_text("PPid:\t100\n")
        (proc/"101/fd/5").symlink_to(dbpath)
        (self.app/"appsettings.json").unlink()
        self.assertEqual(discover(100,self.app,proc),data)


if __name__=="__main__":
    unittest.main()
