"""Exercise the activation transaction with real runtime files and a real SQLite backup."""
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
import nas_activation as nas
from backup import copy_verified


class ActivationTests(unittest.TestCase):
    def exercise(self,failure):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            data=root/"state";runtime=root/"publish";stage=root/"stage"
            backup=data/"backups"/"upgrade"
            for path in (runtime,stage,backup): path.mkdir(parents=True)
            core=data/"homewatch3.db"
            with closing(sqlite3.connect(core)) as db:
                for name in ("Devices","TrafficEvents","Alerts"): db.execute("CREATE TABLE "+name+"(id)")
            original=core.read_bytes()
            record=copy_verified(core,backup/core.name)
            (backup/"manifest.json").write_text(json.dumps([record]))
            plan={"data":str(data),"runtime":str(runtime),"content":str(runtime),"database_id":[1,2]}
            for file in ("HomeWatch3.dll","HomeWatch3.runtimeconfig.json","HomeWatch3.deps.json"):
                (stage/file).write_text("new")
                (runtime/file).write_text("old")
            (runtime/"appsettings.json").write_text("operator")
            (stage/"new.dll").write_text("added")
            (root/"static-files.json").write_text("[]")
            new_version=("4.0.0-alpha.2",True);old_version=("4.0.0-alpha.1",True)
            calls=[]
            def live():
                if failure=="unit" and (runtime/"HomeWatch3.dll").read_text()=="new":
                    raise ValueError("unit changed")
                if failure=="database" and calls:
                    return dict(plan,database_id=[9,9])
                if failure=="offline" and calls: raise ValueError("inactive unit")
                return plan
            def restart():
                calls.append("restart")
                if failure in ("restart","offline") and len(calls)==1: raise OSError("restart failed")
            def health():
                if not calls or len(calls)>1: return old_version
                if failure=="status": raise OSError("status failed")
                return new_version
            with patch.object(nas,"live",live),patch.object(nas,"restart",restart),patch.object(nas,"health",health),patch.object(nas.time,"sleep"):
                with self.assertRaises((RuntimeError,ValueError,OSError)): nas.activate(plan,stage,backup)
            self.assertEqual((runtime/"HomeWatch3.dll").read_text(),"old")
            self.assertFalse((runtime/"new.dll").exists())
            self.assertEqual((runtime/"appsettings.json").read_text(),"operator")
            self.assertEqual(core.read_bytes(),original)
            self.assertEqual(len(calls),{"unit":0,"restart":2,"database":1,"status":2,"offline":1}[failure])

    def test_activation_failures(self):
        for case in ("unit","restart","database","status","offline"):
            with self.subTest(case=case): self.exercise(case)


if __name__=="__main__": unittest.main()
