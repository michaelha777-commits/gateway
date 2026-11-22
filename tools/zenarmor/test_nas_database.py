from contextlib import closing
from pathlib import Path
import json
import os
import sqlite3
import tempfile
import unittest
from nas_database import discover, configured, installation


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
        runtime=self.root/"installed publish"
        runtime.mkdir()
        dll=runtime/"HomeWatch3.dll";dll.touch()
        (runtime/"HomeWatch3.deps.json").write_text('{"libraries":{"HomeWatch3/1.0.0":{"type":"project"}}}')
        host=runtime/"HomeWatch3";host.touch()
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
            (task/"stat").write_text(str(pid)+" (service worker) "+" ".join(["0"]*20))
        import time
        (proc/"stat").write_text("btime %d" % (time.time()+60))
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
        with self.assertRaisesRegex(ValueError,"no open homewatch3.db"):
            installation(100,proc,require_open=True)
        (proc/"100/task/100/children").unlink()
        (proc/"101/status").write_text("PPid:\t100\n")
        (proc/"101/fd/5").symlink_to(dbpath)
        self.assertEqual(discover(100,self.app,proc),data)
        self.assertEqual(installation(100,proc,require_open=True)["database_id"],[dbpath.stat().st_dev,dbpath.stat().st_ino])
        # A framework-dependent launcher resolves its relative DLL from its own cwd.
        dotnet=self.root/"dotnet";dotnet.touch()
        child=proc/"101"
        (child/"exe").unlink()
        (child/"exe").symlink_to(dotnet)
        (child/"cmdline").write_bytes(b"dotnet\0exec\0../installed publish/HomeWatch3.dll\0")
        (child/"fd/5").unlink()
        plan=installation(100,proc)
        self.assertEqual(plan["runtime"],str(runtime))
        self.assertEqual(plan["content"],str(self.app))
        self.assertEqual(plan["data"],str(data))
        # The README sample directory is only authoritative when service configuration selects it.
        other=self.root/"unrelated"
        other.mkdir()
        import shutil
        shutil.copy2(dbpath,other/"homewatch3.db")
        (child/"fd/6").symlink_to(other/"homewatch3.db")
        with self.assertRaisesRegex(ValueError,"disagrees"):
            installation(100,proc)
        (child/"fd/6").unlink()
        (proc/"stat").write_text("btime 1")
        (self.app/"appsettings.json").write_text(json.dumps({"HomeWatch":{"DataPath":str(other)}}))
        with self.assertRaisesRegex(ValueError,"Startup configuration"):
            installation(100,proc)
        (proc/"stat").write_text("btime %d" % (time.time()+60))
        from unittest.mock import patch
        with patch("nas_database.arguments_env",side_effect=PermissionError("unreadable environment")):
            with self.assertRaises(PermissionError): installation(100,proc)
        (self.app/"appsettings.json").unlink()
        default=runtime/"data";default.mkdir()
        shutil.copy2(dbpath,default/"homewatch3.db")
        self.assertEqual(discover(100,self.app,proc),default)
        # Two legitimate workers in a single unit are ambiguous even with valid databases.
        (proc/"100/exe").unlink();(proc/"100/exe").symlink_to(host)
        with self.assertRaisesRegex(ValueError,"found 2"):
            installation(100,proc)


if __name__=="__main__":
    unittest.main()
